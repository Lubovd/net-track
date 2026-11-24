using System.Text;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Export;
using NetTrack.Core.Locking;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

public sealed class LedgerAndReportTests
{
    [Fact]
    public void SqliteLedger_IdempotentBatchCommit_90DayRetention_AndCrossMidnightSubSecondCoverage()
    {
        using var dir = new TempTestDir("ledger-midnight");
        var paths = new NetTrackPaths(dir.Path);
        using var store = new SqliteLedgerStore(paths.DatabasePath);
        store.Initialize();

        // 北京时间 2026-11-23 23:59:59.000 -> 2026-11-24 00:00:00.400（跨午夜，次日首小时覆盖仅 400ms）
        var tPrev = new DateTimeOffset(2026, 11, 23, 23, 59, 59, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var tCross = new DateTimeOffset(2026, 11, 24, 0, 0, 0, 400, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var hourDay1 = BeijingTime.GetHourStartUtcMs(tPrev);
        var hourDay2 = BeijingTime.GetHourStartUtcMs(tCross);

        // 写入一条 95 天前的过期记录
        var oldHour = BeijingTime.GetDayStartUtcMs(new DateOnly(2026, 11, 24).AddDays(-95));

        var batch1 = new PendingBatch(1)
        {
            CollectorStatus = "running",
            CurrentRunStartUtcMs = tPrev,
            LastSampleUtcMs = tCross
        };
        batch1.ProxyDeltas[new ProxyUsageKey(oldHour, "old.exe", "old.com", "HK-01")] = new BytePair(999, 999);
        batch1.ProxyDeltas[new ProxyUsageKey(hourDay1, "chrome.exe", "a.com", "HK-01")] = new BytePair(1000, 2000);
        batch1.ProxyDeltas[new ProxyUsageKey(hourDay2, "chrome.exe", "a.com", "HK-01")] = new BytePair(300, 400);
        batch1.CoverageSegments.AddRange(CoverageCalculator.SplitAcrossBeijingHours(tPrev, tCross));

        store.CommitBatch(batch1, tCross);
        // 幂等重复提交同一 batch_id，绝不重复累加
        store.CommitBatch(batch1, tCross);

        // 执行 90 天清理
        store.CleanupExpiredData(new DateOnly(2026, 11, 24), 90);

        // 在 2026-11-24 00:00:02.000 查询（距提交 1.6s，仍在待更新窗口内）
        var queryClock = new FakeClock(new DateTimeOffset(2026, 11, 24, 0, 0, 2, 0, BeijingTime.Offset));
        var queryService = new ReportQueryService(paths, queryClock);
        var opts = new CollectorOptions();

        var day1 = queryService.QueryDailyReport(new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour), opts);
        Assert.Equal(1000, day1.Summary.UploadBytes);
        Assert.Equal(2000, day1.Summary.DownloadBytes);
        Assert.Equal(day1.Summary.TotalBytes, day1.Items.Sum(i => i.TotalBytes));

        var day2 = queryService.QueryDailyReport(new ReportQueryRequest(new DateOnly(2026, 11, 24), ReportGroupBy.Hour), opts);
        Assert.Equal(300, day2.Summary.UploadBytes);
        Assert.Equal(400, day2.Summary.DownloadBytes);
        var hour00 = day2.Items[0];
        // 次日 00 点覆盖 400ms + 待更新 1600ms，不能误判为 no_data，也不能在有待更新时判为 complete
        Assert.Equal(400, hour00.CoveredMs);
        Assert.Equal(1600, hour00.PendingMs);
        Assert.Equal("updating", hour00.CoverageStatus);

        // 过期数据已被清理
        var oldDay = queryService.QueryDailyReport(new ReportQueryRequest(new DateOnly(2026, 11, 24).AddDays(-95), ReportGroupBy.App), opts);
        Assert.Equal(0, oldDay.Summary.TotalBytes);
    }

    [Fact]
    public void ResumeHeartbeatAfterGap_DoesNotMarkOldGapHoursAsPending()
    {
        using var dir = new TempTestDir("heartbeat-gap");
        var paths = new NetTrackPaths(dir.Path);
        using var store = new SqliteLedgerStore(paths.DatabasePath);
        store.Initialize();

        var t1000 = new DateTimeOffset(2026, 11, 23, 10, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var t1200 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, BeijingTime.Offset).ToUnixTimeMilliseconds();
        var t120002 = t1200 + 2000;

        // 10:00 提交了一帧采样
        var b1 = new PendingBatch(1)
        {
            CollectorStatus = "stopped",
            LastSampleUtcMs = t1000,
            CurrentRunStartUtcMs = null,
            LastStoppedUtcMs = t1000
        };
        store.CommitBatch(b1, t1000);

        // 12:00 恢复采集并先提交了一条 running 心跳（尚未提交 12:00 的首帧采样）
        var b2 = new PendingBatch(2)
        {
            CollectorStatus = "running",
            CurrentRunStartUtcMs = t1200,
            LastSampleUtcMs = null
        };
        store.CommitBatch(b2, t120002);

        var queryClock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(t120002));
        var queryService = new ReportQueryService(paths, queryClock);
        var report = queryService.QueryDailyReport(
            new ReportQueryRequest(new DateOnly(2026, 11, 23), ReportGroupBy.Hour),
            new CollectorOptions());

        // 10 点和 11 点必须是纯空档（pending=0），绝不能被 12:00 的新心跳误标为待更新
        Assert.Equal(0, report.Items[10].PendingMs);
        Assert.Equal("no_data", report.Items[10].CoverageStatus);
        Assert.Equal(0, report.Items[11].PendingMs);
        Assert.Equal("no_data", report.Items[11].CoverageStatus);

        // 只有 12:00:00–12:00:02 的短尾段为 pending
        Assert.Equal(2000, report.Items[12].PendingMs);
        Assert.Equal("pending", report.Items[12].CoverageStatus);
    }

