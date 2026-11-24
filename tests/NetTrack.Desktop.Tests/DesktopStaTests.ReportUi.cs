using System.Reflection;
using System.Windows.Forms;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Storage;
using NetTrack.Desktop.Forms;
using NetTrack.Desktop.ViewModels;
using Xunit;

namespace NetTrack.Desktop.Tests;

public sealed partial class DesktopStaTests
{
    private const string UploadFirstDomain = "long-upload-domain-for-rank.example.test";
    private const string TotalFirstDomain = "long-download-domain-for-rank.example.test";

    [Fact]
    public Task ReportUi_HighDpiKeepsBothReportsUsableAndShowsFullTrafficValues() =>
        _sta.RunAsync(() => WithCompactReportAsync((form, _) =>
        {
            using var largeFont = new Font(form.Font.FontFamily, 24, GraphicsUnit.Pixel);
            form.Font = largeFont;
            var hours = ReportControl<DataGridView>(form, "_gridHours");
            var domains = ReportControl<DataGridView>(form, "_gridDomains");
            var status = ReportControl<Label>(form, "_lblStatusBadge");

            form.Size = new Size(920, 600);
            form.PerformLayout();
            Assert.True(hours.ClientSize.Height >= hours.ColumnHeadersHeight + hours.RowTemplate.Height,
                $"最小窗口的小时表没有一行可用空间：{hours.ClientSize.Height}px。");
            Assert.True(domains.ClientSize.Height >= domains.ColumnHeadersHeight + domains.RowTemplate.Height,
                $"最小窗口的排行没有一行可用空间：{domains.ClientSize.Height}px。");
            Assert.Equal(largeFont.Unit, status.Font.Unit);
            Assert.Equal(largeFont.Size, status.Font.Size);
            Assert.True(status.Font.Bold);
            Assert.Equal(1, hours.Columns["Total"]!.DisplayIndex);
            Assert.Equal(2, hours.Columns["Coverage"]!.DisplayIndex);

            form.Size = new Size(1160, 740);
            form.PerformLayout();
            foreach (var name in new[] { "Upload", "Download", "Total" })
            {
                foreach (DataGridViewRow row in domains.Rows)
                {
                    var cell = row.Cells[name];
                    Assert.True(cell.OwningColumn!.Width >= cell.PreferredSize.Width,
                        $"排行列 {name} 裁切了 {cell.Value}。");
                }
            }
            return Task.CompletedTask;
        }));

    [Fact]
    public Task ReportUi_SplitterAndEmptyHoursToggleKeepMeasuredZeroHours() =>
        _sta.RunAsync(() => WithCompactReportAsync((form, _) =>
        {
            var divider = FindReportDivider(form);
            var width = divider.Parent!.ClientSize.Width - divider.Width;
            Assert.InRange((double)divider.SplitPosition / width, 0.37, 0.39);
            var newPosition = divider.SplitPosition + 55;
            divider.SplitPosition = newPosition;

            var hours = ReportControl<DataGridView>(form, "_gridHours");
            var hide = ReportControl<CheckBox>(form, "_chkHideEmptyHours");
            Assert.False(hide.Checked);
            Assert.Equal(24, hours.Rows.Count);
            hide.Checked = true;
            Assert.Equal(new[] { "20", "21" }, hours.Rows.Cast<DataGridViewRow>().Select(row => row.Cells["Hour"].Value as string));
            Assert.Equal("0 B", hours.Rows[0].Cells["Total"].Value);
            Assert.Contains("3000s", hours.Rows[1].Cells["Coverage"].ToolTipText);
            hide.Checked = false;
            Assert.Equal(24, hours.Rows.Count);
            Assert.Equal(newPosition, divider.SplitPosition);
            return Task.CompletedTask;
        }));

    [Fact]
    public Task ReportUi_ClassicDividerSurvivesMinimizedStartupAndZeroWidth() =>
        _sta.RunAsync(() => WithCompactReportAsync((form, _) =>
        {
            Assert.Empty(form.Controls.OfType<SplitContainer>());
            var divider = FindReportDivider(form);
            var host = divider.Parent!;
            var hoursPane = ReportControl<DataGridView>(form, "_gridHours").Parent!;
            var width = host.ClientSize.Width - divider.Width;
            Assert.InRange((double)hoursPane.Width / width, 0.37, 0.39);

            form.Hide();
            host.Dock = DockStyle.None;
            var previousLeftWidth = hoursPane.Width;
            host.Width = 0;
            Assert.Equal(previousLeftWidth, hoursPane.Width);
            host.Dock = DockStyle.Fill;
            form.Show();
            form.Size = new Size(920, 600);
            form.PerformLayout();

            width = host.ClientSize.Width - divider.Width;
            Assert.InRange((double)hoursPane.Width / width, 0.37, 0.39);
            return Task.CompletedTask;
        }, startMinimized: true));

