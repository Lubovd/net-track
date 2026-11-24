using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NetTrack.Core.Abstractions;

namespace NetTrack.Core.Mihomo;

/// <summary>
/// Mihomo 本机命名管道只读客户端接口（仅开放无选点副作用的 /version 与 /connections）。
/// </summary>
public interface IMihomoClient : IDisposable
{
    string PipeName { get; }
    PipeServerIdentity? LastServerIdentity { get; }
    Task<MihomoVersionResponse?> GetVersionAsync(CancellationToken cancellationToken);
    Task<MihomoConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken);
}

public interface IMihomoClientFactory
{
    IMihomoClient Create(string pipeName, TimeSpan timeout);
}

/// <summary>
/// 通过 NamedPipeClientStream + SocketsHttpHandler 访问本机 Mihomo 只读 HTTP 接口。
/// </summary>
public sealed class MihomoPipeClient : IMihomoClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;
    private readonly IClock _clock;
    private readonly HttpClient _httpClient;
    private readonly object _identityLock = new();
    private uint? _lastServerPid;
    private long? _lastProcessCreationTicks;
    private string? _lastMihomoVersion;

    public string PipeName => _pipeName;

    public PipeServerIdentity? LastServerIdentity
    {
        get
        {
            lock (_identityLock)
            {
                return _lastServerPid.HasValue || _lastMihomoVersion is not null
                    ? new PipeServerIdentity(_pipeName, _lastServerPid, _lastProcessCreationTicks, _lastMihomoVersion)
                    : null;
            }
        }
    }

    public MihomoPipeClient(string pipeName, TimeSpan timeout, IClock? clock = null)
    {
        _pipeName = pipeName;
        _timeout = timeout;
        _clock = clock ?? SystemClock.Instance;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectToNamedPipeAsync
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/"),
            Timeout = Timeout.InfiniteTimeSpan // 由每次调用独立的 CancellationToken 控制超时
        };
    }

    private async ValueTask<Stream> ConnectToNamedPipeAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var pipeStream = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            await pipeStream.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            CaptureServerIdentity(pipeStream.SafePipeHandle);
            return pipeStream;
        }
        catch
        {
            pipeStream.Dispose();
            throw;
        }
    }

    public async Task<MihomoVersionResponse?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var body = await GetStringAsync("version", cancellationToken).ConfigureAwait(false);
        var parsed = MihomoJsonParser.ParseVersion(body);
        if (parsed is not null)
        {
            lock (_identityLock)
            {
                _lastMihomoVersion = parsed.Version;
            }
        }
        return parsed;
    }

    public async Task<MihomoConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var requestStartUtcMs = _clock.UtcNowMs;
        var body = await GetStringAsync("connections", cancellationToken).ConfigureAwait(false);
        var receiveUtcMs = _clock.UtcNowMs;
        var receiveMonoTicks = _clock.GetTimestamp();
        return MihomoJsonParser.ParseConnections(body, requestStartUtcMs, receiveUtcMs, receiveMonoTicks, LastServerIdentity);
    }

    private async Task<string> GetStringAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private void CaptureServerIdentity(SafePipeHandle pipeHandle)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipeHandle, out var pid))
            {
                return;
            }

            long? creationTicks = TryGetProcessCreationTimeUtcTicks(pid);
            lock (_identityLock)
            {
                _lastServerPid = pid;
                _lastProcessCreationTicks = creationTicks;
            }
        }
        catch
        {
            // 获取服务端 PID 失败时不中断请求，仅在重连时保守重建基线
        }
    }

    private static long? TryGetProcessCreationTimeUtcTicks(uint pid)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            // 普通用户无权打开 SYSTEM 服务进程时优雅降级
            return null;
        }

        try
        {
            if (GetProcessTimes(handle, out var creationTime, out _, out _, out _))
            {
                return DateTime.FromFileTimeUtc(creationTime).Ticks;
            }
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class MihomoPipeClientFactory : IMihomoClientFactory
{
    private readonly IClock _clock;

    public MihomoPipeClientFactory(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public IMihomoClient Create(string pipeName, TimeSpan timeout) =>
        new MihomoPipeClient(pipeName, timeout, _clock);
}
