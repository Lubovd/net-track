using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 针对所选节点观察报表口径的 4 个已复现问题（跨 Provider 同名合并、已确认直连入观察、未启用观察显示完整覆盖、清空名单扩大范围）的隔离回归测试。
/// </summary>
public sealed class ObservedReportRegressionTests
{
    private static readonly PipeServerIdentity DefaultServer = new("verge-mihomo-production-1", 9001, 1000, "v1.19.33");

    [Fact]
    public void ObservedBug1_SameNodeNameAcrossDifferentProviders_MustNotMergeAndCanSelectSingleProvider()
    {
        using var dir = new TempTestDir("obs-bug1-provider-split");
        var paths = new NetTrackPaths(dir.Path);
        var baseTime = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset);
        var clock = new FakeClock(baseTime);

        // 1. 场景 A：仅选中 paid-sub::HK-01，必须排除 free-sub::HK-01
        var optsSingle = new CollectorOptions { SelectedNodes = ["paid-sub::HK-01"] };
        var engineSingle = new AccountingEngine(5, clock, selectedNodes: optsSingle.SelectedNodes);
        engineSingle.InitializeSession(ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs));

        clock.Advance(1000);
        engineSingle.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        clock.Advance(1000);
        var s = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap = new MihomoConnectionsSnapshot(
            0, 12288,
            [
                new MihomoConnectionRecord("c-paid", 0, 4096, s, ["HK-01", "代理"], "a.com", null, "app.exe", null, ["paid-sub"]),
                new MihomoConnectionRecord("c-free", 0, 8192, s, ["HK-01", "代理"], "a.com", null, "app.exe", null, ["free-sub"])
            ],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var frameSingle = engineSingle.ProcessSnapshot(snap, ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs), false);
        Assert.Single(frameSingle.ObservedSelectedNodeDeltas);
        Assert.Equal(4096, frameSingle.ObservedSelectedNodeDeltas.Single().Value.DownloadBytes);
        Assert.Equal("paid-sub", frameSingle.ObservedSelectedNodeDeltas.Single().Key.Provider);

        // 2. 场景 B：同时观察两个 Provider 下的同名节点，报表与 CSV 均不得合并成一条 12288 B
        var optsBoth = new CollectorOptions { SelectedNodes = ["paid-sub::HK-01", "free-sub::HK-01"] };
        var engineBoth = new AccountingEngine(5, clock, selectedNodes: optsBoth.SelectedNodes);
        engineBoth.InitializeSession(ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs));

        clock.Advance(1000);
        engineBoth.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        clock.Advance(1000);
        var s2 = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap2 = new MihomoConnectionsSnapshot(
            0, 12288,
            [
                new MihomoConnectionRecord("c-paid", 0, 4096, s2, ["HK-01", "代理"], "a.com", null, "app.exe", null, ["paid-sub"]),
                new MihomoConnectionRecord("c-free", 0, 8192, s2, ["HK-01", "代理"], "a.com", null, "app.exe", null, ["free-sub"])
            ],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var frameBoth = engineBoth.ProcessSnapshot(snap2, ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs), false);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastStoppedUtcMs = clock.UtcNowMs };
            batch.AddFrameResult(frameBoth);
            store.CommitBatch(batch, clock.UtcNowMs);
        }

        var query = new ReportQueryService(paths, clock);
        var dateBj = BeijingTime.GetBeijingDate(clock.UtcNowMs);
        var nodeReport = query.QueryDailyReport(
            new ReportQueryRequest(dateBj, ReportGroupBy.Node, ReportSortBy.Download, Scope: ReportScope.SelectedNodesObserved),
            optsBoth);

        // 必须拆分为两条独立节点记录（free-sub::HK-01=8192，paid-sub::HK-01=4096），绝不合并为 12288
        Assert.Equal(2, nodeReport.Items.Count);
        Assert.Equal("free-sub::HK-01", nodeReport.Items[0].Key);
        Assert.Equal(8192, nodeReport.Items[0].DownloadBytes);
        Assert.Equal("paid-sub::HK-01", nodeReport.Items[1].Key);
        Assert.Equal(4096, nodeReport.Items[1].DownloadBytes);

        var csvRows = query.QueryExportRows(dateBj, null, ReportScope.SelectedNodesObserved, optsBoth.SelectedNodes);
        Assert.Equal(2, csvRows.Count);
        Assert.Contains(csvRows, r => r.Node == "paid-sub::HK-01" && r.DownloadBytes == 4096);
        Assert.Contains(csvRows, r => r.Node == "free-sub::HK-01" && r.DownloadBytes == 8192);
    }

    [Fact]
    public void ObservedBug2_ConfirmedDirectOrRejectConnection_MustNotGenerateObservedRecord()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset));
        var engine = new AccountingEngine(5, clock, selectedNodes: ["CustomDirectNode"]);

        // 构造已核验节点表，明确标出 CustomDirectNode 实际类型为 Direct
        var verifiedCatalog = new ProxyCatalog(
            clock.UtcNowMs,
            new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal)
            {
                ["CustomDirectNode"] = new("CustomDirectNode", "Direct", true, ""),
                ["代理"] = new("代理", "Selector", true, "")
            },
            isKernelVersionVerified: true);

        engine.InitializeSession(verifiedCatalog);

        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap = new MihomoConnectionsSnapshot(
            4096, 0,
            [new MihomoConnectionRecord("c-dir", 4096, 0, connStart, ["CustomDirectNode", "代理"], "music.qq.com", null, "qqmusic.exe", null)],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var frame = engine.ProcessSnapshot(snap, verifiedCatalog, false);

        // 已明确判定为 Direct，严禁生成任何观察记录（不落盘直连应用名、域名和字节）
        Assert.Empty(frame.ProxyDeltas);
        Assert.Empty(frame.ObservedSelectedNodeDeltas);
    }

    [Fact]
    public void ObservedBug3_HourBeforeObservationEnabled_MustShowNoDataInsteadOfCompleteZero()
    {
        using var dir = new TempTestDir("obs-bug3-coverage");
        var paths = new NetTrackPaths(dir.Path);

        var h10Start = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var h11Start = new DateTimeOffset(2026, 11, 23, 11, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var h12Start = new DateTimeOffset(2026, 11, 23, 12, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();

            // 10:00–11:00：管道在正常采样（通用覆盖 3600s），但未启用任何所选节点观察
            var b1 = new PendingBatch(1) { CollectorStatus = "running", LastSampleUtcMs = h11Start };
            b1.CoverageSegments.Add(new CoverageSegment(h10Start, h10Start, h11Start));
            store.CommitBatch(b1, h11Start);

            // 11:00–12:00：启用了对 paid-sub::HK-01 的观察，且该小时无流量
            var b2 = new PendingBatch(2) { CollectorStatus = "stopped", LastSampleUtcMs = h12Start, LastStoppedUtcMs = h12Start };
            b2.CoverageSegments.Add(new CoverageSegment(h11Start, h11Start, h12Start));
            // 通过 AccountingEngine 正常产生观察覆盖（或写入观察覆盖段）
            var clockH11 = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h11Start));
            var eng = new AccountingEngine(5, clockH11, selectedNodes: ["paid-sub::HK-01"]);
            eng.InitializeSession(ProxyCatalog.CreateEmptyUnverified(h11Start));
            eng.ProcessSnapshot(
                new MihomoConnectionsSnapshot(0, 0, [], h11Start, h11Start, clockH11.GetTimestamp(), DefaultServer),
                null, false);
            clockH11.Advance(3000);
            var fH11 = eng.ProcessSnapshot(
                new MihomoConnectionsSnapshot(0, 0, [], h11Start + 2995, h11Start + 3000, clockH11.GetTimestamp(), DefaultServer),
                null, false);
            b2.AddFrameResult(fH11);
            store.CommitBatch(b2, h12Start);
        }

        var queryClock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h12Start));
        var query = new ReportQueryService(paths, queryClock);
        var opts = new CollectorOptions { SelectedNodes = ["paid-sub::HK-01"] };
        var report = query.QueryDailyReport(
            new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour, Scope: ReportScope.SelectedNodesObserved),
            opts);

        // 10 点未启用观察，绝不能显示为 complete 3600s
        Assert.Equal("no_data", report.Items[10].CoverageStatus);
        Assert.Equal(0, report.Items[10].CoveredMs);

        // 11 点启用了观察（3000ms），能明确区分已观察时段
        Assert.Equal(3000, report.Items[11].CoveredMs);
        Assert.NotEqual("no_data", report.Items[11].CoverageStatus);
    }

    [Fact]
    public void ObservedBug4_ClearingSelectedNodes_MustReturnEmptyInsteadOfExpandingToAllHistoricalNodes()
    {
        using var dir = new TempTestDir("obs-bug4-empty-selection");
        var paths = new NetTrackPaths(dir.Path);
        var t = new DateTimeOffset(2026, 11, 23, 10, 30, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var hourStart = BeijingTime.GetHourStartUtcMs(t);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var b = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = t, LastStoppedUtcMs = t };
            b.ObservedNodeDeltas[new ObservedNodeUsageKey(hourStart, "app.exe", "a.com", "NodeA", "sub1")] = new BytePair(1000, 2000);
            b.ObservedNodeDeltas[new ObservedNodeUsageKey(hourStart, "app.exe", "b.com", "NodeB", "sub2")] = new BytePair(3000, 4000);
            store.CommitBatch(b, t);
        }

        var query = new ReportQueryService(paths, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(t)));
        var dateBj = new DateOnly(2026, 11, 23);

        // 选中 NodeA 时仅返回 NodeA
        var onlyA = query.QueryDailyReport(
            new ReportQueryRequest(dateBj, ReportGroupBy.Node, Scope: ReportScope.SelectedNodesObserved),
            new CollectorOptions { SelectedNodes = ["sub1::NodeA"] });
        Assert.Equal(3000, onlyA.Summary.TotalBytes);

        // 清空所选名单后，报表与导出必须返回空（0 B），绝不能反而扩大到显示 NodeA + NodeB
        var emptyOpts = new CollectorOptions { SelectedNodes = [] };
        var cleared = query.QueryDailyReport(
            new ReportQueryRequest(dateBj, ReportGroupBy.Node, Scope: ReportScope.SelectedNodesObserved),
            emptyOpts);
        Assert.Equal(0, cleared.Summary.TotalBytes);
        Assert.Empty(cleared.Items);

        var clearedCsv = query.QueryExportRows(dateBj, null, ReportScope.SelectedNodesObserved, emptyOpts.SelectedNodes);
        Assert.Empty(clearedCsv);
    }
}
