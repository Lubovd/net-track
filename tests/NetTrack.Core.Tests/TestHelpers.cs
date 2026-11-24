using System.Diagnostics;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Mihomo;

namespace NetTrack.Core.Tests;

internal sealed class TempTestDir : IDisposable
{
    public string Path { get; }

    public TempTestDir(string prefix = "nettrack-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // 忽略测试清理时的短暂句柄占用
        }
    }
}

internal sealed class FakeClock : IClock
{
    private long _utcNowMs;
    private long _monoMs;

    public FakeClock(DateTimeOffset initialUtc)
    {
        _utcNowMs = initialUtc.ToUnixTimeMilliseconds();
        _monoMs = 1_000_000L;
    }

    public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(_utcNowMs);
    public long UtcNowMs => _utcNowMs;

    public void Advance(long ms)
    {
        _utcNowMs += ms;
        _monoMs += ms;
    }

    public void AdvanceSeparate(long wallMs, long monoMs)
    {
        _utcNowMs += wallMs;
        _monoMs += monoMs;
    }

    public long GetTimestamp() => _monoMs * (Stopwatch.Frequency / 1000L);

    public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
    {
        var diffTicks = endTimestamp - startTimestamp;
        var ms = (double)diffTicks / (Stopwatch.Frequency / 1000.0);
        return TimeSpan.FromMilliseconds(ms);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance((long)Math.Max(0, delay.TotalMilliseconds));
        return Task.CompletedTask;
    }
}

internal sealed class FakeConfigLocator : IClashConfigLocator
{
    private readonly string? _pipeName;
    public FakeConfigLocator(string? pipeName) => _pipeName = pipeName;
    public string? TryReadConfiguredPipeName() => _pipeName;
}

internal sealed class FakePipeEnumerator : IPipeEnumerator
{
    private readonly IReadOnlyList<string> _pipes;
    public FakePipeEnumerator(params string[] pipes) => _pipes = pipes;
    public IEnumerable<string> EnumerateLocalPipeNames() => _pipes;
}

internal sealed class FakeMihomoClient : IMihomoClient
{
    private readonly Func<CancellationToken, Task<MihomoVersionResponse?>> _versionHandler;
    private readonly Func<CancellationToken, Task<MihomoConnectionsSnapshot>> _connectionsHandler;

    public string PipeName { get; }
    public PipeServerIdentity? LastServerIdentity { get; set; }

    public FakeMihomoClient(
        string pipeName,
        Func<CancellationToken, Task<MihomoVersionResponse?>> versionHandler,
        Func<CancellationToken, Task<MihomoConnectionsSnapshot>> connectionsHandler)
    {
        PipeName = pipeName;
        _versionHandler = versionHandler;
        _connectionsHandler = connectionsHandler;
    }

    public Task<MihomoVersionResponse?> GetVersionAsync(CancellationToken cancellationToken) =>
        _versionHandler(cancellationToken);

    public Task<MihomoConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken) =>
        _connectionsHandler(cancellationToken);

    public void Dispose() { }
}

internal sealed class FakeMihomoClientFactory : IMihomoClientFactory
{
    private readonly Func<string, TimeSpan, IMihomoClient> _factory;
    public FakeMihomoClientFactory(Func<string, TimeSpan, IMihomoClient> factory) => _factory = factory;
    public IMihomoClient Create(string pipeName, TimeSpan timeout) => _factory(pipeName, timeout);
}

internal sealed class FakeProxyCatalogSource : IProxyCatalogSource
{
    private readonly Func<ProxyCatalog> _provider;
    public FakeProxyCatalogSource(Func<ProxyCatalog> provider) => _provider = provider;
    public Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_provider());
    }
}
