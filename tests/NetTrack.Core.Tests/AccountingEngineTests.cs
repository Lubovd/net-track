using NetTrack.Core.Accounting;
using NetTrack.Core.Mihomo;
using Xunit;

namespace NetTrack.Core.Tests;

public sealed class AccountingEngineTests
{
    private static readonly PipeServerIdentity DefaultServer = new("verge-mihomo-production-1", 4321, 99999, "v1.19.33");

    private static ProxyCatalog BuildCatalog(long fetchedAtMs, params ProxyMemberInfo[] extra)
    {
        var members = new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal)
        {
            ["代理"] = new("代理", "Selector", true, ""),
            ["自动选择"] = new("自动选择", "URLTest", true, ""),
            ["故障转移"] = new("故障转移", "Fallback", true, ""),
            ["均衡"] = new("均衡", "LoadBalance", true, ""),
            ["DIRECT"] = new("DIRECT", "Direct", true, ""),
            ["COMPATIBLE"] = new("COMPATIBLE", "Compatible", true, ""),
            ["REJECT"] = new("REJECT", "Reject", true, ""),
            ["REJECT-DROP"] = new("REJECT-DROP", "RejectDrop", true, ""),
            ["PASS"] = new("PASS", "Pass", true, ""),
            ["PASS-RULE"] = new("PASS-RULE", "PassRule", true, ""),
            ["RELAY-GRP"] = new("RELAY-GRP", "Relay", true, ""),
            ["HK-SS"] = new("HK-SS", "Shadowsocks", true, "")
        };

        foreach (var m in extra)
        {
            members[m.Name] = m;
        }

