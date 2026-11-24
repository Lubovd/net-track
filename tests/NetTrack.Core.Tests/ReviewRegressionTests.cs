using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 针对审查复现的 4 个核心缺陷（未生效磁盘配置、短暂采样失败漏计、Provider dialer-proxy 覆盖、零缩进 YAML 列表）的隔离回归测试。
/// </summary>
public sealed class ReviewRegressionTests
{
    [Fact]
    public void Bug1_UnappliedDiskYaml_WrittenAfterKernelStarted_MustNotClassifyDirectAsProxy()
    {
        using var dir = new TempTestDir("bug1-unapplied-yaml");
        var yamlPath = Path.Combine(dir.Path, "clash-verge.yaml");

        // 模拟内核在 04:00:00 启动（实际加载的是旧版 direct 出站）
        var kernelStart = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
        var serverId = new PipeServerIdentity("verge-mihomo-production-1", 5001, kernelStart.Ticks, "v1.19.33");

        // 磁盘上写入了尚未被内核加载的新版同名 ss 定义（修改时间晚于内核启动时间）
        File.WriteAllText(yamlPath, """
            proxies:
              - name: "NodeA"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            """);
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddSeconds(30).UtcDateTime);

        var clock = new FakeClock(kernelStart.AddSeconds(40));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var engine = new AccountingEngine(5, clock);

        var catInit = source.LoadCatalog();
        engine.InitializeSession(catInit);

        // 第 1 帧建立基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：内核实际走旧版 direct，产生 4096 B，但磁盘 YAML 两次读取均显示 ss
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var snap = new MihomoConnectionsSnapshot(
            4096, 0,
            [new MihomoConnectionRecord("c-direct", 4096, 0, connStart, ["NodeA", "代理"], "a.example.com", null, "app.exe", null)],
            clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId);

        var catAfter = source.LoadCatalog();
        var frame = engine.ProcessSnapshot(snap, catAfter, false);