    [Fact]
    public async Task CollectorFileLock_SingleWriterAndReadOnlyQueriesNeverBlockLockAcquisition()
    {
        using var dir1 = new TempTestDir("lock-1");
        using var dir2 = new TempTestDir("lock-2");
        var paths1 = new NetTrackPaths(dir1.Path);
        var paths2 = new NetTrackPaths(dir2.Path);

        // 先初始化账本，高并发只读查询不应挡住后续拿锁
        using (var store = new SqliteLedgerStore(paths1.DatabasePath))
        {
            store.Initialize();
        }

        var query = new ReportQueryService(paths1);
        using var stopQueries = new CancellationTokenSource();
        var readerTask = Task.Run(() =>
        {
            var opts = new CollectorOptions();
            while (!stopQueries.IsCancellationRequested)
            {
                _ = query.QueryStatus(opts);
            }
        });

        // 在只读查询并发期间，获取锁必须 100% 成功
        var first = CollectorFileLock.TryAcquire(paths1.LockFilePath, "cli");
        Assert.Equal(CollectorLockStatus.Acquired, first.Status);

        // 同目录二次获取必报 AlreadyHeld
        var second = CollectorFileLock.TryAcquire(paths1.LockFilePath, "desktop");
        Assert.Equal(CollectorLockStatus.AlreadyHeld, second.Status);

        // 不同目录独立拿锁成功
        var otherDir = CollectorFileLock.TryAcquire(paths2.LockFilePath, "cli");
        Assert.Equal(CollectorLockStatus.Acquired, otherDir.Status);
        otherDir.LockHandle!.Dispose();

        // 释放后可重新取得锁
        first.LockHandle!.Dispose();
        var reacquired = CollectorFileLock.TryAcquire(paths1.LockFilePath, "desktop");
        Assert.Equal(CollectorLockStatus.Acquired, reacquired.Status);
        reacquired.LockHandle!.Dispose();

        stopQueries.Cancel();
        await readerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CsvExporter_Utf8Bom_FixedColumns_AndFormulaGuard()
    {
        using var dir = new TempTestDir("csv-export");
        var outPath = Path.Combine(dir.Path, "report.csv");

        var rows = new List<CsvExportRow>
        {
            new("2026-11-23", "09", "chrome.exe", "example.com", "=SUM(A1),香港\"节点\"", 12345, 67890, 80235),
            new("2026-11-23", "10", "+app.exe", " -evil.com", "@node", 100, 200, 300)
        };

        CsvExporter.WriteToFile(outPath, rows);

        var bytes = File.ReadAllBytes(outPath);
        // 检查 UTF-8 BOM (EF BB BF)
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("date_bj,hour_bj,app,domain,node,upload_bytes,download_bytes,total_bytes", lines[0]);
        Assert.Equal("2026-11-23,09,chrome.exe,example.com,\"'=SUM(A1),香港\"\"节点\"\"\",12345,67890,80235", lines[1]);
        Assert.Equal("2026-11-23,10,'+app.exe,' -evil.com,'@node,100,200,300", lines[2]);

        // 空结果保留表头
        var emptyPath = Path.Combine(dir.Path, "empty.csv");
        CsvExporter.WriteToFile(emptyPath, []);
        var emptyText = File.ReadAllText(emptyPath, Encoding.UTF8);
        Assert.Equal(CsvExporter.HeaderLine + "\r\n", emptyText);
    }

    [Fact]
    public async Task ShutdownFlush_PersistentFailure_StopsInitiatingAfterRetryWindowAndReleasesLockAfterReturn()
    {
        using var dir = new TempTestDir("shutdown-fail");
        var paths = new NetTrackPaths(dir.Path);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var lockRes = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
        Assert.Equal(CollectorLockStatus.Acquired, lockRes.Status);

        var failingWriter = new AlwaysFailingCommitWriter();
        var discovery = new PipeDiscoveryService(
            new FakeConfigLocator(null),
            new FakePipeEnumerator(),
            new FakeMihomoClientFactory((_, _) => throw new InvalidOperationException()),
            TimeSpan.FromSeconds(1));

        var opts = new CollectorOptions { ShutdownRetryWindowSeconds = 5, ShutdownSingleAttemptTimeoutSeconds = 1 };
        using var runner = new CollectorRunner(
            opts,
            discovery,
            new FakeProxyCatalogSource(() => new ProxyCatalog(clock.UtcNowMs, new Dictionary<string, ProxyMemberInfo>())),
            failingWriter,
            lockRes.LockHandle!,
            clock);

        runner.Start();
        var stopResult = await runner.StopAsync(releaseLockOnFailure: true);

        Assert.False(stopResult.FlushSucceeded);
        Assert.True(failingWriter.CommitAttempts >= 2);
        Assert.True(failingWriter.PoolClosed);
        Assert.False(runner.HoldsLock);

        // 锁已在写入调用返回后安全释放，可再次获取
        var reacquire = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
        Assert.Equal(CollectorLockStatus.Acquired, reacquire.Status);
        reacquire.LockHandle!.Dispose();
    }

    private sealed class AlwaysFailingCommitWriter : ILedgerWriter
    {
        public int CommitAttempts { get; private set; }
        public bool PoolClosed { get; private set; }

        public void Initialize() { }
        public long GetLastBatchId() => 0;
        public string? GetLastRetentionCleanupDateBj() => null;
        public int CleanupExpiredData(DateOnly todayBj, int retentionDays) => 0;

        public void CommitBatch(PendingBatch batch, long heartbeatUtcMs, int? singleAttemptTimeoutSeconds = null)
        {
            CommitAttempts++;
            throw new IOException("模拟磁盘空间不足");
        }

        public void CloseAndClearPool() => PoolClosed = true;
        public void Dispose() => CloseAndClearPool();
    }
}
