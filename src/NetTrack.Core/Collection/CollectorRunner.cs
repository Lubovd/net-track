using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Logging;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Storage;

namespace NetTrack.Core.Collection;

public enum CollectorRuntimeState
{
    Stopped,
    WaitingClash,
    Collecting,
    AmbiguousPipes,
    StorageError
}

public sealed record CollectorStatusSnapshot(
    CollectorRuntimeState State,
    string? ActivePipeName,
    string? MihomoVersion,
    long? LastCommittedSampleUtcMs,
    string? Message,
    bool? AttributionVerified = null,
    string? AttributionReasonText = null);

public sealed record CollectorStopResult(
    bool FlushSucceeded,
    long? LastCommittedSampleUtcMs,
    string? ErrorMessage);

/// <summary>
/// 采集主循环协调器：管理管道发现、夹逼节点核对、5 秒原子批次、故障保留重试、90 天清理与 5 秒窗口停止刷盘。
/// </summary>
public sealed class CollectorRunner : IDisposable
{
    private static readonly int[] ReconnectBackoffSeconds = [1, 2, 4, 8, 15];
    private static readonly int[] ShutdownBackoffMs = [200, 500, 1000, 1000];

    private readonly CollectorOptions _options;
    private readonly PipeDiscoveryService _discoveryService;
    private readonly IProxyCatalogSource _catalogSource;
    private readonly ILedgerWriter _ledgerWriter;
    private readonly IClock _clock;
    private readonly ILogSink _logger;
    private readonly AccountingEngine _engine;

    private CollectorFileLock? _lockHandle;
    private IMihomoClient? _activeClient;
    private string? _activePipeName;
    private string? _mihomoVersion;

    private long _nextBatchId;
    private PendingBatch? _pendingBatch;
    private bool _flushFailedMode;
    private long? _lastCommittedSampleUtcMs;
    private DateOnly? _lastRetentionCleanupDateBj;
    private long _lastCommitMonoTicks;
    private long _lastPeriodicProxyRefreshMonoTicks;
    private CollectorRuntimeState _runtimeState = CollectorRuntimeState.Stopped;
    private string? _lastStatusMessage;
    private bool? _attributionVerified;
    private string? _attributionReasonText;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public event Action<CollectorStatusSnapshot>? StatusChanged;

    public CollectorRunner(
        CollectorOptions options,
        PipeDiscoveryService discoveryService,
        IProxyCatalogSource catalogSource,
        ILedgerWriter ledgerWriter,
        CollectorFileLock lockHandle,
        IClock? clock = null,
        ILogSink? logger = null)
    {
        _options = options;
        _discoveryService = discoveryService;
        _catalogSource = catalogSource;
        _ledgerWriter = ledgerWriter;
        _lockHandle = lockHandle;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogSink.Instance;
        _engine = new AccountingEngine(options.GapThresholdSeconds, _clock, _logger, options.SelectedNodes);
    }

    public CollectorStatusSnapshot CurrentStatus => new(
        _runtimeState,
        _activePipeName,
        _mihomoVersion,
        _lastCommittedSampleUtcMs,
        _lastStatusMessage,
        _attributionVerified,
        _attributionReasonText);

    public bool HoldsLock => _lockHandle is not null;