        // 磁盘配置修改时间晚于内核启动且无内核已加载证据：严禁把直连算进代理，必须归入走向未确认
        Assert.Empty(frame.ProxyDeltas);
        Assert.NotNull(frame.DiagnosticDelta);
        Assert.Equal(4096, frame.DiagnosticDelta!.Value.UnconfirmedUploadBytes);
    }

    [Fact]
    public async Task Bug2_TransientSampleFailure_ReconnectSameKernelWithinGap_MustNotLoseConfirmedConnection()
    {
        using var dir = new TempTestDir("bug2-reconnect-loss");
        var paths = new NetTrackPaths(dir.Path);
        var kernelStart = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
        var serverId = new PipeServerIdentity("verge-mihomo-production-1", 6001, kernelStart.Ticks, "v1.19.33");

        // 写入早于内核启动的配置文件，证明内核加载了该版本
        var configDir = Path.Combine(dir.Path, "cfg");
        Directory.CreateDirectory(configDir);
        var yamlPath = Path.Combine(configDir, "clash-verge.yaml");
        File.WriteAllText(yamlPath, """
            proxies:
              - name: "HK-01"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            """);
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddMinutes(-1).UtcDateTime);

        var clock = new FakeClock(kernelStart.AddSeconds(10));
        var connStart = clock.UtcNow.AddMilliseconds(500).ToString("O");

        // 模拟帧序列：
        // probe1(发现) -> frame1(基线0B) -> frame2(100B) -> frame3(短暂失败!) -> probe2(重连发现) -> frame4(250B) -> frame5(400B)
        var callCount = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeMihomoClientFactory((pipe, _) => new FakeMihomoClient(
            pipe,
            _ => Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true)),
            _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                return n switch
                {
                    1 => Task.FromResult(new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId)),
                    2 => Task.FromResult(new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId)),
                    3 => Task.FromResult(new MihomoConnectionsSnapshot(
                        100, 0,
                        [new MihomoConnectionRecord("c1", 100, 0, connStart, ["HK-01", "代理"], "dl.example.com", null, "dl.exe", null)],
                        clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId)),
                    4 => throw new IOException("模拟短暂管道读取失败"),
                    5 => Task.FromResult(new MihomoConnectionsSnapshot(
                        150, 0,
                        [new MihomoConnectionRecord("c1", 150, 0, connStart, ["HK-01", "代理"], "dl.example.com", null, "dl.exe", null)],
                        clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId)),
                    6 => Task.FromResult(new MihomoConnectionsSnapshot(
                        250, 0,
                        [new MihomoConnectionRecord("c1", 250, 0, connStart, ["HK-01", "代理"], "dl.example.com", null, "dl.exe", null)],
                        clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId)),
                    _ => CompleteAndReturn400()
                };

                Task<MihomoConnectionsSnapshot> CompleteAndReturn400()
                {
                    finished.TrySetResult();
                    return Task.FromResult(new MihomoConnectionsSnapshot(
                        400, 0,
                        [new MihomoConnectionRecord("c1", 400, 0, connStart, ["HK-01", "代理"], "dl.example.com", null, "dl.exe", null)],
                        clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId));
                }
            }));

        var lockRes = CollectorFileLock.TryAcquire(paths.LockFilePath, "test");
        Assert.Equal(CollectorLockStatus.Acquired, lockRes.Status);

        var opts = new CollectorOptions { ExplicitPipeName = "verge-mihomo-production-1", PollIntervalSeconds = 1, CommitIntervalSeconds = 1, GapThresholdSeconds = 5 };
        var discovery = new PipeDiscoveryService(new FakeConfigLocator(null), new FakePipeEnumerator(), factory, TimeSpan.FromSeconds(1));
        using var ledger = new SqliteLedgerStore(paths.DatabasePath);
        using var runner = new CollectorRunner(
            opts,
            discovery,
            new RuntimeYamlProxyCatalogSource(configDir, clock: clock, isKernelVersionVerified: true),
            ledger,
            lockRes.LockHandle!,
            clock);

        runner.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
        Assert.True(stopRes.FlushSucceeded);

        var query = new ReportQueryService(paths, clock);
        var report = query.QueryDailyReport(new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.App), opts);

        // 短暂失败后重连同一内核（间隔 <= 5s），已确认连接 c1 全程应累计记账 400 B，不能只记 100 B
        Assert.Equal(400, report.Summary.UploadBytes);
        Assert.Equal(0, report.Summary.UnconfirmedUploadBytes);
    }

    [Fact]
    public void Bug3_ProviderOverrideDialerProxy_MustClassifyAsUnconfirmed()
    {
        using var dir = new TempTestDir("bug3-provider-override");
        var providerDir = Path.Combine(dir.Path, "proxy_providers");
        Directory.CreateDirectory(providerDir);

        File.WriteAllText(Path.Combine(providerDir, "sub-block.yaml"), """
            proxies:
              - name: "JP-Block"
                type: ss
            """);

        File.WriteAllText(Path.Combine(providerDir, "sub-flow.yaml"), """
            proxies:
              - name: "JP-Flow"
                type: vmess
            """);

        File.WriteAllText(Path.Combine(dir.Path, "clash-verge.yaml"), """
            proxies:
              - name: "HK-Jump"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            proxy-providers:
              sub-block:
                type: http
                path: ./proxy_providers/sub-block.yaml
                override:
                  dialer-proxy: "HK-Jump"
              sub-flow:
                type: http
                path: ./proxy_providers/sub-flow.yaml
                override: { dialer-proxy: "HK-Jump" }
            """);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catalog = source.LoadCatalog();

        Assert.Equal(ConnectionPathKind.Proxy, ProxyClassifier.Classify(["HK-Jump", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["JP-Block", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["JP-Flow", "代理"], catalog).Kind);
    }

    [Fact]
    public void Bug4_ZeroIndentYamlBlockSequence_MustParseProxiesAndGroups()
    {
        using var dir = new TempTestDir("bug4-zero-indent");
        File.WriteAllText(Path.Combine(dir.Path, "clash-verge.yaml"), """
            proxies:
            - name: "HK-Zero"
              type: ss
            - { name: "US-ZeroFlow", type: vless }
            proxy-groups:
            - name: "代理"
              type: select
            """);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catalog = source.LoadCatalog();

        Assert.Equal(ConnectionPathKind.Proxy, ProxyClassifier.Classify(["HK-Zero", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Proxy, ProxyClassifier.Classify(["US-ZeroFlow", "代理"], catalog).Kind);
    }
}
