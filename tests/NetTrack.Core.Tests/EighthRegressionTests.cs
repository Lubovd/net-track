using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.Core.Tests;

/// <summary>
/// 第八轮复现回归用例：
/// 2. [P2] 旧版字面名单 paid\::HK::01（Provider=paid\，Node=HK::01）升级后不得越过第一处分隔符误匹配节点名里的 ::
/// （第 1 项桌面公开导出保护在 NetTrack.Desktop.Tests 中用真实 WinForms STA 窗体复现）
/// </summary>
public sealed class EighthRegressionTests
{
    [Fact]
    public void EighthBug2_LegacyLiteralBackslashProviderWithDoubleColonInNodeName_MustPreserveProviderAndNode()
    {
        var parsed = SelectedNodeTarget.TryParse(@"paid\::HK::01");
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsProviderScoped);
        Assert.Equal(@"paid\", parsed.Provider);
        Assert.Equal("HK::01", parsed.Node);
        Assert.True(parsed.Matches("HK::01", @"paid\"));

        using var dir = new TempTestDir("eighth-bug2-legacy-bs-node-colon");
        var paths = new NetTrackPaths(dir.Path);
        var day = new DateOnly(2026, 4, 15);
        var h0 = BeijingTime.GetDayStartUtcMs(day);
        var h1 = h0 + BeijingTime.HourMs;
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(h1));

        using (var store = new SqliteLedgerStore(paths.DatabasePath))
        {
            store.Initialize();
            var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
            batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "legacy.com", "HK::01", @"paid\")] = new BytePair(0, 4096);
            batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, @"paid\::HK::01", h0, h1));
            store.CommitBatch(batch, h1);
        }

        File.WriteAllText(paths.SettingsPath, "{\"selectedNodes\":[\"paid\\\\::HK::01\"]}");
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