    [Fact]
    public Task ReportUi_RefreshKeepsScrollAndManuallySizedColumns() =>
        _sta.RunAsync(() => WithCompactReportAsync(async (form, _) =>
        {
            form.Size = new Size(920, 600);
            var hours = ReportControl<DataGridView>(form, "_gridHours");
            var domains = ReportControl<DataGridView>(form, "_gridDomains");
            var hourUpload = hours.Columns["Upload"]!;
            var domainDownload = domains.Columns["Download"]!;
            var hourWidth = hourUpload.Width + 50;
            var domainWidth = domainDownload.Width + 40;
            hourUpload.Width = hourWidth;
            domainDownload.Width = domainWidth;
            hours.FirstDisplayedScrollingRowIndex = 16;
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hours.RowsAdded += (_, _) => refreshed.TrySetResult();

            ReportControl<Button>(form, "_btnRefresh").PerformClick();
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("16", hours.Rows[hours.FirstDisplayedScrollingRowIndex].Cells["Hour"].Value);
            Assert.Equal(hourWidth, hourUpload.Width);
            Assert.Equal(domainWidth, domainDownload.Width);

            var coverage = hours.Columns["Coverage"]!;
            Assert.Equal(DataGridViewAutoSizeColumnMode.Fill, coverage.AutoSizeMode);
            form.Size = new Size(1600, 850);
            var divider = FindReportDivider(form);
            var beforeExpansion = coverage.Width;
            divider.SplitPosition += 350;
            Assert.True(coverage.Width > beforeExpansion);
        }));

    [Fact]
    public Task ReportUi_DiagnosticsShowUnknownAppsAndPaidNodesOnlyInTheirScope() =>
        _sta.RunAsync(() => WithCompactReportAsync((form, _) =>
        {
            var button = ReportControl<Button>(form, "_btnDiagnostics");
            var tip = ReportControl<ToolTip>(form, "_toolTip").GetToolTip(button);
            Assert.Contains("未知应用 75%", button.Text);
            Assert.Contains("find-process-mode", tip);
            Assert.Contains("时钟异常", tip);
            Assert.Contains("对账差额", tip);

            var scope = ReportControl<ComboBox>(form, "_cmbScope");
            var selectedNodes = ReportControl<Button>(form, "_btnSelectedNodes");
            Assert.False(selectedNodes.Visible);
            scope.SelectedIndex = 2;
            Assert.True(selectedNodes.Visible);
            scope.SelectedIndex = 1;
            Assert.False(selectedNodes.Visible);
            return Task.CompletedTask;
        }));

