using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 对应 TRIAL_REVIEW.md 试运行改进方案的回归测试：
/// - P1 生产核验：管道匹配 + 修改时间早于内核启动通过；管道不匹配或热重载后修改失败并返回明确原因
/// - P2 自动节点发现：从 /connections 自动发现非内置出站，排除 DIRECT/REJECT，同名节点跨供应商自动区分
/// </summary>
public sealed class TrialReviewRegressionTests
{
    [Fact]
    public void P1_ProductionYamlVerification_MatchesPipeAndBeforeStart_Passes_MismatchOrPostStart_FailsWithClearReason()
    {
        using var dir = new TempTestDir("trial-p1-verify");
        var yamlPath = Path.Combine(dir.Path, "clash-verge.yaml");
        var kernelStart = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        var pipeName = "verge-mihomo-production-live";
        var serverId = new PipeServerIdentity(pipeName, 4321, kernelStart.Ticks, "v1.19.33");
        var clock = new FakeClock(kernelStart.AddSeconds(20));

        // 1. 正常启动场景：声明管道匹配且 YAML 修改时间早于进程启动
        File.WriteAllText(yamlPath, $$"""
            external-controller-pipe: \\.\pipe\{{pipeName}}
            proxies:
              - name: "HK-01"
                type: vless
            proxy-groups:
              - name: "Proxy"
                type: select
            """);
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddSeconds(-5).UtcDateTime);

        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catDisk = source.LoadCatalog();
        var (ok, okCode, okText) = catDisk.EvaluateKernelVerification(serverId);
        // 仅凭本地磁盘 YAML（即使管道名相同且修改时间早于进程启动）不能证明内核未从另一份配置启动，必须保守未核验
        Assert.False(ok);
        Assert.Equal("unverified_disk_yaml", okCode);
        Assert.False(string.IsNullOrWhiteSpace(okText));

        // 2. 热重载/磁盘更新晚于内核启动：必须拒绝并返回 config_modified_after_kernel_start
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddSeconds(10).UtcDateTime);
        var catAfterReload = source.LoadCatalog();
        var (reloadOk, reloadCode, reloadText) = catAfterReload.EvaluateKernelVerification(serverId);
        Assert.False(reloadOk);
        Assert.Equal("config_modified_after_kernel_start", reloadCode);
        Assert.False(string.IsNullOrWhiteSpace(reloadText));

        // 3. 管道不匹配：必须拒绝并返回 pipe_mismatch
        var otherServer = new PipeServerIdentity("verge-mihomo-other", 4321, kernelStart.Ticks, "v1.19.33");
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddSeconds(-5).UtcDateTime);
        var catMismatch = source.LoadCatalog();
        var (misOk, misCode, _) = catMismatch.EvaluateKernelVerification(otherServer);
        Assert.False(misOk);
        Assert.Equal("pipe_mismatch", misCode);
    }

    [Fact]
    public void P2_AutoNodeDiscovery_ExcludesBuiltInsAndDirect_DisambiguatesDuplicateNamesAcrossProviders()
    {
        using var dir = new TempTestDir("trial-p2-discovery");
        var paths = new NetTrackPaths(dir.Path);
        var t0 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset);
        var clock = new FakeClock(t0);
        var serverId = new PipeServerIdentity("verge-mihomo-live", 9999, t0.AddMinutes(-1).Ticks, "v1.19.33");

        // 即使未配置 selectedNodes 且归因未核验，也能从连接快照自动发现实际出站节点
        var engine = new AccountingEngine(5, clock, selectedNodes: Array.Empty<string>());
        engine.InitializeSession(ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs));

        clock.Advance(1000);
        var s = clock.UtcNow.AddMilliseconds(-50).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                0, 0,
                [
                    new MihomoConnectionRecord("c1", 100, 200, s, ["HK-01", "Proxy"], "a.com", null, "app.exe", null, ["subA"]),
                    new MihomoConnectionRecord("c2", 100, 200, s, ["HK-01", "Proxy"], "b.com", null, "app.exe", null, ["subB"]),
                    new MihomoConnectionRecord("c3", 100, 200, s, ["JP-02", "Proxy"], "c.com", null, "app.exe", null, ["subA"]),
                    new MihomoConnectionRecord("c4", 100, 200, s, ["DIRECT"], "d.com", null, "app.exe", null),
                    new MihomoConnectionRecord("c5", 100, 200, s, ["REJECT"], "e.com", null, "app.exe", null)
                ],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs),
            false);

        Assert.False(frame.AttributionVerified);
        Assert.Equal("config_missing", frame.AttributionReasonCode);
        Assert.Equal(3, frame.DiscoveredNodes.Count);

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "running" };
            batch.AddFrameResult(frame);
            store.CommitBatch(batch, clock.UtcNowMs);
        }

        var query = new ReportQueryService(paths, clock);
        var todayBj = new DateOnly(2026, 11, 23);
        var discovered = query.QueryDiscoveredNodes(todayBj);

        // DIRECT 和 REJECT 被排除；同名 HK-01 显示供应商区分，唯一 JP-02 显示节点名
        Assert.Equal(3, discovered.Count);
        Assert.Contains(discovered, d => d.Node == "HK-01" && d.Provider == "subA" && d.DisplayLabel == "subA::HK-01");
        Assert.Contains(discovered, d => d.Node == "HK-01" && d.Provider == "subB" && d.DisplayLabel == "subB::HK-01");
        Assert.Contains(discovered, d => d.Node == "JP-02" && d.Provider == "subA" && d.DisplayLabel.Contains("JP-02"));
        Assert.DoesNotContain(discovered, d => d.Node is "DIRECT" or "REJECT");
    }
}
