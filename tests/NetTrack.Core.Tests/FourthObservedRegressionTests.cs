using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 第四轮复现回归用例：
/// 1. [P1] 点选无 Provider 的节点下钻与 CSV 导出不得混入其他 Provider 的同名流量
/// 2. [P2] 名单已限定 Provider 时，按裸节点名筛选不得丢掉覆盖
/// 4. [P2] Provider 名含 :: 时，排行、CSV 与下钻不得与节点名含 :: 的记录混淆
/// （第 3 项桌面摘要在 NetTrack.Desktop.Tests 中用真实 WinForms STA 窗体复现）
/// </summary>
public sealed class FourthObservedRegressionTests
{
    /// <summary>
    /// 复现缺陷 1（P1）：
    /// 名单为裸名 HK-01，数据库中有无 Provider 的 HK-01（4096 B）与 paid 下的 HK-01（8192 B）。
    /// 节点排行拆成两条，点选无 Provider 那条下钻查询与 CSV 导出时，必须只返回 4096 B（1 条），不得混算为 12288 B（2 条）。
    /// </summary>
    [Fact]
    public void FourthBug1_ClickProviderlessNodeInRanking_DrillDownAndCsvMustExcludeOtherProviders()
    {
        using var dir = new TempTestDir("fourth-bug1-providerless-drill");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "a.com", "HK-01", "")] = new BytePair(0, 4096);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "b.com", "HK-01", "paid")] = new BytePair(0, 8192);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        var options = new CollectorOptions { SelectedNodes = ["HK-01"] };

        // 先查节点排行：应分成无 Provider（4096 B）和 paid::HK-01（8192 B）两条
        var nodeRanking = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);
        Assert.Equal(2, nodeRanking.Items.Count);
        var providerlessItem = Assert.Single(nodeRanking.Items, i => i.DownloadBytes == 4096);

        // 模拟桌面点击无 Provider 这一行进行下钻筛选（如果未提供精确 FilterKey，则只能退回 Key）
        var filterProp = typeof(ReportItem).GetProperty("FilterKey");
        var drillFilterKey = (filterProp?.GetValue(providerlessItem) as string) ?? providerlessItem.Key;

        var drillReport = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: drillFilterKey), ReportScope.SelectedNodesObserved),
            options);

        // 下钻结果必须只有 4096 B，不能混入 paid::HK-01 的 8192 B
        Assert.Equal(4096, drillReport.Summary.DownloadBytes);
        Assert.Equal(4096, drillReport.Summary.TotalBytes);
        Assert.Equal("complete", drillReport.Items[0].CoverageStatus);
        Assert.Equal(3600, drillReport.Items[0].CoveredSeconds);

        // 同筛选条件的 CSV 导出也必须只有 1 条 4096 B 记录
        var csvRows = service.QueryExportRows(day, new ReportFilters(Node: drillFilterKey), ReportScope.SelectedNodesObserved, options.SelectedNodes);
        var singleCsv = Assert.Single(csvRows);
        Assert.Equal(4096, singleCsv.DownloadBytes);
        Assert.Equal("a.com", singleCsv.Domain);
    }

    /// <summary>
    /// 复现缺陷 2（P2）：
    /// 名单仅包含 paid::HK-01，查询时按裸节点名 HK-01 筛选。
    /// 字节查询返回 4096 B 时，覆盖查询也必须按「名单与筛选的交集（paid::HK-01）」计算，保留 3600 秒完整覆盖，不得显示 0 秒/no_data。
    /// </summary>
    [Fact]
    public void FourthBug2_SelectedNodesProviderScoped_FilterByBareNodeName_MustRetainCoverage()
    {
        using var dir = new TempTestDir("fourth-bug2-bare-filter-coverage");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "HK-01", "paid")] = new BytePair(0, 4096);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "paid::HK-01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        var options = new CollectorOptions { SelectedNodes = ["paid::HK-01"] };

        var report = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: "HK-01"), ReportScope.SelectedNodesObserved),
            options);

        Assert.Equal(4096, report.Summary.DownloadBytes);
        Assert.Equal("complete", report.Items[0].CoverageStatus);
        Assert.Equal(3600, report.Items[0].CoveredSeconds);
    }

    /// <summary>
    /// 复现缺陷 4（P2）：
    /// Provider 名为 paid::bundle 下的 HK-01（4096 B），与 Provider 名为 paid 下的 bundle::HK-01（8192 B），
    /// 排行和 CSV 必须严格区分成两条不同键，且各自可以精确下钻不互混。
    /// </summary>
    [Fact]
    public void FourthBug4_ProviderNameContainingDoubleColon_MustNotCollideWithNodeContainingDoubleColon()
    {
        using var dir = new TempTestDir("fourth-bug4-provider-double-colon");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "one.com", "HK-01", "paid::bundle")] = new BytePair(0, 4096);
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "two.com", "bundle::HK-01", "paid")] = new BytePair(0, 8192);
            store.CommitBatch(batch, h1);
        }

        var service = new ReportQueryService(paths, clock);
        // 同时选中两个目标：裸名 HK-01 匹配 paid::bundle 下的 HK-01；paid::bundle::HK-01 匹配 paid 下的 bundle::HK-01
        var options = new CollectorOptions { SelectedNodes = ["HK-01", "paid::bundle::HK-01"] };

        var nodeRanking = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved),
            options);

        // 排行必须分成两条，且显示键互不相同
        Assert.Equal(2, nodeRanking.Items.Count);
        Assert.NotEqual(nodeRanking.Items[0].Key, nodeRanking.Items[1].Key);

        // CSV 导出也必须有 2 条且 Node 列互不相同
        var csvRows = service.QueryExportRows(day, null, ReportScope.SelectedNodesObserved, options.SelectedNodes);
        Assert.Equal(2, csvRows.Count);
        Assert.NotEqual(csvRows[0].Node, csvRows[1].Node);

        // 分别下钻两条记录，字节必须各自独立（4096 B 和 8192 B）
        var filterProp = typeof(ReportItem).GetProperty("FilterKey");
        var item4096 = Assert.Single(nodeRanking.Items, i => i.DownloadBytes == 4096);
        var item8192 = Assert.Single(nodeRanking.Items, i => i.DownloadBytes == 8192);
        var key4096 = (filterProp?.GetValue(item4096) as string) ?? item4096.Key;
        var key8192 = (filterProp?.GetValue(item8192) as string) ?? item8192.Key;

        var drill4096 = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: key4096), ReportScope.SelectedNodesObserved),
            options);
        var drill8192 = service.QueryDailyReport(
            new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, new ReportFilters(Node: key8192), ReportScope.SelectedNodesObserved),
            options);

        Assert.Equal(4096, drill4096.Summary.DownloadBytes);
        Assert.Equal(8192, drill8192.Summary.DownloadBytes);
    }
}
