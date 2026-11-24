using System.Reflection;
using System.Windows.Forms;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using NetTrack.Desktop.Coordination;
using NetTrack.Desktop.Forms;
using NetTrack.Desktop.ViewModels;
using Xunit;

namespace NetTrack.Desktop.Tests;

/// <summary>
/// 共享持久化 WinForms STA 消息循环线程，避免多线程测试中 SystemEvents 跨线程 SendMessage 死锁。
/// </summary>
public sealed class WinFormsStaFixture : IDisposable
{
    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private ApplicationContext? _appContext;
    private SynchronizationContext? _syncContext;

    public WinFormsStaFixture()
    {
        _staThread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            _appContext = new ApplicationContext();
            var anchor = new Control();
            _ = anchor.Handle; // 强制在当前 STA 线程创建消息窗口并安装 WindowsFormsSynchronizationContext
            _syncContext = SynchronizationContext.Current;
            anchor.BeginInvoke(() => _ready.Set());
            Application.Run(_appContext);
            anchor.Dispose();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public async Task RunAsync(Func<Action<string>, Task> testBody)
    {
        var lastStep = "queued";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncContext!.Post(async _ =>
        {
            lastStep = "entered";
            try
            {
                await testBody(s => lastStep = s);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (done != tcs.Task)
        {
            throw new TimeoutException($"WinForms STA 界面测试超时（5 秒），最后阶段：{lastStep}");
        }
        await tcs.Task;
    }

    public Task RunAsync(Func<Task> testBody) => RunAsync(_ => testBody());

    public void Dispose()
    {
        _syncContext?.Post(_ => _appContext?.ExitThread(), null);
        _staThread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }
}

public sealed partial class DesktopStaTests : IClassFixture<WinFormsStaFixture>
{
    private readonly WinFormsStaFixture _sta;

    public DesktopStaTests(WinFormsStaFixture sta)
    {
        _sta = sta;
    }

    [Fact]
    public Task DesktopController_StopReleasesLock_CliTakesLock_DesktopRestartBecomesReadOnly() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-desk-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var deps = new DesktopDependencies
                {
                    CreateConfigLocator = () => new EmptyConfigLocator(),
                    CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                    CreateCatalogSource = clock => new StaticCatalogSource(clock)
                };

                using var controller = new DesktopCollectorController(paths, deps);

                // 1. 启动采集：拿到锁并处于 WaitingClash
                var s1 = await controller.TryStartCollectingAsync();
                Assert.Equal(DesktopCollectorMode.WaitingClash, s1.Mode);
                Assert.True(s1.HoldsCollectorLock);

                // 2. 在 WaitingClash 状态下点击停止采集：必须释放锁并进入 Stopped
                var stopRes = await controller.StopCollectingAsync(releaseLockOnFailure: true);
                Assert.True(stopRes.FlushSucceeded);
                Assert.Equal(DesktopCollectorMode.Stopped, controller.CurrentState.Mode);
                Assert.False(controller.CurrentState.HoldsCollectorLock);

                // 3. 此时 CLI 抢先占锁
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    // 4. 桌面再次点击「开始采集」：必须重新竞争锁并切入只读模式
                    var s2 = await controller.TryStartCollectingAsync();
                    Assert.Equal(DesktopCollectorMode.ReadOnlyExternal, s2.Mode);
                    Assert.False(s2.HoldsCollectorLock);
                }

                // 5. CLI 释放锁后，桌面重试开始采集可重新接管
                var s3 = await controller.TryStartCollectingAsync();
                Assert.Equal(DesktopCollectorMode.WaitingClash, s3.Mode);
                Assert.True(s3.HoldsCollectorLock);

                await controller.StopCollectingAsync(releaseLockOnFailure: true);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    [Fact]
    public Task MainForm_TrayRestoreReflectsLiveWaitingClashState_AndSingleWindowSignalActivates() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-desk-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                using var primary = DesktopWindowCoordinator.TryCreateOrSignalExisting(paths);
                Assert.True(primary.IsPrimaryInstance);

                var activated = new ManualResetEventSlim(false);
                primary.ActivationRequested += () => activated.Set();
                primary.StartListening();

                var deps = new DesktopDependencies
                {
                    CreateConfigLocator = () => new EmptyConfigLocator(),
                    CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                    CreateCatalogSource = clock => new StaticCatalogSource(clock),
                    OnExitFlushFailedPrompt = _ => ExitFlushFailureAction.AbandonAndExit,
                    OnStopFlushFailedAlert = _ => { }
                };

                using var form = new MainForm(paths, deps);
                form.Show();
                await form.WaitForStartupAsync();

                // 确认当前处于等待 Clash，收起到托盘
                Assert.Equal(DesktopCollectorMode.WaitingClash, form.CollectorState.Mode);
                form.MinimizeToTray();
                Assert.True(form.IsHiddenInTray);

                // 模拟第二次双击启动唤醒已有窗口
                using var secondary = DesktopWindowCoordinator.TryCreateOrSignalExisting(paths);
                Assert.False(secondary.IsPrimaryInstance);
                Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));

                // 从托盘恢复后显示当前真实采集状态（等待 Clash，非硬编码「采集中」）
                form.RestoreFromTray();
                Assert.True(form.Visible);
                Assert.Equal("等待 Clash", form.StatusBadgeText);

                // 优雅停止并关闭
                var stopRes = await form.TriggerStopCollectingForTestAsync();
                Assert.True(stopRes.FlushSucceeded);
                form.Close();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    [Fact]
    public Task MainForm_WhenCliHoldsLock_OpensReadOnlyAndClosingDoesNotAffectCli() =>
        _sta.RunAsync(async step =>
        {
            step("start");
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-desk-ro-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);

                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock),
                        OnExitFlushFailedPrompt = _ => ExitFlushFailureAction.AbandonAndExit,
                        OnStopFlushFailedAlert = _ => { }
                    };

                    step("new-form");
                    using var form = new MainForm(paths, deps);
                    step("show");
                    form.Show();
                    step("wait-startup");
                    await form.WaitForStartupAsync();
                    step("assert");

                    Assert.Equal(DesktopCollectorMode.ReadOnlyExternal, form.CollectorState.Mode);
                    Assert.False(form.CollectorState.HoldsCollectorLock);

