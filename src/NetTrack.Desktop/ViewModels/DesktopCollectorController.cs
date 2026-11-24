using NetTrack.Core.Abstractions;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Logging;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Storage;

namespace NetTrack.Desktop.ViewModels;

public enum DesktopCollectorMode
{
    Stopped,
    WaitingClash,
    Collecting,
    AmbiguousPipes,
    StorageError,
    StoppingFlush,
    ReadOnlyExternal,
    LockAccessError,
    ConfigError
}

public sealed record DesktopCollectorViewState(
    DesktopCollectorMode Mode,
    bool HoldsCollectorLock,
    string? ActivePipeName,
    string? MihomoVersion,
    long? LastCommittedSampleUtcMs,
    string StatusText,
    string? DetailMessage,
    bool? AttributionVerified = null,
    string? AttributionReasonText = null);

public enum ExitFlushFailureAction
{
    Retry,
    AbandonAndExit,
    CancelExit
}

/// <summary>
/// 桌面端依赖注入容器，供生产环境与 WinForms STA 测试使用。
/// </summary>
public sealed class DesktopDependencies
{
    public IClock Clock { get; init; } = SystemClock.Instance;
    public Func<IClashConfigLocator> CreateConfigLocator { get; init; } = () => new ClashVergeConfigLocator();
    public Func<IPipeEnumerator> CreatePipeEnumerator { get; init; } = () => new DefaultWindowsPipeEnumerator();
    public Func<IClock, IMihomoClientFactory> CreateClientFactory { get; init; } = clock => new MihomoPipeClientFactory(clock);
    public Func<IClock, IProxyCatalogSource> CreateCatalogSource { get; init; } = clock => new RuntimeYamlProxyCatalogSource(clock: clock);
    public Func<string, ILedgerWriter> CreateLedgerWriter { get; init; } = dbPath => new SqliteLedgerStore(dbPath);
    public Func<CollectorOptions, CollectorOptions>? OverrideOptions { get; init; }
    public Func<string, ExitFlushFailureAction>? OnExitFlushFailedPrompt { get; init; }
    public Action<string>? OnStopFlushFailedAlert { get; init; }
    public Action<string>? CopyTextToClipboard { get; init; }
}

/// <summary>
/// 桌面端后台采集状态机控制器（与窗口/托盘显示状态完全解耦）。
/// </summary>
public sealed class DesktopCollectorController : IDisposable
{
    private readonly NetTrackPaths _paths;
    private readonly DesktopDependencies _deps;
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private CollectorRunner? _runner;
    private ILedgerWriter? _ledgerWriter;
    private DesktopCollectorViewState _currentState;
    private bool _disposed;

    public event Action<DesktopCollectorViewState>? StateChanged;

    public DesktopCollectorController(NetTrackPaths paths, DesktopDependencies? deps = null)
    {
        _paths = paths;
        _deps = deps ?? new DesktopDependencies();
        _currentState = new DesktopCollectorViewState(
            DesktopCollectorMode.Stopped,
            HoldsCollectorLock: false,
            ActivePipeName: null,
            MihomoVersion: null,
            LastCommittedSampleUtcMs: null,
            StatusText: "已停止",
            DetailMessage: null);
    }

    public DesktopCollectorViewState CurrentState => _currentState;

    public CollectorOptions LoadEffectiveOptions(out string? configError)
    {
        var (settings, err) = SettingsLoader.Load(_paths.SettingsPath);
        if (settings is null)
        {
            configError = err;
            return new CollectorOptions();
        }

        configError = null;
        var opts = CollectorOptions.FromSettings(settings);
        return _deps.OverrideOptions is not null ? _deps.OverrideOptions(opts) : opts;
    }

    /// <summary>
    /// 尝试获取 collector.lock 并启动采集；任何从 Stopped 或 ReadOnlyExternal 启动都必须经过此路径。
    /// </summary>
    public async Task<DesktopCollectorViewState> TryStartCollectingAsync()
    {
        await _opGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runner is not null)
            {
                return _currentState;
            }

