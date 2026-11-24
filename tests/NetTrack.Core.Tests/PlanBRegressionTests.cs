using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Logging;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 针对方案 B 复查中发现的覆盖口径与节点筛选边界问题（缺陷 2、3）的隔离回归测试。
/// （缺陷 1、4 在 NetTrack.Desktop.Tests 中用真实 WinForms STA 窗体复现）
/// </summary>
public sealed class PlanBRegressionTests
{
    /// <summary>
    /// 复现缺陷 2（P2）：
    /// 00 点只有旧版通用覆盖（未启用自动观察），01 点才开始自动观察。
    /// 自动观察报表必须将 00 点标为 no_data（0 秒），绝不能沿用旧版通用覆盖显示「完整覆盖 3600 秒、0 B」。
    /// </summary>
    [Fact]
    public void PlanBBug2_AutoObservedCoverage_HourBeforeAutoObservedEnabled_MustShowNoDataNotCompleteZero()
    {
        using var dir = new TempTestDir("planb-bug2-auto-cov");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 11, 23);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var h2 = h1 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h2));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            // 批次 1（模拟升级前 00:00-01:00）：仅写入旧版 coverage_segments，无自动观察覆盖
            var batchOld = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batchOld.CoverageSegments.Add(new CoverageSegment(h0, h0, h1));
            store.CommitBatch(batchOld, h1);

            // 批次 2（模拟升级后 01:00-02:00）：自动观察启用，有自动观察流量与覆盖
            var batchNew = new PendingBatch(2) { CollectorStatus = "stopped", LastSampleUtcMs = h2, LastStoppedUtcMs = h2 };
            batchNew.CoverageSegments.Add(new CoverageSegment(h1, h1, h2));
            batchNew.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h1, "code.exe", "api.github.com", "HK-01", "subA")] = new BytePair(0, 4096);
            // 通过 AddFrameResult 或显式 AutoObservedCoverageSegments 写入 01 点自动观察覆盖
            var autoCovProp = typeof(PendingBatch).GetProperty("AutoObservedCoverageSegments");
            if (autoCovProp?.GetValue(batchNew) is List<CoverageSegment> autoCovList)
            {
                autoCovList.Add(new CoverageSegment(h1, h1, h2));
            }
            store.CommitBatch(batchNew, h2);
        }

        var query = new ReportQueryService(paths, clock);
        var report = query.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved),
            new CollectorOptions());

        // 00 点未启用自动观察：必须是 no_data（0 秒），不能是 complete（3600 秒）
        Assert.Equal("no_data", report.Items[0].CoverageStatus);
        Assert.Equal(0, report.Items[0].CoveredSeconds);

        // 01 点已启用自动观察：完整覆盖 3600 秒，下载 4096 B
        Assert.Equal("complete", report.Items[1].CoverageStatus);
        Assert.Equal(3600, report.Items[1].CoveredSeconds);
        Assert.Equal(4096, report.Items[1].DownloadBytes);
    }

    /// <summary>
    /// 复现缺陷 3（P2）：
    /// 1. 严格账本中同时存在原名 "HK::01"（无 Provider，4096 B）与 Provider "HK" 下的 "01"（8192 B）：
    ///    传入原始节点名筛选 "HK::01" 时必须导出 4096 B，绝不能误导成 Provider=HK 下的 8192 B；
    /// 2. 传入非法筛选键 "::" 时，严格账本与自动观察导出都必须返回空集，绝不能退回全量导出。
    /// </summary>
    [Fact]
    public void PlanBBug3_NodeFilter_RawDoubleColonNameInStrictLedger_AndInvalidDoubleColonFilter_MustNotLeakOrExpand()
    {
        using var dir = new TempTestDir("planb-bug3-node-filter");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 11, 23);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ProxyDeltas[new ProxyUsageKey(h0, "app.exe", "raw.com", "HK::01", "")] = new BytePair(0, 4096);
            batch.ProxyDeltas[new ProxyUsageKey(h0, "app.exe", "prov.com", "01", "HK")] = new BytePair(0, 8192);
            batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "app.exe", "auto.com", "HK-01", "subA")] = new BytePair(0, 2048);
            store.CommitBatch(batch, h1);
        }

        var query = new ReportQueryService(paths, clock);

        // 1. 严格账本按节点原名 "HK::01" 筛选：必须命中 4096 B，不得误导出 Provider=HK 下的 8192 B
        var strictRaw = query.QueryExportRows(day, new ReportFilters(Node: "HK::01"), ReportScope.ConfirmedProxy);
        var singleRaw = Assert.Single(strictRaw);
        Assert.Equal(4096, singleRaw.DownloadBytes);
        Assert.Equal("raw.com", singleRaw.Domain);

        // 2. 传入 "::"：严格账本与自动观察导出都必须返回空集，绝不能退回全量
        var strictInvalid = query.QueryExportRows(day, new ReportFilters(Node: "::"), ReportScope.ConfirmedProxy);
        Assert.Empty(strictInvalid);

        var autoInvalid = query.QueryExportRows(day, new ReportFilters(Node: "::"), ReportScope.AutoNodeObserved);
        Assert.Empty(autoInvalid);
    }

    /// <summary>
    /// 复现缺陷 2（P3）：
    /// 前一天记录了 4096 B 未确认的自动观察流量，次日采集器写入一次 AttributionVerified = true 后，
    /// 查询前一天报表时，该条目仍必须保持 unconfirmed，绝不能被最新核验状态改写为 confirmed。
    /// </summary>
    [Fact]
    public void PlanB2Bug2_NextDayVerifiedState_MustNotRewritePreviousDayUnconfirmedItemStatus()
    {
        using var dir = new TempTestDir("planb2-bug2-hist-verify");
        var paths = new NetTrackPaths(dir.Path);
        var day1 = new DateOnly(2026, 11, 22);
        var day2 = new DateOnly(2026, 11, 23);
        var d1h0 = BeijingTime.GetDayStartUtcMs(day1);
        var d1h1 = d1h0 + BeijingTime.HourMs;
        var d2h0 = BeijingTime.GetDayStartUtcMs(day2);
        var d2h1 = d2h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(d2h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();

            // 第 1 天：未核验状态记录 4096 B
            var b1 = new PendingBatch(1)
            {
                CollectorStatus = "running",
                LastSampleUtcMs = d1h1,
                AttributionVerified = false,
                AttributionReasonCode = "unverified_disk_yaml"
            };
            b1.AutoObservedNodeDeltas[new ObservedNodeUsageKey(d1h0, "code.exe", "a.com", "HK-01", "subA")] = new BytePair(0, 4096);
            b1.AutoObservedCoverageSegments.Add(new CoverageSegment(d1h0, d1h0, d1h1));
            store.CommitBatch(b1, d1h1);

            // 第 2 天：写入一次已核验状态
            var b2 = new PendingBatch(2)
            {
                CollectorStatus = "stopped",
                LastSampleUtcMs = d2h1,
                LastStoppedUtcMs = d2h1,
                AttributionVerified = true
            };
            store.CommitBatch(b2, d2h1);
        }

        var query = new ReportQueryService(paths, clock);
        var day1NodeReport = query.QueryDailyReport(
            new ReportQueryRequest(day1, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved),
            new CollectorOptions());

        var item = Assert.Single(day1NodeReport.Items);
        Assert.Equal(4096, item.DownloadBytes);
        Assert.Equal("unconfirmed", item.VerificationStatus);
    }

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// 旧版采集器正在运行并只写通用覆盖与心跳（未启用自动观察），
    /// 自动观察报表不得用旧采集器心跳算出 2 秒「待更新」，必须保持 0 ms 待更新（no_data）。
    /// </summary>
    [Fact]
    public void PlanB2Bug3_OldCollectorHeartbeatWithoutAutoObserved_MustNotMarkAutoObservedAsPending()
    {
        using var dir = new TempTestDir("planb2-bug3-old-heartbeat");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 11, 23);
        var h10 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var nowMs = h10 + 2000;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(nowMs));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            // 模拟旧版采集器：状态为 running、有心跳与 CurrentRunStartUtcMs，但未启用自动观察
            var batch = new PendingBatch(1)
            {
                CollectorStatus = "running",
                ActivePipeName = "verge-mihomo-old",
                CurrentRunStartUtcMs = h10,
                LastSampleUtcMs = h10
            };
            store.CommitBatch(batch, nowMs);
        }

        var query = new ReportQueryService(paths, clock);
        var report = query.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved),
            new CollectorOptions());

        Assert.Equal(0, report.Items[10].PendingMs);
        Assert.Equal("no_data", report.Items[10].CoverageStatus);
    }

    /// <summary>
    /// 复现缺陷 1（P3）：
    /// 配置临时读取失败后在下一帧恢复并确认为 Proxy 时，恢复当帧的 50 B 在严格账本仍归未确认，
    /// 自动观察写入当帧的 50 B 也必须标为 Verified = false，绝不能提前标成 verified = true。
    /// </summary>
    [Fact]
    public void PlanB3Bug1_RecoveryFramePromotionToProxy_MustMarkAutoObservedAsUnverifiedOnRecoveryFrame()
    {
        var t0 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset);
        var clock = new FakeClock(t0);
        var serverId = new NetTrack.Core.Mihomo.PipeServerIdentity("verge-mihomo-test", 4001, t0.AddMinutes(-1).Ticks, "v1.19.33");
        var verifiedCatalog = new NetTrack.Core.Mihomo.ProxyCatalog(
            clock.UtcNowMs,
            new Dictionary<string, NetTrack.Core.Mihomo.ProxyMemberInfo>(StringComparer.Ordinal)
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
            new NetTrack.Core.Mihomo.MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            null, false);

        // 第 2 帧：新连接出现时目录临时读取失败 -> PendingCatalogCheck
        clock.Advance(1000);
        var connStart = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs - 50).ToString("O");
        engine.ProcessSnapshot(
            new NetTrack.Core.Mihomo.MihomoConnectionsSnapshot(
                0, 100,
                [new NetTrack.Core.Mihomo.MihomoConnectionRecord("c1", 0, 100, connStart, ["HK-01", "Proxy"], "a.com", null, "app.exe", null, ["subA"])],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            catalogAfter: null,
            catalogFetchFailed: true);

        // 第 3 帧（恢复帧）：目录恢复并确认为 Proxy，新增 50 B
        clock.Advance(1000);
        var recoveryFrame = engine.ProcessSnapshot(
            new NetTrack.Core.Mihomo.MihomoConnectionsSnapshot(
                0, 150,
                [new NetTrack.Core.Mihomo.MihomoConnectionRecord("c1", 0, 150, connStart, ["HK-01", "Proxy"], "a.com", null, "app.exe", null, ["subA"])],
                clock.UtcNowMs - 5, clock.UtcNowMs, clock.GetTimestamp(), serverId),
            catalogAfter: verifiedCatalog,
            catalogFetchFailed: false);

        // 严格账本恢复当帧 50 B 仍归未确认
        Assert.Empty(recoveryFrame.ProxyDeltas);
        Assert.Equal(50, recoveryFrame.DiagnosticDelta!.Value.UnconfirmedDownloadBytes);

        // 自动观察恢复当帧 50 B 必须标为 Verified = false
        var obs = Assert.Single(recoveryFrame.AutoObservedNodeDeltas);
        Assert.Equal(50, obs.Value.DownloadBytes);
        Assert.False(obs.Key.Verified);
    }

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// 同一小时、同一应用、域名、节点的自动观察记录若分别存在 verified=0（4096 B）与 verified=1（8192 B），
    /// CSV 导出（固定 8 列不含核验列）必须按 5 维汇总为单条 12288 B 记录，绝不能拆成两条重复键行。
    /// </summary>
    [Fact]
    public void PlanB3Bug3_AutoObservedCsvExport_SameHourAppDomainNodeAcrossVerifiedAndUnverified_MustAggregateIntoSingleRow()
    {
        using var dir = new TempTestDir("planb3-bug3-csv-merge");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 11, 23);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "code.exe", "api.github.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 4096);
            batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "code.exe", "api.github.com", "HK-01", "subA", Verified: true)] = new BytePair(0, 8192);
            store.CommitBatch(batch, h1);
        }

        var query = new ReportQueryService(paths, clock);
        var csvRows = query.QueryExportRows(day, null, ReportScope.AutoNodeObserved);

        var single = Assert.Single(csvRows);
        Assert.Equal(12288, single.DownloadBytes);
        Assert.Equal(12288, single.TotalBytes);
    }

    /// <summary>
    /// 复现缺陷 2（P3）：
    /// SQLite 已成功提交批次 1 后，若 StatusChanged 订阅者回调抛出异常，
    /// 采集器绝不能将其误判为账本写入失败而切入 StorageError 或写入重试模式。
    /// </summary>
    [Fact]
    public async Task TrialUi2Bug2_StatusChangedCallbackThrowsAfterCommit_MustNotEnterStorageError()
    {
        using var dir = new TempTestDir("trialui2-bug2-callback");
        var paths = new NetTrackPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        const string pipeName = @"\\.\pipe\test-callback-isolated";
        var lockResult = CollectorFileLock.TryAcquire(paths.LockFilePath, "test");
        Assert.Equal(CollectorLockStatus.Acquired, lockResult.Status);

        var options = new CollectorOptions { PollIntervalSeconds = 1, CommitIntervalSeconds = 1 };
        var discovery = new PipeDiscoveryService(
            new StaticPipeConfigLocator(pipeName),
            new StaticPipeEnumerator([pipeName]),
            new StaticMihomoClientFactory(),
            TimeSpan.FromSeconds(1));
        using var store = new SqliteLedgerStore(paths.DatabasePath);
        var logger = new RollingFileLogger(paths.LogsDirectory, SystemClock.Instance);

        using var runner = new CollectorRunner(
            options,
            discovery,
            new EmptyProxyCatalogSource(SystemClock.Instance),
            store,
            lockResult.LockHandle!,
            SystemClock.Instance,
            logger);

        var callbackThrew = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.StatusChanged += status =>
        {
            if (status.LastCommittedSampleUtcMs.HasValue)
            {
                callbackThrew.TrySetResult(true);
                throw new InvalidOperationException("模拟订阅者回调异常");
            }
        };

        runner.Start();
        var completed = await Task.WhenAny(callbackThrew.Task, Task.Delay(5000));
        Assert.Same(callbackThrew.Task, completed);

        // 留出短暂时间观察采集器是否错误切入 StorageError
        await Task.Delay(150);
        Assert.NotEqual(CollectorRuntimeState.StorageError, runner.CurrentStatus.State);
        Assert.Equal(CollectorRuntimeState.Collecting, runner.CurrentStatus.State);
        Assert.True(runner.CurrentStatus.LastCommittedSampleUtcMs.HasValue);

        var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
        Assert.True(stopRes.FlushSucceeded);
    }

    /// <summary>
    /// 复现缺陷 2（P3）：
    /// 归因状态首次变化触发 StatusChanged 时，若订阅者回调抛异常，
    /// 采集器不得将其误判为 Clash 断线进入 WaitingClash，且当前采样帧必须正常进入批次并提交。
    /// </summary>
    [Fact]
    public async Task TrialUi3Bug2_AttributionChangedCallbackThrows_MustNotDisconnectOrSkipFrame()
    {
        using var dir = new TempTestDir("trialui3-bug2-attr-callback");
        var paths = new NetTrackPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        const string pipeName = @"\\.\pipe\test-attr-callback-isolated";
        var lockResult = CollectorFileLock.TryAcquire(paths.LockFilePath, "test");
        Assert.Equal(CollectorLockStatus.Acquired, lockResult.Status);

        var options = new CollectorOptions { PollIntervalSeconds = 1, CommitIntervalSeconds = 1 };
        var discovery = new PipeDiscoveryService(
            new StaticPipeConfigLocator(pipeName),
            new StaticPipeEnumerator([pipeName]),
            new StaticMihomoClientFactory(),
            TimeSpan.FromSeconds(1));
        using var store = new SqliteLedgerStore(paths.DatabasePath);
        var logger = new RollingFileLogger(paths.LogsDirectory, SystemClock.Instance);

        using var runner = new CollectorRunner(
            options,
            discovery,
            new EmptyProxyCatalogSource(SystemClock.Instance),
            store,
            lockResult.LockHandle!,
            SystemClock.Instance,
            logger);

        var threwOnAttrChange = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredWaitingAfterThrow = false;
        var threwOnce = 0;
        runner.StatusChanged += status =>
        {
            if (Volatile.Read(ref threwOnce) == 1 && status.State == CollectorRuntimeState.WaitingClash)
            {
                enteredWaitingAfterThrow = true;
            }

            if (status.AttributionVerified.HasValue && Interlocked.CompareExchange(ref threwOnce, 1, 0) == 0)
            {
                threwOnAttrChange.TrySetResult(true);
                throw new InvalidOperationException("模拟归因状态变更回调异常");
            }
        };

        runner.Start();
        var completed = await Task.WhenAny(threwOnAttrChange.Task, Task.Delay(5000));
        Assert.Same(threwOnAttrChange.Task, completed);

        // 等待至少完成一次提交并确认未进入 WaitingClash
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && !runner.CurrentStatus.LastCommittedSampleUtcMs.HasValue)
        {
            await Task.Delay(50);
        }

        Assert.False(enteredWaitingAfterThrow, "归因变化回调异常不应导致采集器进入 WaitingClash。");
        Assert.Equal(CollectorRuntimeState.Collecting, runner.CurrentStatus.State);
        Assert.True(runner.CurrentStatus.LastCommittedSampleUtcMs.HasValue, "当前采样帧不应被跳过，必须成功落盘。");

        var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
        Assert.True(stopRes.FlushSucceeded);
    }

    private sealed class StaticPipeConfigLocator(string pipeName) : IClashConfigLocator
    {
        public string? TryReadConfiguredPipeName() => pipeName;
    }

    private sealed class StaticPipeEnumerator(IReadOnlyList<string> pipes) : IPipeEnumerator
    {
        public IEnumerable<string> EnumerateLocalPipeNames() => pipes;
    }

    private sealed class EmptyProxyCatalogSource(IClock clock) : IProxyCatalogSource
    {
        public Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyCatalog(clock.UtcNowMs, new Dictionary<string, ProxyMemberInfo>()));
    }

    private sealed class StaticMihomoClientFactory : IMihomoClientFactory
    {
        public IMihomoClient Create(string pipeName, TimeSpan timeout) => new StaticMihomoClient(pipeName);
    }

    private sealed class StaticMihomoClient(string pipeName) : IMihomoClient
    {
        private long _tick;
        public string PipeName => pipeName;
        public PipeServerIdentity? LastServerIdentity => new(pipeName, 4321, 1, "v1.19.33");

        public Task<MihomoVersionResponse?> GetVersionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true));

        public Task<MihomoConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _tick);
            var now = SystemClock.Instance.UtcNowMs;
            return Task.FromResult(new MihomoConnectionsSnapshot(
                RequestStartUtcMs: now - 5,
                ReceiveUtcMs: now,
                ReceiveMonoTicks: SystemClock.Instance.GetTimestamp(),
                ServerIdentity: LastServerIdentity,
                UploadTotal: n * 512,
                DownloadTotal: n * 1024,
                Connections:
                [
                    new MihomoConnectionRecord(
                        Id: "c1",
                        Upload: n * 512,
                        Download: n * 1024,
                        StartRaw: DateTimeOffset.FromUnixTimeMilliseconds(now - 200).ToString("O"),
                        Chains: ["HK-01", "Proxy"],
                        ProviderChains: ["subA"],
                        Host: "example.com",
                        SniffHost: null,
                        Process: "chrome.exe",
                        ProcessPath: @"C:\chrome.exe")
                ]));
        }

        public void Dispose() { }
    }
}
