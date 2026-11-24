using NetTrack.Core.Configuration;

namespace NetTrack.Desktop.Coordination;

/// <summary>
/// 同 Windows 用户会话与同数据目录的桌面单窗口协调器。
/// 与 collector.lock 完全独立，仅负责唤起已有桌面窗口（包含收起到托盘的窗口）。
/// </summary>
public sealed class DesktopWindowCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wakeEvent;
    private readonly bool _isPrimaryInstance;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _disposed;

    public bool IsPrimaryInstance => _isPrimaryInstance;

    public event Action? ActivationRequested;

    private DesktopWindowCoordinator(Mutex mutex, EventWaitHandle wakeEvent, bool isPrimaryInstance)
    {
        _mutex = mutex;
        _wakeEvent = wakeEvent;
        _isPrimaryInstance = isPrimaryInstance;
    }

    public static DesktopWindowCoordinator TryCreateOrSignalExisting(NetTrackPaths paths)
    {
        var mutexName = $@"Local\NetTrack.Desktop.Ui.{paths.NormalizedDirectoryHash}";
        var wakeName = $@"Local\NetTrack.Desktop.Wake.{paths.NormalizedDirectoryHash}";

        var mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, wakeName);

        if (!createdNew)
        {
            // 已有同会话同目录桌面实例：发送唤醒信号
            try
            {
                wakeEvent.Set();
            }
            catch
            {
                // 忽略唤醒异常
            }

            return new DesktopWindowCoordinator(mutex, wakeEvent, isPrimaryInstance: false);
        }

        return new DesktopWindowCoordinator(mutex, wakeEvent, isPrimaryInstance: true);
    }

    public void StartListening()
    {
        if (!_isPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;
        _listenerTask = Task.Run(() =>
        {
            var handles = new[] { _wakeEvent, ct.WaitHandle };
            while (!ct.IsCancellationRequested)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0 && !ct.IsCancellationRequested)
                {
                    ActivationRequested?.Invoke();
                }
                else
                {
                    break;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listenerCts?.Cancel();
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 忽略停止监听时的竞态异常
        }
        finally
        {
            _listenerCts?.Dispose();
            _wakeEvent.Dispose();
            if (_isPrimaryInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // 忽略释放互斥量异常
                }
            }
            _mutex.Dispose();
        }
    }
}
