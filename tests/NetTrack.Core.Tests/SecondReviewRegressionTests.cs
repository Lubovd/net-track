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
/// 针对第二轮审查复现的 4 个 P1 缺陷的隔离回归测试。
/// </summary>
public sealed class SecondReviewRegressionTests
{
    [Fact]
    public void Bug1_OldYamlFromAnotherPipe_EarlierThanProcessStart_MustNotBeTreatedAsKernelProof()
    {
        using var dir = new TempTestDir("bug1-other-pipe-yaml");
        var yamlPath = Path.Combine(dir.Path, "clash-verge.yaml");

        // 模拟一份属于另一条管道的旧 YAML（修改时间早于内核启动时间）
        var kernelStart = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
        File.WriteAllText(yamlPath, """
            external-controller-pipe: verge-mihomo-sidecar-release-other
            proxies:
              - name: "NodeA"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            """);
        File.SetLastWriteTimeUtc(yamlPath, kernelStart.AddMinutes(-10).UtcDateTime);

        var serverId = new PipeServerIdentity("verge-mihomo-production-real", 7001, kernelStart.Ticks, "v1.19.33");
        var clock = new FakeClock(kernelStart.AddSeconds(10));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var engine = new AccountingEngine(5, clock);

        engine.InitializeSession(source.LoadCatalog());

        // 第 1 帧基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：实际连接走直连 4096 B；仅凭磁盘文件修改时间早于进程启动不能证明内核加载了该文件
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 100).ToString("O");
        var frame = engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(
                4096, 0,
                [new MihomoConnectionRecord("c1", 4096, 0, connStart, ["NodeA", "代理"], "a.com", null, "app.exe", null)],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            source.LoadCatalog(), false);