    /// <summary>
    /// 初始化账本、执行启动期 90 天清理并在后台启动采集循环。
    /// </summary>
    public void Start()
    {
        if (_loopTask is not null)
        {
            throw new InvalidOperationException("采集器已在运行中。");
        }

        _ledgerWriter.Initialize();
        _nextBatchId = _ledgerWriter.GetLastBatchId() + 1;
        _pendingBatch = new PendingBatch(_nextBatchId);

        var todayBj = BeijingTime.GetBeijingDate(_clock.UtcNowMs);
        var lastCleanup = _ledgerWriter.GetLastRetentionCleanupDateBj();
        if (!string.Equals(lastCleanup, BeijingTime.FormatDate(todayBj), StringComparison.Ordinal))
        {
            var deleted = _ledgerWriter.CleanupExpiredData(todayBj, _options.RetentionDays);
            _logger.Info($"启动期保留期清理完成（清理记录数={deleted}）。");
        }
        _lastRetentionCleanupDateBj = todayBj;

        _lastCommitMonoTicks = _clock.GetTimestamp();
        _lastPeriodicProxyRefreshMonoTicks = _lastCommitMonoTicks;

        SetState(CollectorRuntimeState.WaitingClash, "正在查找 Clash / Mihomo 管道…");

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    /// <summary>
    /// 停止采样、执行 5 秒重试发起窗口的末批刷盘，并在写入线程彻底返回后按需释放锁。
    /// </summary>
    public async Task<CollectorStopResult> StopAsync(bool releaseLockOnFailure = true)
    {
        if (_loopCts is not null)
        {
            try
            {
                _loopCts.Cancel();
            }
            catch
            {
                // 忽略重复取消
            }
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch
            {
                // 循环内部已处理取消与异常
            }
            _loopTask = null;
        }

        _loopCts?.Dispose();
        _loopCts = null;

        DisposeActiveClient();

        // 在同一执行流中完成末批刷盘（5 秒重试发起窗口），确保调用返回后再关连接与释锁
        var result = await FlushFinalBatchWithRetryWindowAsync().ConfigureAwait(false);

        if (result.FlushSucceeded || releaseLockOnFailure)
        {
            ReleaseResourcesAndLock();
            SetState(CollectorRuntimeState.Stopped, result.FlushSucceeded ? "已停止采集。" : $"已停止，但末批保存失败：{result.ErrorMessage}");
        }
        else
        {
            SetState(CollectorRuntimeState.StorageError, $"末批保存失败：{result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// 当桌面端用户点击「重试保存」时，在仍持有锁的情况下再次发起一轮 5 秒刷盘窗口。
    /// </summary>
    public async Task<CollectorStopResult> RetryFinalFlushAsync()
    {
        var result = await FlushFinalBatchWithRetryWindowAsync().ConfigureAwait(false);
        if (result.FlushSucceeded)
        {
            ReleaseResourcesAndLock();
            SetState(CollectorRuntimeState.Stopped, "末批保存成功，已停止采集。");
        }
        return result;
    }

    /// <summary>
    /// 用户确认放弃未保存的末批数据时，关闭连接并释放采集锁。
    /// </summary>
    public void AbandonPendingAndReleaseLock()
    {
        ReleaseResourcesAndLock();
        SetState(CollectorRuntimeState.Stopped, "已放弃未保存的末批数据并停止采集。");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 跨北京时间自然日时触发定期保留期清理
                var todayBj = BeijingTime.GetBeijingDate(_clock.UtcNowMs);
                if (_lastRetentionCleanupDateBj != todayBj && !_flushFailedMode)
                {
                    try
                    {
                        var deleted = _ledgerWriter.CleanupExpiredData(todayBj, _options.RetentionDays);
                        _lastRetentionCleanupDateBj = todayBj;
                        _logger.Info($"跨日保留期清理完成（清理记录数={deleted}）。");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("跨日保留期清理失败。", ex);
                    }
                }

                // 写入故障恢复模式：暂停追加新采样，保留原批次重试直到提交成功
                if (_flushFailedMode)
                {
                    await TryRecoverFailedFlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // 若当前无活跃客户端，执行管道发现
                if (_activeClient is null)
                {
                    var discovery = await _discoveryService
                        .DiscoverAsync(_options.ExplicitPipeName, ct)
                        .ConfigureAwait(false);

                    if (discovery.Status == PipeDiscoveryStatus.Connected &&
                        discovery.Client is not null &&
                        discovery.Version is not null)
                    {
                        _activeClient = discovery.Client;
                        _activePipeName = discovery.Client.PipeName;
                        _mihomoVersion = discovery.Version.Version;

                        // 初始化或重连时加载节点表；若 YAML 缺失/不可读则降级为空未核验表，不中断管道采样
                        ProxyCatalog initialCatalog;
                        try
                        {
                            initialCatalog = await _catalogSource.LoadCatalogAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"初始化读取本机节点定义失败，降级为空未核验表并继续采样：{ex.Message}");
                            initialCatalog = ProxyCatalog.CreateEmptyUnverified(_clock.UtcNowMs);
                        }

                        if (_engine.IsSessionInitialized)
                        {
                            _engine.OnReconnected(initialCatalog);
                        }
                        else
                        {
                            _engine.InitializeSession(initialCatalog);
                        }
                        _lastPeriodicProxyRefreshMonoTicks = _clock.GetTimestamp();
                        reconnectAttempt = 0;

                        SetState(CollectorRuntimeState.Collecting, $"已连接 {_activePipeName}（{_mihomoVersion}）。");
                        _logger.Info($"已接通管道 {_activePipeName}，Mihomo {_mihomoVersion}，节点定义数={initialCatalog.Members.Count}。");
                    }
                    else
                    {
                        var nextState = discovery.Status == PipeDiscoveryStatus.Ambiguous
                            ? CollectorRuntimeState.AmbiguousPipes
                            : CollectorRuntimeState.WaitingClash;
                        SetState(nextState, discovery.Message);
                        FlushStatusHeartbeatIfDue(nextState == CollectorRuntimeState.AmbiguousPipes ? "ambiguous_pipes" : "waiting_clash", discovery.Message);

                        var delaySec = ReconnectBackoffSeconds[Math.Min(reconnectAttempt, ReconnectBackoffSeconds.Length - 1)];
                        reconnectAttempt++;
                        await _clock.DelayAsync(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                // 串行采样：同一时刻只有一次 /connections 请求在途
                var pollStartTicks = _clock.GetTimestamp();
                var snapshot = await _activeClient.GetConnectionsAsync(ct).ConfigureAwait(false);

                // 判断是否需要后置夹逼核对节点表（本帧有新连接/待核对连接，或到达 60s 定期刷新）
                ProxyCatalog? catalogAfter = null;
                var catalogFetchFailed = false;
                var needsVerify = _engine.NeedsCatalogVerification(snapshot);
                var periodicDue = _clock.GetElapsedTime(_lastPeriodicProxyRefreshMonoTicks, _clock.GetTimestamp()).TotalSeconds >=
                                  _options.ProxyRefreshIntervalSeconds;

                if (needsVerify || periodicDue)
                {
                    try
                    {
                        catalogAfter = await _catalogSource.LoadCatalogAsync(ct).ConfigureAwait(false);
                        _lastPeriodicProxyRefreshMonoTicks = _clock.GetTimestamp();
                        if (!needsVerify && periodicDue)
                        {
                            _engine.OnPeriodicCatalogRefreshed(catalogAfter);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        catalogFetchFailed = true;
                        _logger.Warn($"读取本机节点定义失败，本帧新连接保守归入走向未确认：{ex.Message}");
                    }
                }

                var frame = _engine.ProcessSnapshot(snapshot, needsVerify ? catalogAfter : null, needsVerify && catalogFetchFailed);

                var attrChanged = _attributionVerified != frame.AttributionVerified ||
                                  !string.Equals(_attributionReasonText, frame.AttributionReasonText, StringComparison.Ordinal);
                _attributionVerified = frame.AttributionVerified;
                _attributionReasonText = frame.AttributionReasonText;
                if (attrChanged)
                {
                    NotifyStatusChanged();
                }

                _pendingBatch ??= new PendingBatch(_nextBatchId);
                _pendingBatch.CollectorStatus = "running";
                _pendingBatch.AutoObservedActive = true;
                _pendingBatch.ActivePipeName = _activePipeName;
                _pendingBatch.MihomoVersion = _mihomoVersion;
                _pendingBatch.ActiveObservedTargetsJson = _options.SelectedNodes.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(_options.SelectedNodes)
                    : null;
                _pendingBatch.LastErrorCode = null;
                _pendingBatch.LastErrorMessage = null;
                _pendingBatch.LastErrorUtcMs = null;
                _pendingBatch.AddFrameResult(frame);

                // 周期提交（默认 5 秒）
                if (_clock.GetElapsedTime(_lastCommitMonoTicks, _clock.GetTimestamp()).TotalSeconds >= _options.CommitIntervalSeconds)
                {
                    TryCommitCurrentBatch();
                }

                // 等待至下一采样周期
                var elapsed = _clock.GetElapsedTime(pollStartTicks, _clock.GetTimestamp());
                var wait = TimeSpan.FromSeconds(_options.PollIntervalSeconds) - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"采集管道读取异常，准备退避重连：{ex.Message}");
                DisposeActiveClient();
                SetState(CollectorRuntimeState.WaitingClash, $"管道连接中断，正在重试：{ex.Message}");
                FlushStatusHeartbeatIfDue("waiting_clash", ex.Message);

                var delaySec = ReconnectBackoffSeconds[Math.Min(reconnectAttempt, ReconnectBackoffSeconds.Length - 1)];
                reconnectAttempt++;
                try
                {
                    await _clock.DelayAsync(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void TryCommitCurrentBatch()
    {
        if (_pendingBatch is null)
        {
            return;
        }

        var committedSampleUpdated = false;
        try
        {
            _ledgerWriter.CommitBatch(_pendingBatch, _clock.UtcNowMs);
            if (_pendingBatch.LastSampleUtcMs.HasValue)
            {
                _lastCommittedSampleUtcMs = _pendingBatch.LastSampleUtcMs;
                committedSampleUpdated = true;
            }
            _nextBatchId = _pendingBatch.BatchId + 1;
            _pendingBatch = new PendingBatch(_nextBatchId);
            _lastCommitMonoTicks = _clock.GetTimestamp();
        }
        catch (Exception ex)
        {
            // 写入失败：保留原批次不动，暂停追加新采样，切入故障恢复模式
            _flushFailedMode = true;
            _logger.Error("SQLite 批次提交失败，暂停追加并保留原批次等待重试。", ex);
            SetState(CollectorRuntimeState.StorageError, $"账本写入失败，正在重试：{ex.Message}");
            return;
        }

        if (committedSampleUpdated)
        {
            NotifyStatusChanged();
        }
    }

    private async Task TryRecoverFailedFlushAsync(CancellationToken ct)
    {
        await _clock.DelayAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (_pendingBatch is null)
        {
            _flushFailedMode = false;
            return;
        }

        try
        {
            _ledgerWriter.CommitBatch(_pendingBatch, _clock.UtcNowMs);
            if (_pendingBatch.LastSampleUtcMs.HasValue)
            {
                _lastCommittedSampleUtcMs = _pendingBatch.LastSampleUtcMs;
            }
            _nextBatchId = _pendingBatch.BatchId + 1;
            _pendingBatch = new PendingBatch(_nextBatchId);
            _lastCommitMonoTicks = _clock.GetTimestamp();
            _flushFailedMode = false;

            // 恢复后强制重建基线，避免将写入失败期间的累计流量错误归因
            _engine.ForceResetBaseline();
            _logger.Info("SQLite 批次重试提交成功，已重建采样基线。");
            SetState(_activeClient is not null ? CollectorRuntimeState.Collecting : CollectorRuntimeState.WaitingClash, "账本写入已恢复。");
        }
        catch (Exception ex)
        {
            _logger.Warn($"SQLite 批次重试仍失败：{ex.Message}");
        }
    }

    private void FlushStatusHeartbeatIfDue(string dbStatus, string? errorMessage)
    {
        if (_flushFailedMode)
        {
            return;
        }

        if (_clock.GetElapsedTime(_lastCommitMonoTicks, _clock.GetTimestamp()).TotalSeconds < _options.CommitIntervalSeconds)
        {
            return;
        }

        _pendingBatch ??= new PendingBatch(_nextBatchId);
        _pendingBatch.CollectorStatus = dbStatus;
        _pendingBatch.ActivePipeName = _activePipeName;
        _pendingBatch.MihomoVersion = _mihomoVersion;
        _pendingBatch.CurrentRunStartUtcMs = null;
        _pendingBatch.LastErrorCode = errorMessage is not null ? dbStatus : null;
        _pendingBatch.LastErrorMessage = errorMessage;
        _pendingBatch.LastErrorUtcMs = errorMessage is not null ? _clock.UtcNowMs : null;

        TryCommitCurrentBatch();
    }

    /// <summary>
    /// 5 秒重试发起窗口的末批刷盘：到期后停止发起新尝试，已在途的单次 1 秒超时调用返回后再继续后续释锁。
    /// </summary>
    private async Task<CollectorStopResult> FlushFinalBatchWithRetryWindowAsync()
    {
        _pendingBatch ??= new PendingBatch(_nextBatchId);
        _pendingBatch.CollectorStatus = "stopped";
        _pendingBatch.CurrentRunStartUtcMs = null;
        _pendingBatch.ActiveObservedTargetsJson = null;
        _pendingBatch.LastStoppedUtcMs = _clock.UtcNowMs;

        var windowStartTicks = _clock.GetTimestamp();
        var retryWindowMs = _options.ShutdownRetryWindowSeconds * 1000.0;
        var attemptTimeoutSec = Math.Max(1, _options.ShutdownSingleAttemptTimeoutSeconds);

        Exception? lastException = null;
        var attempt = 0;

        while (true)
        {
            try
            {
                _ledgerWriter.CommitBatch(_pendingBatch, _clock.UtcNowMs, singleAttemptTimeoutSeconds: attemptTimeoutSec);
                if (_pendingBatch.LastSampleUtcMs.HasValue)
                {
                    _lastCommittedSampleUtcMs = _pendingBatch.LastSampleUtcMs;
                }
                _nextBatchId = _pendingBatch.BatchId + 1;
                _pendingBatch = new PendingBatch(_nextBatchId);
                return new CollectorStopResult(true, _lastCommittedSampleUtcMs, null);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.Error($"停止时末批刷盘第 {attempt + 1} 次失败。", ex);
            }

            var elapsedMs = _clock.GetElapsedTime(windowStartTicks, _clock.GetTimestamp()).TotalMilliseconds;
            var remainingMs = retryWindowMs - elapsedMs;
            if (remainingMs <= 0)
            {
                break;
            }

            var backoff = ShutdownBackoffMs[Math.Min(attempt, ShutdownBackoffMs.Length - 1)];
            var delayMs = Math.Min(backoff, remainingMs);
            if (delayMs > 0)
            {
                try
                {
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(delayMs), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }

            // 退避后再次检查窗口是否已到期，到期即停止发起新尝试
            if (_clock.GetElapsedTime(windowStartTicks, _clock.GetTimestamp()).TotalMilliseconds >= retryWindowMs)
            {
                break;
            }

            attempt++;
        }

        return new CollectorStopResult(
            FlushSucceeded: false,
            LastCommittedSampleUtcMs: _lastCommittedSampleUtcMs,
            ErrorMessage: lastException?.Message ?? "末批写入超时失败。");
    }

    private void ReleaseResourcesAndLock()
    {
        DisposeActiveClient();
        _ledgerWriter.CloseAndClearPool();
        _lockHandle?.Dispose();
        _lockHandle = null;
    }

    private void DisposeActiveClient()
    {
        _activeClient?.Dispose();
        _activeClient = null;
    }

    private void SetState(CollectorRuntimeState state, string? message)
    {
        _runtimeState = state;
        _lastStatusMessage = message;
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(CurrentStatus);
        }
        catch (Exception ex)
        {
            _logger.Warn($"状态变更通知回调异常，已忽略以避免影响采集循环：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        ReleaseResourcesAndLock();
        _ledgerWriter.Dispose();
    }
}
