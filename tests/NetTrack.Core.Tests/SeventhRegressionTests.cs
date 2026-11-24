using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Export;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 第七轮复现回归用例：
/// 1. [P2] Provider 原名为 paid\:east 时，生成的精确筛选键不得被误当作旧版转义键而跳到 paid:east
/// 2. [P2] 上一版仅转义反斜杠保存的 paid\\east::HK-01，升级后必须仍能匹配原 Provider paid\east
/// 3. [边界] 数据目录存在但内部文件尚未创建时，通过等价目录路径（含父目录 OS 身份别名）导出仍必须拒绝
/// </summary>
public sealed class SeventhRegressionTests
{
    /// <summary>
    /// 复现缺陷 1（P2）：
    /// Provider "paid\:east"（4096 B）与 Provider "paid:east"（8192 B）下均有 HK-01。
    /// 选中 4096 B 这一行下钻时，必须返回 4096 B，不得误跳到 paid:east 的 8192 B。
    /// </summary>
    [Fact]
    public void SeventhBug1_ProviderContainingLiteralBackslashColon_DrillDownMustNotJumpToUnescapedSibling()
    {
        using var dir = new TempTestDir("seventh-bug1-bs-colon");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "literal.com", "HK-01", @"paid\:east")] = new BytePair(0, 4096);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "colon.com", "HK-01", "paid:east")] = new BytePair(0, 8192);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        var options = new CollectorOptions { SelectedNodes = ["HK-01"] };

        var ranking = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);
        Assert.Equal(2, ranking.Items.Count);

        var item4096 = Assert.Single(ranking.Items, i => i.DownloadBytes == 4096);
        var drillKey = item4096.FilterKey ?? item4096.Key;

        var drill = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: drillKey), ReportScope.SelectedNodesObserved),
            options);

        // 必须返回 4096 B，不能跳到 paid:east 的 8192 B
        Assert.Equal(4096, drill.Summary.DownloadBytes);
        Assert.Equal("complete", drill.Items[0].CoverageStatus);
    }

    /// <summary>
    /// 复现缺陷 2（P2）：
    /// 上一版仅对反斜杠做转义，将 Provider "paid\east" 保存为 "paid\\east::HK-01"。
    /// 升级后读取该名单，必须仍能匹配 Provider "paid\east" 已记录的 4096 B，不得变成 0 B。
    /// </summary>
    [Fact]
    public void SeventhBug2_PreviousVersionEscapedBackslashOnly_MustStillMatchOriginalProvider()
    {
        var parsed = SelectedNodeTarget.TryParse(@"paid\\east::HK-01");
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsProviderScoped);
        Assert.Equal(@"paid\east", parsed.Provider);
        Assert.Equal("HK-01", parsed.Node);
        Assert.True(parsed.Matches("HK-01", @"paid\east"));

        using var dir = new TempTestDir("seventh-bug2-escaped-bs");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "bs.com", "HK-01", @"paid\east")] = new BytePair(0, 4096);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, @"paid\\east::HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        // 写入上一版仅转义反斜杠保存的 JSON（值本身为 paid\\east::HK-01）
        File.WriteAllText(paths.SettingsPath, "{\"selectedNodes\":[\"paid\\\\\\\\east::HK-01\"]}");
        var (loaded, err) = SettingsLoader.Load(paths.SettingsPath);
        Assert.Null(err);
        var options = CollectorOptions.FromSettings(loaded!);

        var service = new ReportQueryService(paths, clock);
        var report = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);

        Assert.Equal(4096, report.Summary.DownloadBytes);
        Assert.Equal("complete", report.Items[0].CoverageStatus);
    }

    /// <summary>
    /// 复现缺陷 3（边界）：
    /// 数据目录已存在但 traffic.sqlite3 / settings.json / logs\ 尚未创建时，
    /// 经由目录等价路径（及父目录 OS 身份解析）指向这些尚未创建的内部文件名时，仍必须拒绝导出。
    /// </summary>
    [Fact]
    public void SeventhBug3_ExportToNonExistentInternalFileViaParentDirectoryIdentity_MustReject()
    {
        using var dir = new TempTestDir("seventh-bug3-nonexistent-internal");
        var paths = new NetTrackPaths(dir.Path);

        // 确认内部文件均尚未创建
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.False(File.Exists(paths.SettingsPath));
        Assert.False(Directory.Exists(paths.LogsDirectory));

        // 通过 \\?\ 前缀指向尚未创建的内部文件，以及同卷父目录 OS 身份匹配
        var nonExistentDb = @"\\?\" + Path.GetFullPath(paths.DatabasePath);
        var nonExistentWal = @"\\?\" + Path.GetFullPath(paths.DatabasePath + "-wal");
        var nonExistentSettings = @"\\?\" + Path.GetFullPath(paths.SettingsPath);
        var nonExistentLogFile = @"\\?\" + Path.GetFullPath(Path.Combine(paths.LogsDirectory, "export.csv"));

        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(nonExistentDb, paths));
        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(nonExistentWal, paths));
        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(nonExistentSettings, paths));
        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(nonExistentLogFile, paths));

        // 模拟目录联接场景：通过注入自定义目录身份解析器，将不同路径名的别名目录映射到与 DataDirectory 相同的 OS 身份
        var aliasDir = Path.Combine(Path.GetTempPath(), $"nettrack-alias-sim-{Guid.NewGuid():N}");
        var fakeDataDirIdentity = (VolumeSerial: 0x12345678u, FileIndex: 0x9999UL);
        var aliasDb = Path.Combine(aliasDir, "traffic.sqlite3");
        var aliasLog = Path.Combine(aliasDir, "logs", "a.csv");
        var aliasNormalCsv = Path.Combine(aliasDir, "report.csv");

        Func<string, (uint, ulong)?> resolver = p =>
        {
            var norm = Path.GetFullPath(p).TrimEnd('\\', '/');
            if (string.Equals(norm, Path.GetFullPath(paths.DataDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(norm, Path.GetFullPath(aliasDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return fakeDataDirIdentity;
            }
            return null;
        };

        // 别名目录下尚未创建的 traffic.sqlite3 与 logs\a.csv 必须被拒绝
        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(aliasDb, paths, resolver));
        Assert.Throws<InvalidOperationException>(() => CsvExporter.ValidateOutputPath(aliasLog, paths, resolver));
        // 别名目录下的普通 CSV 文件仍应允许
        CsvExporter.ValidateOutputPath(aliasNormalCsv, paths, resolver);
    }
}