        Assert.Empty(frame.ProxyDeltas);
        Assert.NotNull(frame.DiagnosticDelta);
        Assert.Equal(4096, frame.DiagnosticDelta!.Value.UnconfirmedUploadBytes);
    }

    [Fact]
    public async Task Bug2_MissingYamlAtStartup_MustNotAbortSampling_RecordsUnconfirmedTraffic()
    {
        using var dir = new TempTestDir("bug2-missing-yaml");
        var paths = new NetTrackPaths(dir.Path);
        var emptyConfigDir = Path.Combine(dir.Path, "missing-cfg");
        Directory.CreateDirectory(emptyConfigDir); // 故意不放 clash-verge.yaml

        var kernelStart = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
        var serverId = new PipeServerIdentity("verge-mihomo-production-1", 7002, kernelStart.Ticks, "v1.19.33");
        var clock = new FakeClock(kernelStart.AddSeconds(10));
        var connStart = clock.UtcNow.AddMilliseconds(500).ToString("O");

        var connCalls = 0;
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeMihomoClientFactory((pipe, _) => new FakeMihomoClient(
            pipe,
            _ => Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true)),
            _ =>
            {
                var n = Interlocked.Increment(ref connCalls);
                if (n <= 2)
                {
                    return Task.FromResult(new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId));
                }

                sampled.TrySetResult();
                return Task.FromResult(new MihomoConnectionsSnapshot(
                    2048, 4096,
                    [new MihomoConnectionRecord("c1", 2048, 4096, connStart, ["HK-01", "代理"], "a.com", null, "app.exe", null)],
                    clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId));
            }));

        var lockRes = CollectorFileLock.TryAcquire(paths.LockFilePath, "test");
        Assert.Equal(CollectorLockStatus.Acquired, lockRes.Status);

        var opts = new CollectorOptions { ExplicitPipeName = "verge-mihomo-production-1", PollIntervalSeconds = 1, CommitIntervalSeconds = 1, GapThresholdSeconds = 5 };
        var discovery = new PipeDiscoveryService(new FakeConfigLocator(null), new FakePipeEnumerator(), factory, TimeSpan.FromSeconds(1));
        using var ledger = new SqliteLedgerStore(paths.DatabasePath);
        using var runner = new CollectorRunner(
            opts,
            discovery,
            new RuntimeYamlProxyCatalogSource(emptyConfigDir, clock: clock),
            ledger,
            lockRes.LockHandle!,
            clock);

        runner.Start();
        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
        Assert.True(stopRes.FlushSucceeded);

        var query = new ReportQueryService(paths, clock);
        var report = query.QueryDailyReport(new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.App), opts);

        // 即使 YAML 缺失，采集也不能被中断，必须持续采样并把未确认流量记入诊断
        Assert.Equal(0, report.Summary.UploadBytes);
        Assert.Equal(2048, report.Summary.UnconfirmedUploadBytes);
        Assert.Equal(4096, report.Summary.UnconfirmedDownloadBytes);
    }

    [Fact]
    public void Bug3_ProviderLevelDialerProxy_MustClassifyAsUnconfirmed()
    {
        using var dir = new TempTestDir("bug3-provider-level-dialer");
        var providerDir = Path.Combine(dir.Path, "proxy_providers");
        Directory.CreateDirectory(providerDir);

        File.WriteAllText(Path.Combine(providerDir, "sub.yaml"), """
            proxies:
              - name: "JP-Leaf"
                type: ss
            """);

        File.WriteAllText(Path.Combine(dir.Path, "clash-verge.yaml"), """
            proxies:
              - name: "HK-Jump"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            proxy-providers:
              sub:
                type: http
                path: ./proxy_providers/sub.yaml
                dialer-proxy: "HK-Jump"
            """);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catalog = source.LoadCatalog();

        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["JP-Leaf", "代理"], catalog).Kind);
    }

    [Fact]
    public void Bug4_DuplicateNodeNameAcrossProviders_ConflictingDefinitions_MustClassifyAsUnconfirmed()
    {
        using var dir = new TempTestDir("bug4-dup-provider-nodes");
        var providerDir = Path.Combine(dir.Path, "proxy_providers");
        Directory.CreateDirectory(providerDir);

        File.WriteAllText(Path.Combine(providerDir, "p1.yaml"), """
            proxies:
              - name: "SharedName"
                type: direct
            """);

        File.WriteAllText(Path.Combine(providerDir, "p2.yaml"), """
            proxies:
              - name: "SharedName"
                type: ss
            """);

        File.WriteAllText(Path.Combine(dir.Path, "clash-verge.yaml"), """
            proxy-groups:
              - name: "代理"
                type: select
            proxy-providers:
              p1:
                type: http
                path: ./proxy_providers/p1.yaml
              p2:
                type: http
                path: ./proxy_providers/p2.yaml
            """);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catalog = source.LoadCatalog();

        // 两个 Provider 存在同名冲突定义（direct vs ss），绝不能由后者覆盖前者变成 Proxy
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["SharedName", "代理"], catalog).Kind);
    }

    [Fact]
    public void Feature5_ProviderChainsAndSelectedNodesObservedReport_IndependentOfStrictLedger()
    {
        using var dir = new TempTestDir("feature5-selected-nodes");
        var paths = new NetTrackPaths(dir.Path);

        // 1. 解析 /connections JSON 中的 providerChains
        var sampleJson = """
            {
              "uploadTotal": 5000,
              "downloadTotal": 9000,
              "connections": [
                {
                  "id": "c1",
                  "upload": 3000,
                  "download": 6000,
                  "start": "2026-11-23T04:00:11.500Z",
                  "chains": ["HK-Paid-01", "代理"],
                  "providerChains": ["paid-sub"],
                  "metadata": { "host": "api.example.com", "processPath": "C:\\Apps\\Tool.exe" }
                },
                {
                  "id": "c2",
                  "upload": 2000,
                  "download": 3000,
                  "start": "2026-11-23T04:00:11.500Z",
                  "chains": ["DIRECT", "代理"],
                  "providerChains": [],
                  "metadata": { "host": "music.qq.com", "processPath": "C:\\Apps\\QQMusic.exe" }
                }
              ]
            }
            """;

        var kernelStart = new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
        var serverId = new PipeServerIdentity("verge-mihomo-production-1", 8001, kernelStart.Ticks, "v1.19.33");
        var clock = new FakeClock(kernelStart.AddSeconds(10));

        // 2. 配置 selectedNodes（故意混入 DIRECT，验证会被自动排除）
        var settings = new NetTrackSettings { SelectedNodes = ["HK-Paid-01", "DIRECT", "HK-Paid-01"] };
        SettingsLoader.Save(paths.SettingsPath, settings);
        var (loaded, err) = SettingsLoader.Load(paths.SettingsPath);
        Assert.Null(err);
        Assert.Equal(["HK-Paid-01"], loaded!.SelectedNodes);

        var opts = CollectorOptions.FromSettings(loaded);
        var engine = new AccountingEngine(5, clock, selectedNodes: opts.SelectedNodes);

        // 故意使用未核验空节点表（严格账本无权确认代理，归为未确认）
        engine.InitializeSession(ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs));

        // 第 1 帧基线
        clock.Advance(1000);
        engine.ProcessSnapshot(
            new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：解析含 providerChains 的快照
        clock.Advance(1000);
        var parsedSnap = MihomoJsonParser.ParseConnections(sampleJson, clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId);
        Assert.Equal(["paid-sub"], parsedSnap.Connections[0].ProviderChains);

        var frame = engine.ProcessSnapshot(parsedSnap, ProxyCatalog.CreateEmptyUnverified(clock.UtcNowMs), false);

        // 严格代理账本不受污染（为 0），全部归入未确认诊断
        Assert.Empty(frame.ProxyDeltas);
        Assert.Equal(5000, frame.DiagnosticDelta!.Value.UnconfirmedUploadBytes);

        // 所选节点观察量精确记录 HK-Paid-01（含 provider），绝不记录 DIRECT
        Assert.Single(frame.ObservedSelectedNodeDeltas);
        var obs = frame.ObservedSelectedNodeDeltas.Single();
        Assert.Equal("HK-Paid-01", obs.Key.Node);
        Assert.Equal("paid-sub", obs.Key.Provider);
        Assert.Equal("tool.exe", obs.Key.App);
        Assert.Equal("api.example.com", obs.Key.Domain);
        Assert.Equal(3000, obs.Value.UploadBytes);
        Assert.Equal(6000, obs.Value.DownloadBytes);

        // 写入 SQLite 并通过独立报表查询验证
        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastStoppedUtcMs = clock.UtcNowMs };
            batch.AddFrameResult(frame);
            store.CommitBatch(batch, clock.UtcNowMs);
        }

        var query = new ReportQueryService(paths, clock);
        var dateBj = BeijingTime.GetBeijingDate(clock.UtcNowMs);

        var strictReport = query.QueryDailyReport(new ReportQueryRequest(dateBj, ReportGroupBy.App, Scope: ReportScope.ConfirmedProxy), opts);
        Assert.Equal(0, strictReport.Summary.TotalBytes);

        var observedReport = query.QueryDailyReport(new ReportQueryRequest(dateBj, ReportGroupBy.Domain, Scope: ReportScope.SelectedNodesObserved), opts);
        Assert.Equal("selected_nodes_observed", observedReport.Scope);
        Assert.Equal(3000, observedReport.Summary.UploadBytes);
        Assert.Equal(6000, observedReport.Summary.DownloadBytes);
        Assert.Single(observedReport.Items);
        Assert.Equal("api.example.com", observedReport.Items[0].Key);
    }
}