        return new ProxyCatalog(fetchedAtMs, members);
    }

    [Fact]
    public void ProxyClassifier_AllSupportedProtocolsAndUnconfirmedBoundaries()
    {
        var extra = ProxyClassifier.ProxyOutboundTypes
            .Select(t => new ProxyMemberInfo($"Node-{t}", t, true, ""))
            .Concat([
                new ProxyMemberInfo("Chained-SS", "Shadowsocks", true, "HK-SS"),
                new ProxyMemberInfo("MissingDialer-SS", "Shadowsocks", false, null),
                new ProxyMemberInfo("UnknownType-Node", "SomeFutureType", true, "")
            ])
            .ToArray();

        var catalog = BuildCatalog(1000, extra);

        // 15 种代理协议全部命中 Proxy
        foreach (var proto in ProxyClassifier.ProxyOutboundTypes)
        {
            var res = ProxyClassifier.Classify([$"Node-{proto}", "代理"], catalog);
            Assert.Equal(ConnectionPathKind.Proxy, res.Kind);
            Assert.Equal($"Node-{proto}", res.NodeName);
        }

        // 同名「代理」策略组下走直连/拒绝
        Assert.Equal(ConnectionPathKind.Direct, ProxyClassifier.Classify(["DIRECT", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Direct, ProxyClassifier.Classify(["COMPATIBLE", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Reject, ProxyClassifier.Classify(["REJECT", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Reject, ProxyClassifier.Classify(["REJECT-DROP", "代理"], catalog).Kind);

        // 复合/未知/异常出站全部归 Unconfirmed
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify([], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["MissingNode", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["HK-SS", "RELAY-GRP"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["HK-SS", "Node-Vmess", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["Chained-SS", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["MissingDialer-SS", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["PASS", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["PASS-RULE", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["UnknownType-Node", "代理"], catalog).Kind);
    }

    [Fact]
    public void PreStartupReloadCounterexample_ExistingConnectionStaysUnconfirmedAndNeverPollutesProxyTotal()
    {
        // 反例：连接在 net-track 启动前创建（原本走直连），启动前同名节点 NodeA 已重载为 Shadowsocks
        // 启动后两次加载到的节点表都一致显示 NodeA = Shadowsocks
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 10, TimeSpan.Zero));
        var engine = new AccountingEngine(5, clock);

        var catInit = BuildCatalog(clock.UtcNowMs, new ProxyMemberInfo("NodeA", "Shadowsocks", true, ""));
        engine.InitializeSession(catInit);

        // 旧连接建立于启动前 5 分钟
        var oldStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 300_000).ToString("O");

        clock.Advance(1000);
        var snap1 = new MihomoConnectionsSnapshot(
            1000, 2000,
            [new MihomoConnectionRecord("c-old", 1000, 2000, oldStart, ["NodeA", "代理"], "example.com", null, @"C:\App\app.exe", null)],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var cat1 = BuildCatalog(clock.UtcNowMs + 5, new ProxyMemberInfo("NodeA", "Shadowsocks", true, ""));
        var f1 = engine.ProcessSnapshot(snap1, cat1, false);
        Assert.True(f1.IsBaselineOnly);
        Assert.Empty(f1.ProxyDeltas);

        // 第二帧产生新增字节：因为 start < T_initial_catalog_recv，必须继续归入未确认诊断，代理总量为 0
        clock.Advance(1000);
        var snap2 = new MihomoConnectionsSnapshot(
            1500, 2800,
            [new MihomoConnectionRecord("c-old", 1500, 2800, oldStart, ["NodeA", "代理"], "example.com", null, @"C:\App\app.exe", null)],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var f2 = engine.ProcessSnapshot(snap2, null, false);
        Assert.False(f2.IsBaselineOnly);
        Assert.Empty(f2.ProxyDeltas);
        Assert.NotNull(f2.DiagnosticDelta);
        Assert.Equal(500, f2.DiagnosticDelta!.Value.UnconfirmedUploadBytes);
        Assert.Equal(800, f2.DiagnosticDelta!.Value.UnconfirmedDownloadBytes);
    }

    [Fact]
    public void MidFrameReloadBracketing_KeepsCrossReloadConnectionUnconfirmed_ConfirmsPostReloadConnection()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var engine = new AccountingEngine(5, clock);

        // 启动时 NodeA 是 Direct
        var catDirect = BuildCatalog(clock.UtcNowMs, new ProxyMemberInfo("NodeA", "Direct", true, ""));
        engine.InitializeSession(catDirect);

        // 第 1 帧空连接建立基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        // 第 2 帧：读取 /connections 看到 c-cross（建立于重载前），紧接着重载发生，后置节点表显示 NodeA = Shadowsocks
        clock.Advance(1000);
        var crossStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap2 = new MihomoConnectionsSnapshot(
            300, 400,
            [new MihomoConnectionRecord("c-cross", 300, 400, crossStart, ["NodeA", "代理"], "a.com", null, "a.exe", null)],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var catReloaded = BuildCatalog(clock.UtcNowMs + 20, new ProxyMemberInfo("NodeA", "Shadowsocks", true, ""));
        var f2 = engine.ProcessSnapshot(snap2, catReloaded, false);

        Assert.Empty(f2.ProxyDeltas);
        Assert.Equal(300, f2.DiagnosticDelta!.Value.UnconfirmedUploadBytes);

        // 第 3 帧：c-cross 继续传输 + 重载完成后建立的新连接 c-new 出现，两份节点表一致显示 Shadowsocks
        clock.Advance(1000);
        var newStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap3 = new MihomoConnectionsSnapshot(
            900, 1200,
            [
                new MihomoConnectionRecord("c-cross", 500, 600, crossStart, ["NodeA", "代理"], "a.com", null, "a.exe", null),
                new MihomoConnectionRecord("c-new", 400, 600, newStart, ["NodeA", "代理"], "b.com", null, "b.exe", null)
            ],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer);

        var catStable = BuildCatalog(clock.UtcNowMs + 10, new ProxyMemberInfo("NodeA", "Shadowsocks", true, ""));
        var f3 = engine.ProcessSnapshot(snap3, catStable, false);

        // c-cross 仍维持 Unconfirmed（增量 200/200），只有 c-new 入代理账本（400/600）
        Assert.Single(f3.ProxyDeltas);
        var proxyEntry = f3.ProxyDeltas.Single();
        Assert.Equal("b.exe", proxyEntry.Key.App);
        Assert.Equal("b.com", proxyEntry.Key.Domain);
        Assert.Equal(400, proxyEntry.Value.UploadBytes);
        Assert.Equal(600, proxyEntry.Value.DownloadBytes);
        Assert.Equal(200, f3.DiagnosticDelta!.Value.UnconfirmedUploadBytes);
        Assert.Equal(200, f3.DiagnosticDelta!.Value.UnconfirmedDownloadBytes);
    }

    [Fact]
    public void TemporaryCatalogFailure_PromotesOnlyAfterConsistentRecovery_CountsPostConfirmationDeltasOnly()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var engine = new AccountingEngine(5, clock);
        var cat0 = BuildCatalog(clock.UtcNowMs);
        engine.InitializeSession(cat0);

        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        // 第 2 帧：新连接出现但本次节点表读取失败 -> 暂归 PendingCatalogCheck (Unconfirmed)
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var f2 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                100, 200,
                [new MihomoConnectionRecord("c1", 100, 200, connStart, ["HK-SS", "代理"], "x.com", null, "x.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, catalogFetchFailed: true);

        Assert.Empty(f2.ProxyDeltas);
        Assert.Equal(100, f2.DiagnosticDelta!.Value.UnconfirmedUploadBytes);

        // 第 3 帧：节点表恢复且与失败前完全一致 -> 改判确认，但本帧确认前的增量（+50）仍归未确认
        clock.Advance(1000);
        var catRecovered = BuildCatalog(clock.UtcNowMs + 5);
        var f3 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                150, 250,
                [new MihomoConnectionRecord("c1", 150, 250, connStart, ["HK-SS", "代理"], "x.com", null, "x.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            catRecovered, catalogFetchFailed: false);

        Assert.Empty(f3.ProxyDeltas);
        Assert.Equal(50, f3.DiagnosticDelta!.Value.UnconfirmedUploadBytes);

        // 第 4 帧：确认后的新增量（+70/+80）计入代理账本
        clock.Advance(1000);
        var f4 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                220, 330,
                [new MihomoConnectionRecord("c1", 220, 330, connStart, ["HK-SS", "代理"], "x.com", null, "x.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, catalogFetchFailed: false);

        var p = f4.ProxyDeltas.Single().Value;
        Assert.Equal(70, p.UploadBytes);
        Assert.Equal(80, p.DownloadBytes);
    }

    [Fact]
    public void StartFourBoundaries_AndGap5000Vs5001Ms_AndClockRollback()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var engine = new AccountingEngine(5, clock);
        engine.InitializeSession(BuildCatalog(clock.UtcNowMs));

        clock.Advance(1000);
        var prevReqStart = clock.UtcNowMs - 10;
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], prevReqStart, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);

        // 恰好 5000ms：仍连续；测试 start 四边界
        clock.Advance(5000);
        var curRecv = clock.UtcNowMs;
        var sEqualPrevReq = DateTimeOffset.FromUnixTimeMilliseconds(prevReqStart).ToString("O");
        var sBeforePrevReq = DateTimeOffset.FromUnixTimeMilliseconds(prevReqStart - 1).ToString("O");
        var sAfterCurRecv = DateTimeOffset.FromUnixTimeMilliseconds(curRecv + 1).ToString("O");

        var f2 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                1000, 1000,
                [
                    new MihomoConnectionRecord("eq", 100, 200, sEqualPrevReq, ["HK-SS", "代理"], "eq.com", null, "a.exe", null),
                    new MihomoConnectionRecord("before", 300, 300, sBeforePrevReq, ["HK-SS", "代理"], "before.com", null, "a.exe", null),
                    new MihomoConnectionRecord("after", 300, 300, sAfterCurRecv, ["HK-SS", "代理"], "after.com", null, "a.exe", null),
                    new MihomoConnectionRecord("missing", 300, 200, null, ["HK-SS", "代理"], "miss.com", null, "a.exe", null)
                ],
                curRecv - 5, curRecv, clock.GetTimestamp(), DefaultServer),
            BuildCatalog(curRecv + 5), false);

        Assert.False(f2.IsBaselineOnly);
        Assert.Single(f2.ProxyDeltas);
        Assert.Equal("eq.com", f2.ProxyDeltas.Single().Key.Domain);
        Assert.Equal(100, f2.ProxyDeltas.Single().Value.UploadBytes);
        // 其余 3 条连接首次只建基线，全局差额保留
        Assert.Equal(900, f2.DiagnosticDelta!.Value.ReconcileDiffUploadBytes);

        // 5001ms：严格大于 5s 阈值 -> 重建基线
        clock.Advance(5001);
        var f3 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(1200, 1200, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);
        Assert.True(f3.IsBaselineOnly);
        Assert.True(f3.IsGapBoundary);

        // 时钟回拨 -> 切断覆盖并记录时钟异常
        clock.AdvanceSeparate(wallMs: -2000, monoMs: 1000);
        var f4 = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(1300, 1300, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), DefaultServer),
            null, false);
        Assert.True(f4.IsBaselineOnly);
        Assert.True(f4.IsClockAnomaly);
        Assert.Equal(1, f4.DiagnosticDelta!.Value.ClockAnomalyCount);
    }

    [Fact]
    public void MetadataNormalizer_DomainAndAppRules()
    {
        Assert.Equal("files.example.com", MetadataNormalizer.NormalizeDomain("  Files.Example.com. ", "other.com"));
        Assert.Equal("sniff.example.com", MetadataNormalizer.NormalizeDomain("1.2.3.4", "Sniff.Example.COM."));
        Assert.Equal("未知域名", MetadataNormalizer.NormalizeDomain("[2001:db8::1]", "192.168.1.1"));
        Assert.Equal("chrome.exe", MetadataNormalizer.NormalizeApp(@"C:\Apps\Chrome.EXE", "fallback.exe"));
        Assert.Equal("curl.exe", MetadataNormalizer.NormalizeApp("   ", "Curl.EXE"));
        Assert.Equal("未知应用", MetadataNormalizer.NormalizeApp(null, "  "));
    }
}