            var options = LoadEffectiveOptions(out var configErr);
            if (configErr is not null)
            {
                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.ConfigError,
                    false,
                    null,
                    null,
                    null,
                    "配置错误（只读）",
                    configErr));
                return _currentState;
            }

            var lockResult = CollectorFileLock.TryAcquire(_paths.LockFilePath, "desktop");
            if (lockResult.Status == CollectorLockStatus.AlreadyHeld)
            {
                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.ReadOnlyExternal,
                    false,
                    null,
                    null,
                    null,
                    "已有采集实例（只读模式）",
                    "检测到其他进程（如命令行采集器）正在写入账本，当前窗口以只读模式查询。"));
                return _currentState;
            }

            if (lockResult.Status != CollectorLockStatus.Acquired || lockResult.LockHandle is null)
            {
                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.LockAccessError,
                    false,
                    null,
                    null,
                    null,
                    "存储访问错误（只读）",
                    lockResult.ErrorMessage));
                return _currentState;
            }

            try
            {
                _paths.EnsureDirectoriesExist();
                var logger = new RollingFileLogger(_paths.LogsDirectory, _deps.Clock);
                var discovery = new PipeDiscoveryService(
                    _deps.CreateConfigLocator(),
                    _deps.CreatePipeEnumerator(),
                    _deps.CreateClientFactory(_deps.Clock),
                    TimeSpan.FromSeconds(options.HttpTimeoutSeconds));

                _ledgerWriter = _deps.CreateLedgerWriter(_paths.DatabasePath);
                _runner = new CollectorRunner(
                    options,
                    discovery,
                    _deps.CreateCatalogSource(_deps.Clock),
                    _ledgerWriter,
                    lockResult.LockHandle,
                    _deps.Clock,
                    logger);

                _runner.StatusChanged += OnRunnerStatusChanged;
                _runner.Start();
                OnRunnerStatusChanged(_runner.CurrentStatus);
            }
            catch (Exception ex)
            {
                _runner?.Dispose();
                _runner = null;
                _ledgerWriter?.Dispose();
                _ledgerWriter = null;
                lockResult.LockHandle.Dispose();

                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.StorageError,
                    false,
                    null,
                    null,
                    null,
                    "启动采集失败",
                    ex.Message));
            }

            return _currentState;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>
    /// 停止采集并刷盘。若 releaseLockOnFailure=true（用户点击「停止采集」），刷盘失败也释放锁并进入 Stopped。
    /// 若 releaseLockOnFailure=false（窗口关闭/托盘退出），失败时保留锁供用户选择「重试保存」或「放弃退出」。
    /// </summary>
    public async Task<CollectorStopResult> StopCollectingAsync(bool releaseLockOnFailure)
    {
        await _opGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runner is null)
            {
                return new CollectorStopResult(true, _currentState.LastCommittedSampleUtcMs, null);
            }

            UpdateState(_currentState with
            {
                Mode = DesktopCollectorMode.StoppingFlush,
                StatusText = "正在停止并保存末批数据…"
            });

            var result = await _runner.StopAsync(releaseLockOnFailure).ConfigureAwait(false);
            if (result.FlushSucceeded || releaseLockOnFailure)
            {
                CleanupRunnerInstances();
                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.Stopped,
                    false,
                    null,
                    null,
                    result.LastCommittedSampleUtcMs,
                    "已停止",
                    result.FlushSucceeded ? "已停止采集，历史账本可继续查询。" : $"末批未保存：{result.ErrorMessage}"));
            }
            else
            {
                UpdateState(_currentState with
                {
                    Mode = DesktopCollectorMode.StorageError,
                    HoldsCollectorLock = true,
                    StatusText = "末批保存失败",
                    DetailMessage = result.ErrorMessage
                });
            }

            return result;
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task<CollectorStopResult> RetryFinalFlushAsync()
    {
        await _opGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runner is null)
            {
                return new CollectorStopResult(true, _currentState.LastCommittedSampleUtcMs, null);
            }

            var result = await _runner.RetryFinalFlushAsync().ConfigureAwait(false);
            if (result.FlushSucceeded)
            {
                CleanupRunnerInstances();
                UpdateState(new DesktopCollectorViewState(
                    DesktopCollectorMode.Stopped,
                    false,
                    null,
                    null,
                    result.LastCommittedSampleUtcMs,
                    "已停止",
                    null));
            }
            return result;
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task AbandonPendingAndReleaseLockAsync()
    {
        await _opGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runner is not null)
            {
                _runner.AbandonPendingAndReleaseLock();
                CleanupRunnerInstances();
            }

            UpdateState(new DesktopCollectorViewState(
                DesktopCollectorMode.Stopped,
                false,
                null,
                null,
                _currentState.LastCommittedSampleUtcMs,
                "已停止",
                "已放弃未保存的末批数据。"));
        }
        finally
        {
            _opGate.Release();
        }
    }

    private void OnRunnerStatusChanged(CollectorStatusSnapshot snapshot)
    {
        var (mode, label) = snapshot.State switch
        {
            CollectorRuntimeState.Collecting when !snapshot.LastCommittedSampleUtcMs.HasValue =>
                (DesktopCollectorMode.Collecting, "已连接 Clash · 等待首批数据"),
            CollectorRuntimeState.Collecting when snapshot.AttributionVerified == false =>
                (DesktopCollectorMode.Collecting, "采集中（自动观察 · 严格归因未核验）"),
            CollectorRuntimeState.Collecting => (DesktopCollectorMode.Collecting, "采集中"),
            CollectorRuntimeState.WaitingClash => (DesktopCollectorMode.WaitingClash, "等待 Clash"),
            CollectorRuntimeState.AmbiguousPipes => (DesktopCollectorMode.AmbiguousPipes, "存在多个 Clash 管道"),
            CollectorRuntimeState.StorageError => (DesktopCollectorMode.StorageError, "账本写入异常"),
            _ => (DesktopCollectorMode.Stopped, "已停止")
        };

        UpdateState(new DesktopCollectorViewState(
            mode,
            HoldsCollectorLock: _runner?.HoldsLock ?? false,
            snapshot.ActivePipeName,
            snapshot.MihomoVersion,
            snapshot.LastCommittedSampleUtcMs,
            label,
            snapshot.AttributionReasonText ?? snapshot.Message,
            snapshot.AttributionVerified,
            snapshot.AttributionReasonText));
    }

    private void CleanupRunnerInstances()
    {
        if (_runner is not null)
        {
            _runner.StatusChanged -= OnRunnerStatusChanged;
            _runner.Dispose();
            _runner = null;
        }

        _ledgerWriter?.Dispose();
        _ledgerWriter = null;
    }

    private void UpdateState(DesktopCollectorViewState next)
    {
        _currentState = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CleanupRunnerInstances();
        _opGate.Dispose();
    }
}