    [Fact]
    public Task ReportUi_RankingShareUsesFilteredTotalAndCopiesFullCellWithoutSuffix() =>
        _sta.RunAsync(() =>
        {
            string? copied = null;
            return WithCompactReportAsync(async (form, _) =>
            {
                var domains = ReportControl<DataGridView>(form, "_gridDomains");
                Assert.Equal(DataGridViewCellBorderStyle.SingleHorizontal, domains.CellBorderStyle);
                Assert.Equal(DataGridViewContentAlignment.MiddleRight, domains.Columns["Share"]!.DefaultCellStyle.Alignment);
                Assert.Equal(UploadFirstDomain, domains.Rows[0].Cells["Key"].Value);
                Assert.Equal("25.00%", domains.Rows[0].Cells["Share"].Value);
                Assert.Equal(TotalFirstDomain, domains.Rows[1].Cells["Key"].Value);
                Assert.Equal("75.00%", domains.Rows[1].Cells["Share"].Value);
                Assert.True(domains.Columns["Attribution"]!.Visible);
                Assert.Equal("未确认", domains.Rows[1].Cells["Attribution"].Value);

                domains.CurrentCell = domains.Rows[0].Cells["Key"];
                var menu = ReportControl<ContextMenuStrip>(form, "_cellMenu");
                typeof(DataGridView).GetMethod("OnCellMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(domains, [new DataGridViewCellMouseEventArgs(0, 1, 5, 5, new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0))]);
                Assert.Equal(1, domains.CurrentCell?.RowIndex);
                Assert.True(domains.Rows[1].Selected);
                Assert.False(domains.Rows[0].Selected);
                menu.Items[0].PerformClick();
                menu.Close();
                Assert.Equal(TotalFirstDomain, copied);

                typeof(DataGridView).GetMethod("OnCellDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(domains, [new DataGridViewCellEventArgs(0, 1)]);
                await WaitForReportAsync(() => domains.Rows.Count == 1);
                Assert.Equal(TotalFirstDomain, domains.Rows[0].Cells["Key"].Value);
                Assert.Equal("100.00%", domains.Rows[0].Cells["Share"].Value);
                Assert.False(domains.Columns["Attribution"]!.Visible);
                Assert.Contains("走向未确认", ReportControl<Label>(form, "_lblSummaryTraffic").Text);
            }, clock => new DesktopDependencies
            {
                Clock = clock,
                CreateConfigLocator = () => new EmptyConfigLocator(),
                CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                CopyTextToClipboard = text => copied = text
            });
        });

    /// <summary>
    /// 复现初次运行内容加载不全（隐藏 Tab + 高 DPI）：
    /// 启动时默认停在 Tab 0（应用排行），高 DPI 字体生效且后台加载完成后，
    /// 用户首次切换到 Tab 1（域名排行）时，所有数值列必须完整显示且不裁切。
    /// </summary>
    [Fact]
    public Task StartupUiBug1_HiddenTabLoadedAtHighDpi_SwitchingTabMustShowFullNumericCells() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-startup-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var clock = new ReportUiClock();
                var dayStart = BeijingTime.GetDayStartUtcMs(BeijingTime.GetBeijingDate(clock.UtcNowMs));
                var trafficHour = dayStart + 21 * BeijingTime.HourMs;
                var lastSample = trafficHour + 600_000;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = lastSample,
                        LastStoppedUtcMs = lastSample,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(trafficHour, "chrome.exe", UploadFirstDomain, "HK-01", "subA", Verified: false)]
                        = new BytePair(299_000_000, 1_000_000);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(trafficHour, "code.exe", TotalFirstDomain, "HK-02", "subA", Verified: false)]
                        = new BytePair(60_000_000, 840_000_000);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(trafficHour, trafficHour, lastSample));
                    store.CommitBatch(batch, lastSample);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using var lockHandle = cliLock.LockHandle!;

                var deps = new DesktopDependencies
                {
                    Clock = clock,
                    CreateConfigLocator = () => new EmptyConfigLocator(),
                    CreatePipeEnumerator = () => new EmptyPipeEnumerator()
                };

                using var form = new MainForm(paths, deps);
                form.Show();
                await form.WaitForStartupAsync();
                ReportControl<System.Windows.Forms.Timer>(form, "_refreshTimer").Stop();

                // 模拟高 DPI 下字体放大，且此时仍停留在默认 Tab 0（域名排行从未显示）
                var tabs = ReportControl<TabControl>(form, "_tabDimensions");
                Assert.Equal(0, tabs.SelectedIndex);
                using var largeFont = new Font(form.Font.FontFamily, 24, GraphicsUnit.Pixel);
                form.Font = largeFont;

                var domains = ReportControl<DataGridView>(form, "_gridDomains");
                await WaitForReportAsync(() => domains.Rows.Count == 2);

                // 用户首次点击「域名排行」标签页
                tabs.SelectedIndex = 1;
                form.PerformLayout();
                Application.DoEvents();

                foreach (var colName in new[] { "Upload", "Download", "Total", "Share" })
                {
                    var col = domains.Columns[colName]!;
                    foreach (DataGridViewRow row in domains.Rows)
                    {
                        var cell = row.Cells[colName];
                        Assert.True(col.Width >= cell.PreferredSize.Width,
                            $"首次切到隐藏的域名排行后，列 {colName} 裁切了 {cell.Value}：Width={col.Width} < Preferred={cell.PreferredSize.Width}");
                    }
                }

                form.Close();
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            }
        });

    /// <summary>
    /// 复现高 DPI 初次启动窗口过小导致内容显示不全：
    /// 200% 缩放（192 DPI）的 4K 屏幕上，默认启动尺寸必须按 DPI 缩放并适配工作区，
    /// 不得维持未缩放的 1160x740 物理像素小窗。
    /// </summary>
    [Fact]
    public void StartupUiBug2_HighDpiInitialWindowSize_MustScaleWithDpiAndClampToWorkingArea()
    {
        var method = typeof(MainForm).GetMethod("CalculateInitialWindowSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        // 96 DPI (100%): 保持 1160x740
        var s96 = (Size)method!.Invoke(null, [96, new Rectangle(0, 0, 1920, 1032)])!;
        Assert.Equal(new Size(1160, 740), s96);

        // 192 DPI (200%) 4K 工作区：应放大到 2320x1480，而非 1160x740 小窗
        var s192_4k = (Size)method.Invoke(null, [192, new Rectangle(0, 0, 3840, 2064)])!;
        Assert.Equal(new Size(2320, 1480), s192_4k);

        // 192 DPI (200%) 2560x1500 笔记本屏幕：应夹紧在工作区 92%x90% 内，不溢出屏幕
        var s192_laptop = (Size)method.Invoke(null, [192, new Rectangle(0, 0, 2560, 1500)])!;
        Assert.True(s192_laptop.Width <= (int)(2560 * 0.92) && s192_laptop.Width >= 1800, $"200% 笔记本宽度不合理：{s192_laptop}");
        Assert.True(s192_laptop.Height <= (int)(1500 * 0.90) && s192_laptop.Height >= 1100, $"200% 笔记本高度不合理：{s192_laptop}");

        // 144 DPI (150%) 1920x1032 屏幕：放大并夹紧在工作区内
        var s144_fhd = (Size)method.Invoke(null, [144, new Rectangle(0, 0, 1920, 1032)])!;
        Assert.True(s144_fhd.Width <= (int)(1920 * 0.92) && s144_fhd.Width >= 1500, $"150% FHD 宽度不合理：{s144_fhd}");
        Assert.True(s144_fhd.Height <= (int)(1032 * 0.90) && s144_fhd.Height >= 800, $"150% FHD 高度不合理：{s144_fhd}");
    }

    private static async Task WithCompactReportAsync(
        Func<MainForm, ReportUiClock, Task> check,
        Func<ReportUiClock, DesktopDependencies>? createDependencies = null,
        bool startMinimized = false)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-report-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new NetTrackPaths(tempDir);
            var clock = new ReportUiClock();
            var dayStart = BeijingTime.GetDayStartUtcMs(BeijingTime.GetBeijingDate(clock.UtcNowMs));
            var emptyCoveredHour = dayStart + 20 * BeijingTime.HourMs;
            var trafficHour = dayStart + 21 * BeijingTime.HourMs;
            var lastSample = trafficHour + 600_000;
            using (var store = new SqliteLedgerStore(paths.DatabasePath))
            {
                store.Initialize();
                var batch = new PendingBatch(1)
                {
                    CollectorStatus = "stopped",
                    LastSampleUtcMs = lastSample,
                    LastStoppedUtcMs = lastSample,
                    AttributionVerified = false,
                    AttributionReasonText = "运行时配置声明的管道与当前连接的 Clash 管道不一致（例如服务模式覆盖）",
                    AutoObservedActive = true
                };
                batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(trafficHour, "upload.exe", UploadFirstDomain, "HK-01", "subA", Verified: true)]
                    = new BytePair(299_000_000, 1_000_000);
                batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(trafficHour, MetadataNormalizer.UnknownApp, TotalFirstDomain, "HK-02", "subA", Verified: false)]
                    = new BytePair(60_000_000, 840_000_000);
                batch.AutoObservedCoverageSegments.Add(new CoverageSegment(emptyCoveredHour, emptyCoveredHour, trafficHour));
                batch.AutoObservedCoverageSegments.Add(new CoverageSegment(trafficHour, trafficHour, lastSample));
                batch.DiagnosticDeltas[trafficHour] = new HourlyDiagnosticDelta(trafficHour, 601, 60_000_000, 840_000_000, 5_000, 0, 2);
                store.CommitBatch(batch, lastSample);
            }

            var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
            Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
            using var lockHandle = cliLock.LockHandle!;
            var deps = createDependencies?.Invoke(clock) ?? new DesktopDependencies
            {
                Clock = clock,
                CreateConfigLocator = () => new EmptyConfigLocator(),
                CreatePipeEnumerator = () => new EmptyPipeEnumerator()
            };
            using var form = new MainForm(paths, deps);
            if (startMinimized)
            {
                form.WindowState = FormWindowState.Minimized;
            }
            form.Show();
            await form.WaitForStartupAsync();
            ReportControl<System.Windows.Forms.Timer>(form, "_refreshTimer").Stop();
            if (startMinimized)
            {
                form.WindowState = FormWindowState.Normal;
                form.PerformLayout();
            }
            ReportControl<TabControl>(form, "_tabDimensions").SelectedIndex = 1;
            var domains = ReportControl<DataGridView>(form, "_gridDomains");
            await WaitForReportAsync(() => domains.Rows.Count == 2);
            await check(form, clock);
            form.Close();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WaitForReportAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(predicate(), "隔离报表没有在规定时间内刷新。");
    }

    private static T ReportControl<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static Splitter FindReportDivider(MainForm form) =>
        Assert.Single(form.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<Splitter>()));

    private static string ReportDiagnosticsText(MainForm form) =>
        (string)typeof(MainForm).GetMethod("BuildDiagnosticsText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;

    private sealed class ReportUiClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 24, 22, 35, 0, BeijingTime.Offset);
        public long UtcNowMs => UtcNow.ToUnixTimeMilliseconds();
        public long GetTimestamp() => SystemClock.Instance.GetTimestamp();
        public TimeSpan GetElapsedTime(long start, long end) => SystemClock.Instance.GetElapsedTime(start, end);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
