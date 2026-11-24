using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 第五轮复现回归用例：
/// 2. [P2] Provider 名末尾带空格时，下钻必须命中原 Provider，不得跳到去空格后的同名 Provider
/// 3. [P2] 旧版名单中的字面反斜杠（如 paid\::HK-01）升级后必须继续按 Provider=paid\、Node=HK-01 生效
/// （第 1 项内部文件覆盖防护在 NetTrack.Cli.Tests 中复现）
/// </summary>
public sealed class FifthRegressionTests
{
    /// <summary>
    /// 复现缺陷 2（P2）：
    /// Provider "paid "（末尾带空格，4096 B）与 Provider "paid"（8192 B）下都有 HK-01。
    /// 双击 4096 B 那一行的精确筛选键进行下钻时，必须返回 4096 B，不得因 Trim 空格而跳到 8192 B。
    /// </summary>
    [Fact]
    public void FifthBug2_ProviderTrailingSpace_DrillDownMustMatchExactProviderNotTrimmedSibling()
    {
        using var dir = new TempTestDir("fifth-bug2-trailing-space");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "space.com", "HK-01", "paid ")] = new BytePair(0, 4096);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "clean.com", "HK-01", "paid")] = new BytePair(0, 8192);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        var options = new CollectorOptions { SelectedNodes = ["HK-01"] };

        var ranking = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);
        Assert.Equal(2, ranking.Items.Count);

        var trailingSpaceItem = Assert.Single(ranking.Items, i => i.DownloadBytes == 4096);
        var drillKey = trailingSpaceItem.FilterKey ?? trailingSpaceItem.Key;

        var drill = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: drillKey), ReportScope.SelectedNodesObserved),
            options);

        // 下钻必须返回 4096 B，绝不能跳到 8192 B
        Assert.Equal(4096, drill.Summary.DownloadBytes);
        Assert.Equal("complete", drill.Items[0].CoverageStatus);

        var csvRows = service.QueryExportRows(day, new ReportFilters(Node: drillKey), ReportScope.SelectedNodesObserved, options.SelectedNodes);
        var single = Assert.Single(csvRows);
        Assert.Equal(4096, single.DownloadBytes);
        Assert.Equal("space.com", single.Domain);
    }

    /// <summary>
    /// 复现缺陷 3（P2）：
    /// 旧版 settings.json 保存的 "paid\::HK-01" 表示 Provider="paid\" 下的 Node="HK-01"。
    /// 新版解析时不得把字面反斜杠当作转义符吞掉分隔符，必须保持 Provider="paid\" 且继续命中观察流量。
    /// </summary>
    [Fact]
    public void FifthBug3_LegacySelectedNodeWithLiteralBackslashBeforeSeparator_MustKeepProviderScope()
    {
        var parsed = SelectedNodeTarget.TryParse(@"paid\::HK-01");
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsProviderScoped);
        Assert.Equal(@"paid\", parsed.Provider);
        Assert.Equal("HK-01", parsed.Node);
        Assert.True(parsed.Matches("HK-01", @"paid\"));
        Assert.False(parsed.Matches("HK-01", "paid"));

        using var dir = new TempTestDir("fifth-bug3-legacy-backslash");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "legacy.com", "HK-01", @"paid\")] = new BytePair(0, 4096);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, @"paid\::HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        // 从旧版配置文件加载 "paid\::HK-01"
        SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings { SelectedNodes = [@"paid\::HK-01"] });
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
}
