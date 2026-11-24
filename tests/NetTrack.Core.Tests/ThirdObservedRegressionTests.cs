using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 针对第三轮审查复现的 5 个观察报表与覆盖口径问题的隔离回归测试。
/// </summary>
public sealed class ThirdObservedRegressionTests
{
    private static readonly PipeServerIdentity DefaultServer = new("verge-mihomo-production-1", 9501, 1000, "v1.19.33");

    [Fact]
    public void ThirdBug1_BareNodeCollection_FilterByProviderNode_MustRetainCoverage()
    {
        using var dir = new TempTestDir("third-bug1-bare-filter");
        var paths = new NetTrackPaths(dir.Path);
        var t0 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset);
        var clock = new FakeClock(t0);

        // 按裸节点名 HK-01 开始采集（覆盖记录以裸名 HK-01 保存）
        var opts = new CollectorOptions { SelectedNodes = ["HK-01"] };
        var engine = new AccountingEngine(5, clock, selectedNodes: opts.SelectedNodes);
        engine.InitializeSession(ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs));

        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        clock.Advance(2000);
        var s = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                0, 4096,
                [new MihomoConnectionRecord("c1", 0, 4096, s, ["HK-01", "代理"], "a.com", null, "app.exe", null, ["paid"])],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs), false);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastStoppedUtcMs = clock.UtcNowMs };
            batch.AddFrameResult(frame);
            store.CommitBatch(batch, clock.UtcNowMs);
        }

        // 从节点排行点进 paid::HK-01，字节为 4096 B，覆盖记录绝不能丢失变成 no_data
        var query = new ReportQueryService(paths, clock);
        var report = query.QueryDailyReport(
            new ReportQueryRequest(
                new DateOnly(2026, 11, 23),
                ReportGroupBy.Hour,
                Filters: new ReportFilters(Node: "paid::HK-01"),
                Scope: ReportScope.SelectedNodesObserved),
            opts);

        Assert.Equal(4096, report.Summary.DownloadBytes);
        Assert.Equal(2000, report.Items[10].CoveredMs);
        Assert.NotEqual("no_data", report.Items[10].CoverageStatus);
    }

    [Fact]
    public void ThirdBug2_MultiNodeTotalCoverage_MustUseIntersectionNotUnion()
    {
        using var dir = new TempTestDir("third-bug2-intersection");
        var paths = new NetTrackPaths(dir.Path);
        var h10 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var h1030 = h10 + 1800_000L;
        var h11 = h10 + 3600_000L;

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h11, LastStoppedUtcMs = h11 };
            // A 观察了整小时 10:00-11:00（60 分钟），B 只观察了后半小时 10:30-11:00（30 分钟）
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h10, "sub::A", h10, h11));
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h10, "sub::B", h1030, h11));
            store.CommitBatch(batch, h11);
        }

        var query = new ReportQueryService(paths, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h11)));
        var opts = new CollectorOptions { SelectedNodes = ["sub::A", "sub::B"] };
        var report = query.QueryDailyReport(
            new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour, Scope: ReportScope.SelectedNodesObserved),
            opts);

        // 多节点合计必须取交集（30 分钟覆盖 + 30 分钟空档），绝不能取并集掩盖 B 的空档
        Assert.Equal("partial_gap", report.Items[10].CoverageStatus);
        Assert.Equal(1800_000L, report.Items[10].CoveredMs);
        Assert.Equal(1800_000L, report.Items[10].GapMs);
    }

    [Fact]
    public void ThirdBug3_SavedNewSelectedNodeNotYetObservedByRunningCollector_MustNotShowPending()
    {
        using var dir = new TempTestDir("third-bug3-pending-mismatch");
        var paths = new NetTrackPaths(dir.Path);
        var t1000 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var t100002 = t1000 + 2000;

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            // 正在运行的采集器实际只观察 A，发送了 running 心跳
            var batch = new PendingBatch(1)
            {
                CollectorStatus = "running",
                CurrentRunStartUtcMs = t1000,
                LastSampleUtcMs = t1000,
                ActiveObservedTargetsJson = """["sub::A"]"""
            };
            store.CommitBatch(batch, t100002);
        }

        // 用户把 settings.json 改成了 B（尚未重启采集器）
        var query = new ReportQueryService(paths, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(t100002)));
        var optsForB = new CollectorOptions { SelectedNodes = ["sub::B"] };
        var reportB = query.QueryDailyReport(
            new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour, Scope: ReportScope.SelectedNodesObserved),
            optsForB);

        // B 从未被当前采集器观察，绝不能用 A 的心跳给 B 算出 2 秒 pending
        Assert.Equal(0, reportB.Items[10].PendingMs);
        Assert.Equal("no_data", reportB.Items[10].CoverageStatus);

        // 对照：正在被观察的 A 仍正常获得 2000ms 待更新
        var reportA = query.QueryDailyReport(
            new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour, Scope: ReportScope.SelectedNodesObserved),
            new CollectorOptions { SelectedNodes = ["sub::A"] });
        Assert.Equal(2000, reportA.Items[10].PendingMs);
        Assert.Equal("pending", reportA.Items[10].CoverageStatus);
    }

    [Fact]
    public void ThirdBug4_NodeNameContainingDoubleColon_MustNotCollideWithProviderNode()
    {
        using var dir = new TempTestDir("third-bug4-colon-collision");
        var paths = new NetTrackPaths(dir.Path);
        var t = new DateTimeOffset(2026, 11, 23, 10, 10, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var h10 = BeijingTime.GetHourStartUtcMs(t);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = t, LastStoppedUtcMs = t };
            // 无 Provider、原名为 "paid::HK-01" 的内联节点，与 paid Provider 下的 "HK-01"
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h10, "app.exe", "a.com", "paid::HK-01", "")] = new BytePair(1000, 0);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h10, "app.exe", "a.com", "HK-01", "paid")] = new BytePair(2000, 0);
            store.CommitBatch(batch, t);
        }

        var query = new ReportQueryService(paths, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(t)));
        var opts = new CollectorOptions { SelectedNodes = ["::paid::HK-01", "paid::HK-01"] };
        var dateBj = new DateOnly(2026, 11, 23);

        var report = query.QueryDailyReport(
            new ReportQueryRequest(dateBj, ReportGroupBy.Node, ReportSortBy.Upload, Scope: ReportScope.SelectedNodesObserved),
            opts);

        // 两个节点必须保持独立显示键，绝不合并为一条 3000 B
        Assert.Equal(2, report.Items.Count);
        Assert.Equal("paid::HK-01", report.Items[0].Key);
        Assert.Equal(2000, report.Items[0].UploadBytes);
        Assert.Equal("::paid::HK-01", report.Items[1].Key);
        Assert.Equal(1000, report.Items[1].UploadBytes);

        var csvRows = query.QueryExportRows(dateBj, null, ReportScope.SelectedNodesObserved, opts.SelectedNodes);
        Assert.Equal(2, csvRows.Count);
        Assert.Contains(csvRows, r => r.Node == "paid::HK-01" && r.UploadBytes == 2000);
        Assert.Contains(csvRows, r => r.Node == "::paid::HK-01" && r.UploadBytes == 1000);
    }

    [Fact]
    public void ThirdBug5_CatalogRecoveryConfirmingDirect_MustNotEmitObservedRecordOnRecoveryFrame()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset));
        var engine = new AccountingEngine(5, clock, selectedNodes: ["CustomDirectNode"]);

        var verifiedDirectCatalog = new ProxyCatalog(
            clock.UtcNowMs,
            new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal)
            {
                ["CustomDirectNode"] = new("CustomDirectNode", "Direct", true, ""),
                ["代理"] = new("代理", "Selector", true, "")
            },
            isKernelVersionVerified: true);

        engine.InitializeSession(verifiedDirectCatalog);

        // 第 1 帧基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        // 第 2 帧：节点表读取临时失败，连接暂归 PendingCatalogCheck
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                50, 0,
                [new MihomoConnectionRecord("c-dir", 50, 0, connStart, ["CustomDirectNode", "代理"], "music.qq.com", null, "qqmusic.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, catalogFetchFailed: true);

        // 第 3 帧：节点表恢复并确认该连接为 Direct，产生 +100 B；这一帧严禁再生成观察记录
        clock.Advance(1000);
        var recoveredCatalog = new ProxyCatalog(
            clock.UtcNowMs + 5,
            verifiedDirectCatalog.Members,
            isKernelVersionVerified: true);

        var frame3 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                150, 0,
                [new MihomoConnectionRecord("c-dir", 150, 0, connStart, ["CustomDirectNode", "代理"], "music.qq.com", null, "qqmusic.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            recoveredCatalog, catalogFetchFailed: false);

        Assert.Empty(frame3.ProxyDeltas);
        Assert.Empty(frame3.ObservedSelectedNodeDeltas);
    }
}
