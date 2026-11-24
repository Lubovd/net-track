using System.Text.Json;
using NetTrack.Cli;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Cli.Tests;

public sealed class CliAppTests
{
    [Fact]
    public async Task StatusAndReportJson_PureStdoutAndMatchesCsvExport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new NetTrackPaths(tempDir);
            var sampleTime = new DateTimeOffset(2026, 11, 23, 14, 30, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
            var hourStart = BeijingTime.GetHourStartUtcMs(sampleTime);

            using (var store = new SqliteLedgerStore(paths.DatabasePath))
            {
                store.Initialize();
                var batch = new PendingBatch(1)
                {
                    CollectorStatus = "stopped",
                    ActivePipeName = "verge-mihomo-production-abc",
                    MihomoVersion = "v1.19.33",
                    LastSampleUtcMs = sampleTime,
                    LastStoppedUtcMs = sampleTime
                };
                batch.ProxyDeltas[new ProxyUsageKey(hourStart, "chrome.exe", "example.com", "香港-01")] = new BytePair(5_000_000, 12_000_000);
                batch.CoverageSegments.Add(new CoverageSegment(hourStart, hourStart, hourStart + 1800_000));
                store.CommitBatch(batch, sampleTime);
            }

            var clock = new FixedClock(new DateTimeOffset(2026, 11, 23, 15, 0, 0, BeijingTime.Offset));
            var deps = new CliDependencies { Clock = clock };

            // 1. status --json stdout 纯净
            var statusOut = new StringWriter();
            var statusErr = new StringWriter();
            var cli1 = new CliApp(statusOut, statusErr, deps);
            var code1 = await cli1.RunAsync(["status", "--data-dir", tempDir, "--json"], CancellationToken.None);

            Assert.Equal(0, code1);
            Assert.Equal(string.Empty, statusErr.ToString());
            using var statusDoc = JsonDocument.Parse(statusOut.ToString());
            Assert.Equal("verge-mihomo-production-abc", statusDoc.RootElement.GetProperty("active_pipe_name").GetString());

            // 2. report --json stdout 纯净且数值准确
            var reportOut = new StringWriter();
            var reportErr = new StringWriter();
            var cli2 = new CliApp(reportOut, reportErr, deps);
            var code2 = await cli2.RunAsync(["report", "--data-dir", tempDir, "--date", "2026-11-23", "--by", "app", "--json"], CancellationToken.None);

            Assert.Equal(0, code2);
            Assert.Equal(string.Empty, reportErr.ToString());
            using var reportDoc = JsonDocument.Parse(reportOut.ToString());
            var summary = reportDoc.RootElement.GetProperty("summary");
            Assert.Equal(5_000_000L, summary.GetProperty("upload_bytes").GetInt64());
            Assert.Equal(12_000_000L, summary.GetProperty("download_bytes").GetInt64());

            // 3. export CSV 与 JSON 同条件数值一致
            var csvPath = Path.Combine(tempDir, "out.csv");
            var exportOut = new StringWriter();
            var exportErr = new StringWriter();
            var cli3 = new CliApp(exportOut, exportErr, deps);
            var code3 = await cli3.RunAsync(["export", "--data-dir", tempDir, "--date", "2026-11-23", "--out", csvPath], CancellationToken.None);

            Assert.Equal(0, code3);
            var csvText = File.ReadAllText(csvPath);
            Assert.Contains("2026-11-23,14,chrome.exe,example.com,香港-01,5000000,12000000,17000000", csvText);

            // 4. --scope observed 独立查询所选节点观察量（与严格账本隔离）
            var obsOut = new StringWriter();
            var obsErr = new StringWriter();
            var cli4 = new CliApp(obsOut, obsErr, deps);
            var code4 = await cli4.RunAsync(["report", "--data-dir", tempDir, "--date", "2026-11-23", "--scope", "observed", "--json"], CancellationToken.None);
            Assert.Equal(0, code4);
            Assert.Equal(string.Empty, obsErr.ToString());
            using var obsDoc = JsonDocument.Parse(obsOut.ToString());
            Assert.Equal("selected_nodes_observed", obsDoc.RootElement.GetProperty("scope").GetString());
            Assert.Equal(0L, obsDoc.RootElement.GetProperty("summary").GetProperty("total_bytes").GetInt64());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Collect_WhenLockAlreadyHeld_ReturnsExitCode3()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-cli-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new NetTrackPaths(tempDir);
            var held = CollectorFileLock.TryAcquire(paths.LockFilePath, "desktop");
            Assert.Equal(CollectorLockStatus.Acquired, held.Status);
            using var _ = held.LockHandle!;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var cli = new CliApp(stdout, stderr);
            var code = await cli.RunAsync(["collect", "--data-dir", tempDir], CancellationToken.None);

            Assert.Equal(3, code);
            Assert.Contains("已有采集实例", stderr.ToString());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 复现缺陷 1（P1）：
    /// 将 --out 指向内部文件 traffic.sqlite3、settings.json 或 collector.lock 时，
    /// CLI 必须拒绝导出返回非 0 退出码，且原文件内容不得被覆盖破坏。
    /// </summary>
    [Fact]
    public async Task FifthBug1_ExportToInternalLedgerOrSettingsFile_MustRejectAndPreserveOriginalFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-cli-protect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new NetTrackPaths(tempDir);
            var h0 = new DateTimeOffset(2026, 4, 15, 0, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();

            using (var store = new SqliteLedgerStore(paths.DatabasePath))
            {
                store.Initialize();
                var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h0 + 1000, LastStoppedUtcMs = h0 + 1000 };
                batch.ProxyDeltas[new ProxyUsageKey(h0, "chrome.exe", "example.com", "HK-01")] = new BytePair(100, 200);
                store.CommitBatch(batch, h0 + 1000);
            }

            const string originalSettings = "{\"gapThresholdSeconds\": 5}";
            File.WriteAllText(paths.SettingsPath, originalSettings);
            var originalDbBytes = File.ReadAllBytes(paths.DatabasePath);

            var deps = new CliDependencies { Clock = new FixedClock(DateTimeOffset.FromUnixTimeMilliseconds(h0 + 1000)) };

            // 1. 尝试覆盖 traffic.sqlite3：必须拒绝且数据库原样保留
            var errDb = new StringWriter();
            var cliDb = new CliApp(new StringWriter(), errDb, deps);
            var codeDb = await cliDb.RunAsync(["export", "--data-dir", tempDir, "--date", "2026-04-15", "--out", paths.DatabasePath], CancellationToken.None);
            Assert.NotEqual(0, codeDb);
            Assert.Equal(originalDbBytes, File.ReadAllBytes(paths.DatabasePath));

            // 2. 尝试覆盖 settings.json：必须拒绝且配置原样保留
            var errCfg = new StringWriter();
            var cliCfg = new CliApp(new StringWriter(), errCfg, deps);
            var codeCfg = await cliCfg.RunAsync(["export", "--data-dir", tempDir, "--date", "2026-04-15", "--out", paths.SettingsPath], CancellationToken.None);
            Assert.NotEqual(0, codeCfg);
            Assert.Equal(originalSettings, File.ReadAllText(paths.SettingsPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 复现缺陷 1（P1）：
    /// 使用 Windows 等价路径前缀 \\?\ 指向同一份 traffic.sqlite3 或 settings.json 时，
    /// CLI 导出仍必须识别为同一内部文件并拒绝写入，原文件内容不得被替换。
    /// </summary>
    [Fact]
    public async Task SixthBug1_ExportToExtendedLengthWindowsPathOfLedger_MustRejectAndPreserveDatabase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-cli-extpath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new NetTrackPaths(tempDir);
            var h0 = new DateTimeOffset(2026, 4, 15, 0, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();

            using (var store = new SqliteLedgerStore(paths.DatabasePath))
            {
                store.Initialize();
                var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h0 + 1000, LastStoppedUtcMs = h0 + 1000 };
                batch.ProxyDeltas[new ProxyUsageKey(h0, "chrome.exe", "example.com", "HK-01")] = new BytePair(100, 200);
                store.CommitBatch(batch, h0 + 1000);
            }

            const string originalSettings = "{\"gapThresholdSeconds\": 5}";
            File.WriteAllText(paths.SettingsPath, originalSettings);
            var originalDbBytes = File.ReadAllBytes(paths.DatabasePath);

            var extendedDbPath = @"\\?\" + Path.GetFullPath(paths.DatabasePath);
            var extendedSettingsPath = @"\\?\" + Path.GetFullPath(paths.SettingsPath);
            var deps = new CliDependencies { Clock = new FixedClock(DateTimeOffset.FromUnixTimeMilliseconds(h0 + 1000)) };

            var cliDb = new CliApp(new StringWriter(), new StringWriter(), deps);
            var codeDb = await cliDb.RunAsync(["export", "--data-dir", tempDir, "--date", "2026-04-15", "--out", extendedDbPath], CancellationToken.None);
            Assert.NotEqual(0, codeDb);
            Assert.Equal(originalDbBytes, File.ReadAllBytes(paths.DatabasePath));

            var cliCfg = new CliApp(new StringWriter(), new StringWriter(), deps);
            var codeCfg = await cliCfg.RunAsync(["export", "--data-dir", tempDir, "--date", "2026-04-15", "--out", extendedSettingsPath], CancellationToken.None);
            Assert.NotEqual(0, codeCfg);
            Assert.Equal(originalSettings, File.ReadAllText(paths.SettingsPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// CLI 长跑期间稳定连接并经历多次批次提交时，stderr 不得反复打印完全相同的「[Collecting] 已连接」消息。
    /// </summary>
    [Fact]
    public async Task TrialUi3Bug3_CliStableConnectionMultipleCommits_MustNotRepeatIdenticalConnectedMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-cli-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string pipeName = @"\\.\pipe\cli-dedup-test";
            var deps = new CliDependencies
            {
                CreateConfigLocator = () => new FixedPipeConfigLocator(pipeName),
                CreatePipeEnumerator = () => new FixedPipeEnumerator([pipeName]),
                CreateClientFactory = _ => new FakeCliMihomoClientFactory(),
                CreateCatalogSource = clock => new EmptyCliCatalogSource(clock),
                OverrideOptions = o => o with { PollIntervalSeconds = 1, CommitIntervalSeconds = 1 }
            };

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var cli = new CliApp(stdout, stderr, deps);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3200));
            var exitCode = await cli.RunAsync(["collect", "--data-dir", tempDir], cts.Token);
            Assert.Equal(0, exitCode);

            var lines = stderr.ToString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("[Collecting]", StringComparison.Ordinal))
                .ToList();

            Assert.Single(lines);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class FixedPipeConfigLocator(string pipeName) : IClashConfigLocator
    {
        public string? TryReadConfiguredPipeName() => pipeName;
    }

    private sealed class FixedPipeEnumerator(IReadOnlyList<string> pipes) : IPipeEnumerator
    {
        public IEnumerable<string> EnumerateLocalPipeNames() => pipes;
    }

    private sealed class EmptyCliCatalogSource(IClock clock) : IProxyCatalogSource
    {
        public Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyCatalog(clock.UtcNowMs, new Dictionary<string, ProxyMemberInfo>()));
    }

    private sealed class FakeCliMihomoClientFactory : IMihomoClientFactory
    {
        public IMihomoClient Create(string pipeName, TimeSpan timeout) => new FakeCliMihomoClient(pipeName);
    }

    private sealed class FakeCliMihomoClient(string pipeName) : IMihomoClient
    {
        private long _tick;
        public string PipeName => pipeName;
        public PipeServerIdentity? LastServerIdentity => new(pipeName, 9999, 1, "v1.19.33");

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
                UploadTotal: n * 256,
                DownloadTotal: n * 512,
                Connections:
                [
                    new MihomoConnectionRecord(
                        Id: "cli-c1",
                        Upload: n * 256,
                        Download: n * 512,
                        StartRaw: DateTimeOffset.FromUnixTimeMilliseconds(now - 100).ToString("O"),
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

    private sealed class FixedClock : IClock
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public DateTimeOffset UtcNow => _now.ToUniversalTime();
        public long UtcNowMs => _now.ToUnixTimeMilliseconds();
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp) => TimeSpan.Zero;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
