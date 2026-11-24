using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 第六轮复现回归用例：
/// 2. [P2] Provider 以单个冒号结尾（paid: + HK-01）不得与 Provider paid + :HK-01 碰撞合并
/// 3. [P2] 上一版保存的转义名单（paid\:\:bundle::HK-01）升级后必须继续命中 Provider paid::bundle 下的 HK-01
/// （第 1 项 Windows \\?\ 等价路径覆盖账本在 NetTrack.Cli.Tests 中复现）
/// </summary>
public sealed class SixthRegressionTests
{
    /// <summary>
    /// 复现缺陷 2（P2）：
    /// Provider "paid:" 下的 "HK-01"（4096 B）与 Provider "paid" 下的 ":HK-01"（8192 B），
    /// 排行和 CSV 必须拆分成两条不同键，且各自精确下钻不互混。
    /// </summary>
    [Fact]
    public void SixthBug2_ProviderEndingWithSingleColon_MustNotCollideWithNodeStartingWithColon()
    {
        using var dir = new TempTestDir("sixth-bug2-trailing-colon");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "one.com", "HK-01", "paid:")] = new BytePair(0, 4096);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "two.com", ":HK-01", "paid")] = new BytePair(0, 8192);
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        var options = new CollectorOptions { SelectedNodes = ["HK-01", ":HK-01"] };

        var ranking = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);

        Assert.Equal(2, ranking.Items.Count);
        Assert.NotEqual(ranking.Items[0].Key, ranking.Items[1].Key);

        var csvRows = service.QueryExportRows(day, null, ReportScope.SelectedNodesObserved, options.SelectedNodes);
        Assert.Equal(2, csvRows.Count);
        Assert.NotEqual(csvRows[0].Node, csvRows[1].Node);

        // 分别下钻两条记录，字节必须各自独立
        var item4096 = Assert.Single(ranking.Items, i => i.DownloadBytes == 4096);
        var item8192 = Assert.Single(ranking.Items, i => i.DownloadBytes == 8192);

        var drill4096 = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: item4096.FilterKey ?? item4096.Key), ReportScope.SelectedNodesObserved),
            options);
        var drill8192 = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: item8192.FilterKey ?? item8192.Key), ReportScope.SelectedNodesObserved),
            options);

        Assert.Equal(4096, drill4096.Summary.DownloadBytes);
        Assert.Equal(8192, drill8192.Summary.DownloadBytes);
    }

    /// <summary>
    /// 复现缺陷 3（P2）：
    /// 上一版保存的转义名单 "paid\:\:bundle::HK-01" 指向 Provider="paid::bundle"、Node="HK-01"。
    /// 升级后读取该配置，必须继续命中原已记录的 4096 B 观察流量，不得变成 0 B。
    /// </summary>
    [Fact]
    public void SixthBug3_PreviousVersionEscapedSelectedNode_MustStillMatchProviderWithDoubleColon()
    {
        var parsed = SelectedNodeTarget.TryParse(@"paid\:\:bundle::HK-01");
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsProviderScoped);
        Assert.Equal("paid::bundle", parsed.Provider);
        Assert.Equal("HK-01", parsed.Node);
        Assert.True(parsed.Matches("HK-01", "paid::bundle"));

        using var dir = new TempTestDir("sixth-bug3-escaped-compat");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "bundle.com", "HK-01", "paid::bundle")] = new BytePair(0, 4096);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, @"paid\:\:bundle::HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        // 直接以 JSON 写入上一版保存的转义格式名单
        File.WriteAllText(paths.SettingsPath, "{\"selectedNodes\":[\"paid\\\\:\\\\:bundle::HK-01\"]}");
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
