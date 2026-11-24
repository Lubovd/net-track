using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 针对试运行二轮复查中复现的 4 个问题（Point 1 / 3 / 5，Point 4 在 DesktopStaTests 中复现）的隔离回归测试。
/// </summary>
public sealed class TrialReviewSecondRegressionTests
{
    /// <summary>
    /// 复现缺陷 1（P1）：
    /// 磁盘 YAML 声明了同名管道且修改时间早于内核启动，但内核实际从另一份配置启动且出站为直连。
    /// 仅凭「管道名相同 + 文件修改早于进程启动」绝不能视为核验通过，4096 B 不得进入严格代理账本。
    /// </summary>
    [Fact]
    public void Trial2Bug1_SamePipeOldYaml_DifferentConfigLoadedByKernel_MustNotMisclassifyDirectAsProxy()
    {
        using var dir = new TempTestDir("trial2-bug1-same-pipe-old-yaml");
        var yamlPath = Path.Combine(dir.Path, "clash-verge.yaml");
        var kernelStart = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        var pipeName = "verge-mihomo-production-same";
        var serverId = new PipeServerIdentity(pipeName, 5123, kernelStart.Ticks, "v1.19.33");
        var clock = new FakeClock(kernelStart.AddSeconds(20));

        // 磁盘旧 YAML：同名管道、修改时间早于进程启动，但内核实际加载了另一份配置（出站为直连）
        File.WriteAllText(yamlPath, $$"""
            external-controller-pipe: \\.\pipe\{{pipeName}}
            proxies:
              - name: "NodeA"
                type: vless
            proxy-groups:
              - name: "Proxy"
                type: select
            """);
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddMinutes(-5).UtcDateTime);

        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var engine = new AccountingEngine(5, clock);
        engine.InitializeSession(source.LoadCatalog());

        // 第 1 帧基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：实际连接走直连 4096 B
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                0, 4096,
                [new MihomoConnectionRecord("c-direct", 0, 4096, connStart, ["NodeA", "Proxy"], "a.com", null, "app.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            source.LoadCatalog(), false);

        Assert.False(frame.AttributionVerified);
        Assert.Empty(frame.ProxyDeltas);
        Assert.NotNull(frame.DiagnosticDelta);
        Assert.Equal(4096, frame.DiagnosticDelta!.Value.UnconfirmedDownloadBytes);
    }

    /// <summary>
    /// 复现缺陷 3（P2）：
    /// 严格代理账本中不同供应商（subA 与 subB）下均有同名节点 HK-01（4096 B 与 8192 B）。
    /// 按下拉选项的筛选键分别查询与导出时，必须各自返回 4096 B 与 8192 B，绝不能混算为 12288 B。
    /// </summary>
    [Fact]
    public void Trial2Bug3_ConfirmedProxyScope_SameNodeAcrossTwoProviders_MustFilterSeparately()
    {
        using var dir = new TempTestDir("trial2-bug3-confirmed-provider-split");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 11, 23);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));
        var serverId = new PipeServerIdentity("verge-mihomo-test", 7777, h0 - 1000, "v1.19.33");

        var verifiedCatalog = new ProxyCatalog(
            h0,
            new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal)
            {
                ["HK-01"] = new("HK-01", "Vless", true, string.Empty),
                ["Proxy"] = new("Proxy", "Selector", true, string.Empty)
            },
            isKernelVersionVerified: true);

        var engine = new AccountingEngine(5, clock);
        engine.InitializeSession(verifiedCatalog);

        var tBase = new DateTimeOffset(2026, 11, 23, 0, 1, 0, BeijingTime.Offset);
        clock.Advance(tBase.ToUnixTimeMilliseconds() - clock.UtcNowMs);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        clock.Advance(1000);
        var s = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 50).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                0, 12288,
                [
                    new MihomoConnectionRecord("c1", 0, 4096, s, ["HK-01", "Proxy"], "a.com", null, "app.exe", null, ["subA"]),
                    new MihomoConnectionRecord("c2", 0, 8192, s, ["HK-01", "Proxy"], "b.com", null, "app.exe", null, ["subB"])
                ],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            verifiedCatalog, false);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastStoppedUtcMs = clock.UtcNowMs };
            batch.AddFrameResult(frame);
            store.CommitBatch(batch, clock.UtcNowMs);
        }

        var query = new ReportQueryService(paths, clock);
        var discovered = query.QueryDiscoveredNodes(day);
        var optA = Assert.Single(discovered, d => d.Provider == "subA" && d.Node == "HK-01");
        var optB = Assert.Single(discovered, d => d.Provider == "subB" && d.Node == "HK-01");

        var reportA = query.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, Filters: new ReportFilters(Node: optA.ConfirmedFilterKey), Scope: ReportScope.ConfirmedProxy),
            new CollectorOptions());
        var reportB = query.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, Filters: new ReportFilters(Node: optB.ConfirmedFilterKey), Scope: ReportScope.ConfirmedProxy),
            new CollectorOptions());

        Assert.Equal(4096, reportA.Summary.DownloadBytes);
        Assert.Equal(8192, reportB.Summary.DownloadBytes);

        var csvA = query.QueryExportRows(day, new ReportFilters(Node: optA.ConfirmedFilterKey), ReportScope.ConfirmedProxy);
        var csvB = query.QueryExportRows(day, new ReportFilters(Node: optB.ConfirmedFilterKey), ReportScope.ConfirmedProxy);
        Assert.Equal(4096, Assert.Single(csvA).DownloadBytes);
        Assert.Equal(8192, Assert.Single(csvB).DownloadBytes);
    }

    /// <summary>
    /// 复现缺陷 5（P2）：
    /// 上一份节点表已核验，但本帧配置读取失败（catalogFetchFailed = true）导致新增 4096 B 归入未确认时，
    /// 返回的 AttributionVerified 必须为 false（绝不能退回上一份已核验表返回 true）。
    /// </summary>
    [Fact]
    public void Trial2Bug5_CatalogFetchFailedOnFrame_MustSetAttributionVerifiedFalse()
    {
        var t0 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset);
        var clock = new FakeClock(t0);
        var serverId = new PipeServerIdentity("verge-mihomo-live", 8888, t0.AddMinutes(-1).Ticks, "v1.19.33");

        var verifiedCatalog = new ProxyCatalog(
            clock.UtcNowMs,
            new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal)
            {
                ["HK-01"] = new("HK-01", "Vless", true, string.Empty),
                ["Proxy"] = new("Proxy", "Selector", true, string.Empty)
            },
            isKernelVersionVerified: true);

        var engine = new AccountingEngine(5, clock);
        engine.InitializeSession(verifiedCatalog);

        // 第 1 帧基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：本帧配置读取失败（catalogAfter = null, catalogFetchFailed = true）
        clock.Advance(1000);
        var s = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 50).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                0, 4096,
                [new MihomoConnectionRecord("c-fail", 0, 4096, s, ["HK-01", "Proxy"], "a.com", null, "app.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            catalogAfter: null,
            catalogFetchFailed: true);

        Assert.Empty(frame.ProxyDeltas);
        Assert.Equal(4096, frame.DiagnosticDelta!.Value.UnconfirmedDownloadBytes);
        Assert.False(frame.AttributionVerified);
        Assert.Equal("config_read_failed", frame.AttributionReasonCode);
    }
}