                    // 关闭只读窗口，CLI 的锁仍被持有
                    step("close");
                    form.Close();
                    step("closed");
                }

                // CLI 释放后可重新拿锁
                step("reacquire");
                var after = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, after.Status);
                after.LockHandle!.Dispose();
                step("done");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 3（P2）：
    /// 观察名单有 A 与 B，A 已记录 4096 B，B 尚未观察，因此共同覆盖为 0 秒（no_data）。
    /// 桌面顶部摘要必须展示已记录的 4.10 KB 流量并提示无共同覆盖，绝不能隐藏流量而显示「未启用观察」。
    /// </summary>
    [Fact]
    public Task FourthBug3_ObservedSummary_WhenNoCommonCoverageButBytesExist_MustShowRecordedBytesNotUnenabled() =>
        _sta.RunAsync(async step =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-fourth-bug3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);

                // 准备已包含 A=4096 B 流量与覆盖、但 B 尚未观察的账本
                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var h1 = h0 + BeijingTime.HourMs;
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "A", "sub")] = new BytePair(0, 4096);
                    batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "sub::A", h0, h1));
                    store.CommitBatch(batch, h1);
                }

                SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings
                {
                    SelectedNodes = ["sub::A", "sub::B"]
                });

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    step("new-form");
                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    // 直接构造观察报表并调用 BindReports，避免定时刷新竞态
                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions { SelectedNodes = ["sub::A", "sub::B"] };
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Hour, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.App, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Domain, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Node, ReportSortBy.Total, Scope: ReportScope.SelectedNodesObserved), options);

                    Assert.Equal("no_data", hourReport.Summary.CoverageStatus);
                    Assert.Equal(4096, hourReport.Summary.TotalBytes);

                    var bindMethod = typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(bindMethod);
                    bindMethod!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var lblField = typeof(MainForm).GetField("_lblTotalKpi", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(lblField);
                    var lbl = (Label)lblField!.GetValue(form)!;

                    // 顶部摘要必须显示已记录字节（4.10 KB），不能显示「未启用观察」
                    Assert.Contains("4.10 KB", lbl.Text);
                    Assert.DoesNotContain("暂无观察数据", ReportControl<Label>(form, "_lblSummaryTraffic").Text);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 桌面公开导出方法 ExportCurrentFilterToCsv() 指向内部 settings.json 或 traffic.sqlite3 时，
    /// 必须拒绝写入并抛出异常，原配置文件和数据库内容不得被替换。
    /// </summary>
    [Fact]
    public Task EighthBug1_ExportCurrentFilterToCsv_ToInternalSettingsFile_MustRejectAndPreserveSettings() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-eighth-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                const string originalSettings = "{\"gapThresholdSeconds\": 5}";
                File.WriteAllText(paths.SettingsPath, originalSettings);

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                }
                var originalDbBytes = File.ReadAllBytes(paths.DatabasePath);

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(paths.SettingsPath));
                    Assert.Equal(originalSettings, File.ReadAllText(paths.SettingsPath));

                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(paths.DatabasePath));
                    Assert.Equal(originalDbBytes, File.ReadAllBytes(paths.DatabasePath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷（P2）：
    /// 所选节点观察视图先成功导出一条 4096 B 记录到 CSV，再将 settings.json 改为无效 JSON；
    /// 再次调用桌面导出入口时，必须报配置错误并终止导出，原有 CSV 内容必须完整保留，绝不能被覆盖成空表头。
    /// </summary>
    [Fact]
    public Task NinthBug1_DesktopExport_WhenSettingsInvalid_MustThrowAndPreserveExistingCsv() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-ninth-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "HK-01", "paid")] = new BytePair(0, 4096);
                    batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "paid::HK-01", h0, h1));
                    store.CommitBatch(batch, h1);
                }

                SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings
                {
                    SelectedNodes = ["paid::HK-01"]
                });

                var exportPath = Path.Combine(tempDir, "observed.csv");

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    // 切换到「所选节点观察」视图
                    var scopeField = typeof(MainForm).GetField("_selectedScope", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(scopeField);
                    scopeField!.SetValue(form, ReportScope.SelectedNodesObserved);

                    // 1. 首次导出成功，包含 4096 B 记录
                    form.ExportCurrentFilterToCsv(exportPath);
                    var firstCsv = File.ReadAllText(exportPath);
                    Assert.Contains("4096", firstCsv);

                    // 2. 将 settings.json 破坏为无效 JSON
                    File.WriteAllText(paths.SettingsPath, "{ invalid json");

                    // 3. 再次导出必须抛异常终止，且已有 CSV 内容不被覆盖为空表头
                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(exportPath));
                    Assert.Equal(firstCsv, File.ReadAllText(exportPath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷（P2）：
    /// 先导出一条 4096 B 记录到 CSV，再将已存在的 settings.json 截断为 0 字节或纯空白；
    /// 再次导出必须报配置错误并保留原 CSV，绝不能当作默认空名单覆盖成空表头；
    /// 同时验证合法空名单（{}）仍可正常导出表头，配置文件缺失时仍返回默认配置。
    /// </summary>
    [Fact]
    public Task TenthBug1_DesktopExport_WhenSettingsTruncatedEmpty_MustThrowAndPreserveExistingCsv() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-tenth-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                // 配置文件缺失时仍应返回默认配置
                var (missingSettings, missingErr) = SettingsLoader.Load(paths.SettingsPath);
                Assert.NotNull(missingSettings);
                Assert.Null(missingErr);

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "HK-01", "paid")] = new BytePair(0, 4096);
                    batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "paid::HK-01", h0, h1));
                    store.CommitBatch(batch, h1);
                }

                SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings
                {
                    SelectedNodes = ["paid::HK-01"]
                });

                var exportPath = Path.Combine(tempDir, "observed.csv");

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var scopeField = typeof(MainForm).GetField("_selectedScope", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(scopeField);
                    scopeField!.SetValue(form, ReportScope.SelectedNodesObserved);

                    // 1. 首次导出成功，包含 4096 B 记录
                    form.ExportCurrentFilterToCsv(exportPath);
                    var firstCsv = File.ReadAllText(exportPath);
                    Assert.Contains("4096", firstCsv);

                    // 2. 将 settings.json 截断为 0 字节：必须抛异常终止并保留原 CSV
                    File.WriteAllText(paths.SettingsPath, string.Empty);
                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(exportPath));
                    Assert.Equal(firstCsv, File.ReadAllText(exportPath));

                    // 3. 纯空白 settings.json：同样必须抛异常终止并保留原 CSV
                    File.WriteAllText(paths.SettingsPath, "   \r\n\t  ");
                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(exportPath));
                    Assert.Equal(firstCsv, File.ReadAllText(exportPath));

                    // 4. 合法空名单 JSON 仍可正常导出表头
                    File.WriteAllText(paths.SettingsPath, "{\"selectedNodes\":[]}");
                    var emptyExportPath = Path.Combine(tempDir, "empty-valid.csv");
                    form.ExportCurrentFilterToCsv(emptyExportPath);
                    Assert.Contains("date_bj,hour_bj", File.ReadAllText(emptyExportPath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷（P3）：
    /// 当 settings.json 路径被同名目录占用时，SettingsLoader.Load 不得因 File.Exists 返回 false 而回退默认配置，
    /// 桌面导出必须报配置错误并终止，原 CSV 内容必须完整保留。
    /// </summary>
    [Fact]
    public Task EleventhBug1_DesktopExport_WhenSettingsPathIsDirectory_MustThrowAndPreserveExistingCsv() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-eleventh-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "HK-01", "paid")] = new BytePair(0, 4096);
                    batch.ObservedCoverageSegments.Add(new ObservedCoverageSegment(h0, "paid::HK-01", h0, h1));
                    store.CommitBatch(batch, h1);
                }

                SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings
                {
                    SelectedNodes = ["paid::HK-01"]
                });

                var exportPath = Path.Combine(tempDir, "observed.csv");

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var scopeField = typeof(MainForm).GetField("_selectedScope", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(scopeField);
                    scopeField!.SetValue(form, ReportScope.SelectedNodesObserved);

                    // 1. 首次导出成功，包含 4096 B 记录
                    form.ExportCurrentFilterToCsv(exportPath);
                    var firstCsv = File.ReadAllText(exportPath);
                    Assert.Contains("4096", firstCsv);

                    // 2. 删除 settings.json 并在原路径创建同名目录
                    File.Delete(paths.SettingsPath);
                    Directory.CreateDirectory(paths.SettingsPath);

                    // 3. 再次导出必须抛异常终止，且已有 CSV 内容不被覆盖为空表头
                    Assert.Throws<InvalidOperationException>(() => form.ExportCurrentFilterToCsv(exportPath));
                    Assert.Equal(firstCsv, File.ReadAllText(exportPath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 对应 TRIAL_REVIEW.md 验收项 2：
    /// 归因受阻时首页明确提示「代理归因暂不可用」与未确认字节及原因，且节点筛选下拉自动填充当天发现的出站节点。
    /// </summary>
    [Fact]
    public Task TrialReview_HomeStatusShowsAttributionBlockedReason_AndNodeFilterDropdownPopulatesAutomatically() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trial-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var dateText = BeijingTime.FormatDate(day);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "running",
                        ActivePipeName = "verge-mihomo-test",
                        MihomoVersion = "v1.19.33",
                        LastSampleUtcMs = h1,
                        AttributionVerified = false,
                        AttributionReasonCode = "config_modified_after_kernel_start",
                        AttributionReasonText = "配置文件在内核启动后发生修改（热重载后无法仅凭磁盘文件核验）"
                    };
                    batch.DiagnosticDeltas[h0] = new HourlyDiagnosticDelta(h0, 10, 1000, 4000, 0, 0, 0);
                    batch.CoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    batch.DiscoveredNodes[new DiscoveredNodeKey(dateText, "HK-Auto-01", "subA")] = (h0, h1);
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Hour), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.App), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Domain), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Node), options);
                    var discovered = query.QueryDiscoveredNodes(day);

                    var bindNodes = typeof(MainForm).GetMethod("BindDiscoveredNodes", BindingFlags.Instance | BindingFlags.NonPublic);
                    var bindReports = typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic);
                    bindNodes!.Invoke(form, [discovered]);
                    bindReports!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var lblTraffic = (Label)typeof(MainForm).GetField("_lblSummaryTraffic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var diagnostics = ReportDiagnosticsText(form);
                    var cmbNode = (ComboBox)typeof(MainForm).GetField("_cmbNodeFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    Assert.Contains("代理归因暂不可用", lblTraffic.Text);
                    Assert.Contains("5.00 KB", diagnostics);
                    Assert.Contains("归因受阻", diagnostics);
                    Assert.Equal(2, cmbNode.Items.Count);
                    Assert.Contains("HK-Auto-01", cmbNode.Items[1]!.ToString());

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 4（P2）：
    /// 在观察量口径下选中节点筛选（如 subA::HK-01）后，通过口径下拉框切回严格代理账本时，
    /// 必须清空或同步旧口径节点筛选，避免下拉显示「全部节点」但实际仍带旧筛选导出空表头。
    /// </summary>
    [Fact]
    public Task Trial2Bug4_SwitchScopeFromObservedToConfirmed_MustClearStaleNodeFilterAndNotExportEmptyHeader() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trial2-bug4-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.ProxyDeltas[new ProxyUsageKey(h0, "chrome.exe", "example.com", "HK-01")] = new BytePair(0, 4096);
                    batch.ObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "example.com", "HK-01", "subA")] = new BytePair(0, 4096);
                    batch.CoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    store.CommitBatch(batch, h1);
                }

                SettingsLoader.Save(paths.SettingsPath, new NetTrackSettings { SelectedNodes = ["subA::HK-01"] });

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var cmbScope = (ComboBox)typeof(MainForm).GetField("_cmbScope", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var filterNodeField = typeof(MainForm).GetField("_filterNode", BindingFlags.Instance | BindingFlags.NonPublic)!;

                    // 1. 切到所选节点观察量口径（索引 2），并设置筛选键 subA::HK-01
                    cmbScope.SelectedIndex = 2;
                    filterNodeField.SetValue(form, "subA::HK-01");

                    // 2. 切回严格代理账本口径（索引 1）
                    cmbScope.SelectedIndex = 1;

                    // 3. 此时 _filterNode 必须已清除，导出严格代理账本必须包含 4096 B（不能导出空表头）
                    Assert.Null(filterNodeField.GetValue(form));
                    var exportPath = Path.Combine(tempDir, "after-switch.csv");
                    form.ExportCurrentFilterToCsv(exportPath);
                    Assert.Contains("4096", File.ReadAllText(exportPath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 方案 B 桌面主视图验收：
    /// 打开即默认显示「自动节点观察」，无需手填名单；下拉选中自动发现的供应商::节点后，
    /// 立即过滤小时/应用/域名与 CSV 导出，同时首页明确标注未确认字节。
    /// </summary>
    [Fact]
    public Task PlanB_DefaultHomeViewIsAutoNodeObserved_PickDiscoveredNodeFiltersImmediatelyAndMarksUnconfirmed() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-planb-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var dateText = BeijingTime.FormatDate(day);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "running",
                        ActivePipeName = "verge-mihomo-production-1",
                        MihomoVersion = "v1.19.33",
                        LastSampleUtcMs = h1,
                        AttributionVerified = false,
                        AttributionReasonCode = "unverified_disk_yaml",
                        AttributionReasonText = "仅凭本地 YAML 无法证明当前内核加载的配置版本，已保守归入走向未确认"
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "code.exe", "api.github.com", "HK-01", "subA")] = new BytePair(1000, 4096);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "other.com", "HK-01", "subB")] = new BytePair(500, 8192);
                    batch.DiagnosticDeltas[h0] = new HourlyDiagnosticDelta(h0, 10, 1500, 12288, 0, 0, 0);
                    batch.CoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    batch.DiscoveredNodes[new DiscoveredNodeKey(dateText, "HK-01", "subA")] = (h0, h1);
                    batch.DiscoveredNodes[new DiscoveredNodeKey(dateText, "HK-01", "subB")] = (h0, h1);
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var cmbScope = (ComboBox)typeof(MainForm).GetField("_cmbScope", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var cmbNode = (ComboBox)typeof(MainForm).GetField("_cmbNodeFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var lblTraffic = (Label)typeof(MainForm).GetField("_lblSummaryTraffic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    // 默认打开即为「自动节点观察」
                    Assert.Equal(0, cmbScope.SelectedIndex);

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var discovered = query.QueryDiscoveredNodes(day);
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.App, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Domain, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindDiscoveredNodes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [discovered]);
                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    Assert.Contains("自动节点观察", lblTraffic.Text);
                    Assert.Contains("未确认", lblTraffic.Text);
                    Assert.Equal(3, cmbNode.Items.Count); // 全部节点 + subA::HK-01 + subB::HK-01

                    // 选中 subA::HK-01，立即导出 CSV 只含 subA 的 4096 B（不混入 subB 的 8192 B）
                    var subAIdx = cmbNode.Items.IndexOf("subA::HK-01");
                    Assert.True(subAIdx > 0);
                    cmbNode.SelectedIndex = subAIdx;

                    var csvPath = Path.Combine(tempDir, "planb-suba.csv");
                    form.ExportCurrentFilterToCsv(csvPath);
                    var csv = File.ReadAllText(csvPath);
                    Assert.Contains("4096", csv);
                    Assert.Contains("api.github.com", csv);
                    Assert.DoesNotContain("8192", csv);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 自动节点观察筛选单个节点 subA::HK-01（4.10 KB）时，顶部摘要绝不能把全天全局未确认量（45.06 KB）混进该节点摘要。
    /// </summary>
    [Fact]
    public Task PlanBBug1_DesktopAutoObservedSummary_WhenSingleNodeFiltered_MustNotMixGlobalUnconfirmedIntoNodeSummary() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-planb-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "running",
                        LastSampleUtcMs = h1,
                        AttributionVerified = false,
                        AttributionReasonCode = "unverified_disk_yaml",
                        AttributionReasonText = "仅凭本地 YAML 无法证明当前内核加载的配置版本"
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "code.exe", "a.com", "HK-01", "subA")] = new BytePair(0, 4096);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "b.com", "HK-02", "subB")] = new BytePair(0, 40960);
                    batch.DiagnosticDeltas[h0] = new HourlyDiagnosticDelta(h0, 10, 0, 45056, 0, 0, 0);
                    batch.CoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var filters = new ReportFilters(Node: "subA::HK-01");
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Hour, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.App, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Domain, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Node, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var total = ReportControl<Label>(form, "_lblTotalKpi");
                    Assert.Contains("4.10 KB", total.Text);
                    // 筛选单节点时绝不能把全天 45.06 KB 的全局未确认量写进该节点的摘要里
                    Assert.DoesNotContain("45.06 KB", total.Text);
                    Assert.Contains("45.06 KB", ReportDiagnosticsText(form));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 4（P2）：
    /// 在「今天」选中节点 subA::HK-01 后，切换到「昨天」（昨天只有 subB::JP-02 的流量），
    /// 必须清空旧的节点筛选，确保下拉显示「全部节点」且导出昨天的 CSV 包含 8192 B（不能只导出空表头）。
    /// </summary>
    [Fact]
    public Task PlanBBug4_SwitchDateAfterNodeFilter_MustClearStaleNodeFilterAndExportTargetDayData() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-planb-bug4-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var yesterday = today.AddDays(-1);
                var y0 = BeijingTime.GetDayStartUtcMs(yesterday);
                var y1 = y0 + BeijingTime.HourMs;
                var t0 = BeijingTime.GetDayStartUtcMs(today);
                var t1 = t0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = t1, LastStoppedUtcMs = t1 };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(y0, "chrome.exe", "yesterday.com", "JP-02", "subB")] = new BytePair(0, 8192);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(t0, "code.exe", "today.com", "HK-01", "subA")] = new BytePair(0, 4096);
                    store.CommitBatch(batch, t1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var filterNodeField = typeof(MainForm).GetField("_filterNode", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    filterNodeField.SetValue(form, "subA::HK-01");

                    // 切换到昨天
                    var setDateMethod = typeof(MainForm).GetMethod("SetSelectedDate", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    setDateMethod.Invoke(form, [yesterday]);

                    // 切日后旧节点筛选必须被清空，导出昨天 CSV 必须包含 8192 B
                    Assert.Null(filterNodeField.GetValue(form));
                    var csvPath = Path.Combine(tempDir, "yesterday.csv");
                    form.ExportCurrentFilterToCsv(csvPath);
                    Assert.Contains("8192", File.ReadAllText(csvPath));

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 在自动节点观察视图下双击节点排行里的 subA::HK-01 后，重新绑定节点下拉列表，
    /// 下拉框必须保持选中 subA::HK-01，绝不能回退显示「全部节点」。
    /// </summary>
    [Fact]
    public Task PlanB2Bug1_DoubleClickAutoObservedNodeRanking_DropdownMustStaySyncedWithSelectedNode() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-planb2-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var day = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var dateText = BeijingTime.FormatDate(day);
                var h0 = BeijingTime.GetDayStartUtcMs(day);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1) { CollectorStatus = "stopped", LastSampleUtcMs = h1, LastStoppedUtcMs = h1 };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "code.exe", "a.com", "HK-01", "subA")] = new BytePair(0, 4096);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "b.com", "HK-01", "subB")] = new BytePair(0, 8192);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    batch.DiscoveredNodes[new DiscoveredNodeKey(dateText, "HK-01", "subA")] = (h0, h1);
                    batch.DiscoveredNodes[new DiscoveredNodeKey(dateText, "HK-01", "subB")] = (h0, h1);
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var discovered = query.QueryDiscoveredNodes(day);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(day, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved), options);

                    var subAItem = Assert.Single(nodeReport.Items, i => i.DownloadBytes == 4096);
                    var pickedFilterKey = subAItem.FilterKey ?? subAItem.Key;

                    // 模拟双击节点排行 subA::HK-01 写入 _filterNode，并重新绑定下拉框
                    typeof(MainForm).GetField("_filterNode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, pickedFilterKey);
                    typeof(MainForm).GetMethod("BindDiscoveredNodes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [discovered]);

                    var cmbNode = (ComboBox)typeof(MainForm).GetField("_cmbNodeFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    Assert.Equal("subA::HK-01", cmbNode.SelectedItem?.ToString());

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 2（P3）：
    /// 昨天有 5430 B 未确认的自动观察记录，今天写入一次 AttributionVerified = true 后，
    /// 桌面查看昨天时，首页顶部摘要仍必须显示「走向未确认」，绝不能跟着最新状态显示「已核验」。
    /// </summary>
    [Fact]
    public Task PlanB3Bug2_NextDayVerifiedState_MustNotChangePreviousDayAutoObservedSummaryToVerified() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-planb3-bug2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var yesterday = today.AddDays(-1);
                var y0 = BeijingTime.GetDayStartUtcMs(yesterday);
                var y1 = y0 + BeijingTime.HourMs;
                var t0 = BeijingTime.GetDayStartUtcMs(today);
                var t1 = t0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var b1 = new PendingBatch(1)
                    {
                        CollectorStatus = "running",
                        LastSampleUtcMs = y1,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    b1.AutoObservedNodeDeltas[new ObservedNodeUsageKey(y0, "code.exe", "a.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 5430);
                    b1.AutoObservedCoverageSegments.Add(new CoverageSegment(y0, y0, y1));
                    store.CommitBatch(b1, y1);

                    var b2 = new PendingBatch(2)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = t1,
                        LastStoppedUtcMs = t1,
                        AttributionVerified = true
                    };
                    store.CommitBatch(b2, t1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(yesterday, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(yesterday, ReportGroupBy.App, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(yesterday, ReportGroupBy.Domain, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(yesterday, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var lblTraffic = (Label)typeof(MainForm).GetField("_lblSummaryTraffic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    Assert.Contains("走向未确认", lblTraffic.Text);
                    Assert.DoesNotContain("已核验", lblTraffic.Text);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 采集器首批提交落盘后，必须主动发出状态变更通知，让桌面顶部从「等待首批数据」切到「采集中」并更新提交时间。
    /// </summary>
    [Fact]
    public Task TrialUiBug1_AfterFirstBatchCommit_CollectorNotifiesStatusAndUpdatesTopBadge() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                const string pipeName = @"\\.\pipe\verge-mihomo-service-production";
                var fakeFactory = new FakeStreamingMihomoClientFactory();

                var deps = new DesktopDependencies
                {
                    CreateConfigLocator = () => new FixedConfigLocator(pipeName),
                    CreatePipeEnumerator = () => new FixedPipeEnumerator([pipeName]),
                    CreateClientFactory = _ => fakeFactory,
                    CreateCatalogSource = clock => new StaticCatalogSource(clock),
                    OverrideOptions = o => o with { PollIntervalSeconds = 1, CommitIntervalSeconds = 1 },
                    OnExitFlushFailedPrompt = _ => ExitFlushFailureAction.AbandonAndExit,
                    OnStopFlushFailedAlert = _ => { }
                };

                using var form = new MainForm(paths, deps);
                form.Show();
                await form.WaitForStartupAsync();

                // 等待首批提交落盘并同步到 UI（最多 3.5 秒）
                var committedLabel = ReportControl<Label>(form, "_lblCommittedInfo");
                string? expectedCommittedText = null;
                var deadline = DateTime.UtcNow.AddMilliseconds(3500);
                while (DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    if (form.CollectorState.LastCommittedSampleUtcMs is long committed)
                    {
                        expectedCommittedText = $"已提交：{BeijingTime.FormatDisplay(committed)}";
                        if (committedLabel.Text == expectedCommittedText)
                        {
                            break;
                        }
                    }
                    await Task.Delay(50);
                }

                Assert.NotNull(expectedCommittedText);
                Assert.Equal(expectedCommittedText, committedLabel.Text);
                Assert.DoesNotContain("等待首批数据", form.StatusBadgeText);
                Assert.Contains("采集中", form.StatusBadgeText);

                var stopRes = await form.TriggerStopCollectingForTestAsync();
                Assert.True(stopRes.FlushSucceeded);
                form.Close();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 2（P2）：
    /// 完整管道名较长时，顶部操作按钮（停止采集、收起到托盘、导出 CSV）与日期控件不得被挤出窗口或截断；
    /// 完整管道名应放入提示或详情中展示。
    /// </summary>
    [Fact]
    public Task TrialUiBug2_LongPipeName_MustNotClipActionButtonsOrDatePicker() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui-bug2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    const string longPipe = @"\\.\pipe\verge-mihomo-service-production-very-long-instance-name";
                    var state = new DesktopCollectorViewState(
                        DesktopCollectorMode.Collecting,
                        HoldsCollectorLock: true,
                        ActivePipeName: longPipe,
                        MihomoVersion: "v1.19.33",
                        LastCommittedSampleUtcMs: SystemClock.Instance.UtcNowMs,
                        StatusText: "采集中（自动观察 · 严格归因未核验）",
                        DetailMessage: null,
                        AttributionVerified: false);

                    typeof(MainForm).GetMethod("ApplyCollectorStateToUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [state]);

                    var btnToggle = (Button)typeof(MainForm).GetField("_btnToggleCollect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnTray = (Button)typeof(MainForm).GetField("_btnMinimizeToTray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnExport = (Button)typeof(MainForm).GetField("_btnExportCsv", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var datePicker = (DateTimePicker)typeof(MainForm).GetField("_datePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    foreach (var size in new[] { new Size(1160, 740), new Size(920, 600) })
                    {
                        form.Size = size;
                        form.PerformLayout();
                        Application.DoEvents();

                        var clientW = form.ClientSize.Width;
                        foreach (var ctrl in new Control[] { btnToggle, btnTray, btnExport, datePicker })
                        {
                            var boundsInForm = form.RectangleToClient(ctrl.Parent!.RectangleToScreen(ctrl.Bounds));
                            Assert.True(boundsInForm.Left >= 0, $"控件 {ctrl.Text} 左边界越界 ({size.Width}x{size.Height})。");
                            Assert.True(boundsInForm.Right <= clientW, $"控件 {ctrl.Text} 被挤出窗口右边界：Right={boundsInForm.Right} > ClientWidth={clientW} ({size.Width}x{size.Height})。");
                        }
                    }

                    Assert.Contains(longPipe, ReportDiagnosticsText(form));
                    Assert.Equal("诊断详情", ReportControl<Button>(form, "_btnDiagnostics").Text);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// 存在未知应用流量时，覆盖摘要必须标明「当日累计」口径，并提示应用归因依赖 Clash/Mihomo 进程匹配模式。
    /// </summary>
    [Fact]
    public Task TrialUiBug3_UnknownAppSummary_MustIndicateDailyCumulativeAndProcessMatchHint() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui-bug3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(today);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = h1,
                        LastStoppedUtcMs = h1,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, NetTrack.Core.Accounting.MetadataNormalizer.UnknownApp, "a.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 4096);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "b.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 4096);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.App, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Domain, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var diagnostics = ReportDiagnosticsText(form);
                    Assert.Contains("当日累计未知应用占比：50.00%", diagnostics);
                    Assert.Contains("进程匹配", diagnostics);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 200% DPI 下顶部三个按钮需要 48 px 高度时，固定高度工具栏会裁掉底部 15 px；
    /// 顶部工具栏必须随内容自动增高，保证按钮在默认与最小窗口下均无水平或垂直裁切。
    /// </summary>
    [Fact]
    public Task TrialUi2Bug1_HighDpiTallButtons_TopToolbarMustNotClipButtonsVertically() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui2-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var btnToggle = (Button)typeof(MainForm).GetField("_btnToggleCollect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnTray = (Button)typeof(MainForm).GetField("_btnMinimizeToTray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnExport = (Button)typeof(MainForm).GetField("_btnExportCsv", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    // 模拟 192 DPI (200%) 下按钮所需 48 px 高度
                    foreach (var btn in new[] { btnToggle, btnTray, btnExport })
                    {
                        btn.MinimumSize = new Size(0, 48);
                    }

                    var topPanel = btnToggle.Parent!.Parent!;
                    foreach (var size in new[] { new Size(1160, 740), new Size(920, 600) })
                    {
                        form.Size = size;
                        form.PerformLayout();
                        Application.DoEvents();

                        var clientW = form.ClientSize.Width;
                        foreach (var btn in new[] { btnToggle, btnTray, btnExport })
                        {
                            var boundsInForm = form.RectangleToClient(btn.Parent!.RectangleToScreen(btn.Bounds));
                            var boundsInTop = topPanel.RectangleToClient(btn.Parent!.RectangleToScreen(btn.Bounds));
                            Assert.True(boundsInForm.Left >= 0 && boundsInForm.Right <= clientW, $"按钮 {btn.Text} 水平越界 ({size.Width}x{size.Height})。");
                            Assert.True(btn.Height >= 48, $"按钮 {btn.Text} 高度不足 48px：实际 {btn.Height}px ({size.Width}x{size.Height})。");
                            Assert.True(boundsInTop.Top >= 0 && boundsInTop.Bottom <= topPanel.ClientSize.Height,
                                $"按钮 {btn.Text} 在顶部栏内被垂直裁切：Bottom={boundsInTop.Bottom} > TopPanelHeight={topPanel.ClientSize.Height} ({size.Width}x{size.Height})。");
                        }
                    }

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// 全天未知应用占比 50%，筛选已知应用 chrome.exe 后占比为 0.00% 时，
    /// 文案必须标明「当前筛选未知应用占比」，不得仍写成「当日累计未知应用占比」。
    /// </summary>
    [Fact]
    public Task TrialUi2Bug3_FilteredReport_UnknownAppSummaryMustStateFilteredScopeNotDailyCumulative() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui2-bug3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(today);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = h1,
                        LastStoppedUtcMs = h1,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, NetTrack.Core.Accounting.MetadataNormalizer.UnknownApp, "a.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 4096);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "b.com", "HK-01", "subA", Verified: false)] = new BytePair(0, 4096);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var filters = new ReportFilters(App: "chrome.exe");
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Hour, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.App, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Domain, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Node, Filters: filters, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var diagnostics = ReportDiagnosticsText(form);
                    Assert.Contains("当前筛选未知应用占比：0.00%", diagnostics);
                    Assert.DoesNotContain("当日累计未知应用占比", diagnostics);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 200% 缩放下（字体放大两倍），顶部栏在 1160x740 与 920x600 两个尺寸下，
    /// 必须完整显示最近提交时间，并保留足够宽度的管道名悬停区，同时三枚操作按钮无裁切。
    /// </summary>
    [Fact]
    public Task TrialUi3Bug1_HighDpi_MustShowFullCommittedTimeAndDiagnosticsEntry() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui3-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    // 模拟 192 DPI (200% 缩放，9pt = 24px 物理字高)
                    using var scaledFont = new Font(form.Font.FontFamily, 24f, GraphicsUnit.Pixel);
                    form.Font = scaledFont;

                    const string longPipe = @"\\.\pipe\verge-mihomo-service-production";
                    var state = new DesktopCollectorViewState(
                        DesktopCollectorMode.Collecting,
                        HoldsCollectorLock: true,
                        ActivePipeName: longPipe,
                        MihomoVersion: "v1.19.33",
                        LastCommittedSampleUtcMs: SystemClock.Instance.UtcNowMs,
                        StatusText: "采集中（自动观察 · 严格归因未核验）",
                        DetailMessage: null,
                        AttributionVerified: false);

                    typeof(MainForm).GetMethod("ApplyCollectorStateToUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [state]);

                    var lblCommitted = (Label)typeof(MainForm).GetField("_lblCommittedInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnDiagnostics = ReportControl<Button>(form, "_btnDiagnostics");
                    var btnToggle = (Button)typeof(MainForm).GetField("_btnToggleCollect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnTray = (Button)typeof(MainForm).GetField("_btnMinimizeToTray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var btnExport = (Button)typeof(MainForm).GetField("_btnExportCsv", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var topPanel = btnToggle.Parent!.Parent!;

                    foreach (var size in new[] { new Size(1160, 740), new Size(920, 600) })
                    {
                        form.Size = size;
                        form.PerformLayout();
                        Application.DoEvents();

                        var clientW = form.ClientSize.Width;

                        // 验证最近提交时间完整可见（不被挤出或截断）
                        var committedInForm = form.RectangleToClient(lblCommitted.Parent!.RectangleToScreen(lblCommitted.Bounds));
                        var committedInParent = lblCommitted.Bounds;
                        var visibleCommittedWidth = Math.Min(committedInForm.Right, clientW) - Math.Max(committedInForm.Left, 0);
                        var parentVisibleRight = Math.Min(lblCommitted.Parent.ClientSize.Width, committedInParent.Right);
                        var visibleInParent = parentVisibleRight - Math.Max(0, committedInParent.Left);
                        Assert.True(visibleCommittedWidth >= lblCommitted.PreferredWidth - 2 && visibleInParent >= lblCommitted.PreferredWidth - 2,
                            $"提交时间被裁切：visibleInForm={visibleCommittedWidth}, visibleInParent={visibleInParent}, preferred={lblCommitted.PreferredWidth} ({size.Width}x{size.Height})。");

                        var diagnosticsBounds = form.RectangleToClient(btnDiagnostics.Parent!.RectangleToScreen(btnDiagnostics.Bounds));
                        Assert.True(form.ClientRectangle.Contains(diagnosticsBounds),
                            $"诊断入口被裁切 ({size.Width}x{size.Height})。");

                        // 验证三个按钮均无水平或垂直裁切
                        foreach (var btn in new[] { btnToggle, btnTray, btnExport })
                        {
                            var boundsInForm = form.RectangleToClient(btn.Parent!.RectangleToScreen(btn.Bounds));
                            var boundsInTop = topPanel.RectangleToClient(btn.Parent!.RectangleToScreen(btn.Bounds));
                            Assert.True(boundsInForm.Left >= 0 && boundsInForm.Right <= clientW, $"按钮 {btn.Text} 水平越界 ({size.Width}x{size.Height})。");
                            Assert.True(boundsInTop.Top >= 0 && boundsInTop.Bottom <= topPanel.ClientSize.Height, $"按钮 {btn.Text} 垂直裁切 ({size.Width}x{size.Height})。");
                        }
                    }

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 1（P2）：
    /// 点击排行表头「合计」时，必须按真实字节数降序排序（3.22 MB 排在 950.00 KB 之前）并同步工具栏排序下拉与排序箭头；
    /// 切回「按上传排序」刷新后，「合计」列的旧箭头必须清除，不得与实际排序不一致。
    /// </summary>
    [Fact]
    public Task TrialUi4Bug1_RankingGridHeaderClick_MustSortNumericallyAndSyncToolbarAndGlyph() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui4-bug1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(today);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = h1,
                        LastStoppedUtcMs = h1,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    // small-kb.com: 上传更高 (200 B)，但合计仅 950.00 KB；large-mb.com: 上传较低 (100 B)，但合计 3.22 MB
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "small-kb.com", "HK-01", "subA", Verified: false)] = new BytePair(200, 949_800);
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "large-mb.com", "HK-01", "subA", Verified: false)] = new BytePair(100, 3_219_900);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var gridDomains = (DataGridView)typeof(MainForm).GetField("_gridDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var cmbSort = (ComboBox)typeof(MainForm).GetField("_cmbSort", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    // 等待首次加载完成（默认按上传排序：small-kb.com 在前）
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline && gridDomains.Rows.Count < 2)
                    {
                        Application.DoEvents();
                        await Task.Delay(30);
                    }
                    Assert.Equal(2, gridDomains.Rows.Count);

                    // 模拟点击「合计」列表头（第 3 列）
                    var onHeaderClick = typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    onHeaderClick.Invoke(gridDomains, [new DataGridViewCellMouseEventArgs(3, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0))]);

                    // 等待异步刷新完成
                    var sortDeadline = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < sortDeadline)
                    {
                        Application.DoEvents();
                        if (cmbSort.SelectedIndex == 2 && gridDomains.Rows.Count == 2 && gridDomains.Rows[0].Cells[0].Value?.ToString()?.StartsWith("large-mb.com", StringComparison.Ordinal) == true)
                        {
                            break;
                        }
                        await Task.Delay(30);
                    }

                    // 3.22 MB 必须排在 950.00 KB 之前，而非按字符串把 "950.00 KB" 排在前面
                    Assert.Equal(2, cmbSort.SelectedIndex);
                    Assert.StartsWith("large-mb.com", gridDomains.Rows[0].Cells[0].Value?.ToString() ?? string.Empty);
                    Assert.StartsWith("small-kb.com", gridDomains.Rows[1].Cells[0].Value?.ToString() ?? string.Empty);
                    Assert.Equal(SortOrder.Descending, gridDomains.Columns[3].HeaderCell.SortGlyphDirection);
                    Assert.Equal(SortOrder.None, gridDomains.Columns[1].HeaderCell.SortGlyphDirection);

                    // 切回按上传排序后，「合计」列的旧箭头必须清除
                    cmbSort.SelectedIndex = 0;
                    var backDeadline = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < backDeadline)
                    {
                        Application.DoEvents();
                        if (gridDomains.Rows.Count == 2 && gridDomains.Rows[0].Cells[0].Value?.ToString()?.StartsWith("small-kb.com", StringComparison.Ordinal) == true)
                        {
                            break;
                        }
                        await Task.Delay(30);
                    }

                    Assert.Equal(SortOrder.None, gridDomains.Columns[3].HeaderCell.SortGlyphDirection);
                    Assert.Equal(SortOrder.Descending, gridDomains.Columns[1].HeaderCell.SortGlyphDirection);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 2（P3）：
    /// 高 DPI 下日期控件和下拉框须显示完整，名称和覆盖列可以用剩余列宽。
    /// </summary>
    [Fact]
    public Task TrialUi4Bug2_HighDpi_DatePickerTextAndComboBoxTextMustNotBeTruncated() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui4-bug2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    using var scaledFont = new Font(form.Font.FontFamily, 24f, GraphicsUnit.Pixel);
                    form.Font = scaledFont;
                    form.PerformLayout();
                    Application.DoEvents();

                    var datePicker = (DateTimePicker)typeof(MainForm).GetField("_datePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var cmbScope = (ComboBox)typeof(MainForm).GetField("_cmbScope", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var cmbSort = (ComboBox)typeof(MainForm).GetField("_cmbSort", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var gridDomains = (DataGridView)typeof(MainForm).GetField("_gridDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    var gridHours = (DataGridView)typeof(MainForm).GetField("_gridHours", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                    var minDateWidth = TextRenderer.MeasureText("2026-11-23", datePicker.Font).Width + SystemInformation.VerticalScrollBarWidth + 16;
                    Assert.True(datePicker.Width >= minDateWidth, $"日期控件内部文字将被截断：Width={datePicker.Width} < required={minDateWidth}");

                    var minScopeWidth = TextRenderer.MeasureText("所选节点观察量", cmbScope.Font).Width + SystemInformation.VerticalScrollBarWidth + 12;
                    Assert.True(cmbScope.Width >= minScopeWidth, $"口径下拉框内部文字将被截断：Width={cmbScope.Width} < required={minScopeWidth}");

                    var minSortWidth = TextRenderer.MeasureText("按上传排序", cmbSort.Font).Width + SystemInformation.VerticalScrollBarWidth + 12;
                    Assert.True(cmbSort.Width >= minSortWidth, $"排序下拉框内部文字将被截断：Width={cmbSort.Width} < required={minSortWidth}");

                    Assert.Equal(DataGridViewAutoSizeColumnMode.Fill, gridDomains.Columns["Key"]!.AutoSizeMode);
                    Assert.True(gridHours.Columns["Coverage"]!.Width >= gridHours.Columns["Coverage"]!.HeaderCell.PreferredSize.Width);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    /// <summary>
    /// 复现缺陷 3（P3）：
    /// 自动节点观察模式下，顶部观察合计与底部全局未确认诊断量可能包含同一批流量，
    /// 诊断文案必须明确标出「严格账本」全局范围并提示可能与上方观察量重叠。
    /// </summary>
    [Fact]
    public Task TrialUi4Bug3_AutoObservedDiagnostics_MustDistinguishGlobalStrictUnconfirmedFromObservedTotal() =>
        _sta.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-trialui4-bug3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new NetTrackPaths(tempDir);
                var today = BeijingTime.GetBeijingDate(SystemClock.Instance.UtcNowMs);
                var h0 = BeijingTime.GetDayStartUtcMs(today);
                var h1 = h0 + BeijingTime.HourMs;

                using (var store = new SqliteLedgerStore(paths.DatabasePath))
                {
                    store.Initialize();
                    var batch = new PendingBatch(1)
                    {
                        CollectorStatus = "stopped",
                        LastSampleUtcMs = h1,
                        LastStoppedUtcMs = h1,
                        AttributionVerified = false,
                        AutoObservedActive = true
                    };
                    batch.AutoObservedNodeDeltas[new ObservedNodeUsageKey(h0, "chrome.exe", "a.com", "HK-01", "subA", Verified: false)] = new BytePair(43_220_000, 9_050_000);
                    batch.AutoObservedCoverageSegments.Add(new CoverageSegment(h0, h0, h1));
                    batch.DiagnosticDeltas[h0] = new HourlyDiagnosticDelta(h0, 10, 60_940_000, 15_790_000, 0, 0, 0);
                    store.CommitBatch(batch, h1);
                }

                var cliLock = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
                Assert.Equal(CollectorLockStatus.Acquired, cliLock.Status);
                using (cliLock.LockHandle!)
                {
                    var deps = new DesktopDependencies
                    {
                        CreateConfigLocator = () => new EmptyConfigLocator(),
                        CreatePipeEnumerator = () => new EmptyPipeEnumerator(),
                        CreateCatalogSource = clock => new StaticCatalogSource(clock)
                    };

                    using var form = new MainForm(paths, deps);
                    form.Show();
                    await form.WaitForStartupAsync();

                    var query = new ReportQueryService(paths);
                    var options = new CollectorOptions();
                    var hourReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Hour, Scope: ReportScope.AutoNodeObserved), options);
                    var appReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.App, Scope: ReportScope.AutoNodeObserved), options);
                    var domainReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Domain, Scope: ReportScope.AutoNodeObserved), options);
                    var nodeReport = query.QueryDailyReport(new ReportQueryRequest(today, ReportGroupBy.Node, Scope: ReportScope.AutoNodeObserved), options);

                    typeof(MainForm).GetMethod("BindReports", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [hourReport, appReport, domainReport, nodeReport]);

                    var diagnostics = ReportDiagnosticsText(form);
                    Assert.Contains("严格账本", diagnostics);
                    Assert.Contains("重叠", diagnostics);

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    private sealed class FixedConfigLocator(string pipeName) : IClashConfigLocator
    {
        public string? TryReadConfiguredPipeName() => pipeName;
    }

    private sealed class FixedPipeEnumerator(IReadOnlyList<string> pipes) : IPipeEnumerator
    {
        public IEnumerable<string> EnumerateLocalPipeNames() => pipes;
    }

    private sealed class FakeStreamingMihomoClientFactory : IMihomoClientFactory
    {
        public IMihomoClient Create(string pipeName, TimeSpan timeout) => new FakeStreamingMihomoClient(pipeName);
    }

    private sealed class FakeStreamingMihomoClient(string pipeName) : IMihomoClient
    {
        private long _tick;
        public string PipeName => pipeName;
        public PipeServerIdentity? LastServerIdentity => new(pipeName, 1234, 1, "v1.19.33");

        public Task<MihomoVersionResponse?> GetVersionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true));

        public Task<MihomoConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _tick);
            var now = SystemClock.Instance.UtcNowMs;
            var startIso = DateTimeOffset.FromUnixTimeMilliseconds(now - 500).ToString("O");
            var conns = new[]
            {
                new MihomoConnectionRecord(
                    Id: "c1",
                    Upload: n * 1024,
                    Download: n * 2048,
                    StartRaw: startIso,
                    Chains: ["HK-01", "Proxy"],
                    ProviderChains: ["subA"],
                    Host: "example.com",
                    SniffHost: null,
                    Process: "chrome.exe",
                    ProcessPath: @"C:\chrome.exe")
            };
            return Task.FromResult(new MihomoConnectionsSnapshot(
                RequestStartUtcMs: now - 5,
                ReceiveUtcMs: now,
                ReceiveMonoTicks: SystemClock.Instance.GetTimestamp(),
                ServerIdentity: LastServerIdentity,
                UploadTotal: n * 1024,
                DownloadTotal: n * 2048,
                Connections: conns));
        }

        public void Dispose() { }
    }

    private sealed class EmptyConfigLocator : IClashConfigLocator
    {
        public string? TryReadConfiguredPipeName() => null;
    }

    private sealed class EmptyPipeEnumerator : IPipeEnumerator
    {
        public IEnumerable<string> EnumerateLocalPipeNames() => Array.Empty<string>();
    }

    private sealed class StaticCatalogSource : IProxyCatalogSource
    {
        private readonly IClock _clock;
        public StaticCatalogSource(IClock clock) => _clock = clock;
        public Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyCatalog(_clock.UtcNowMs, new Dictionary<string, ProxyMemberInfo>()));
    }
}
