using System.Drawing;
using System.Windows.Forms;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Export;
using NetTrack.Core.Reporting;
using NetTrack.Desktop.ViewModels;

namespace NetTrack.Desktop.Forms;

/// <summary>
/// NetTrack WinForms 主窗口：展示采集状态、日期与维度筛选、24 小时用量、排行与 CSV 导出，支持收起到托盘。
/// 窗口显示状态与后台采集状态完全解耦。
/// </summary>
public sealed partial class MainForm : Form
{
    private const int DefaultLogicalWidth = 1160;
    private const int DefaultLogicalHeight = 740;
    private const int MinHoursPaneWidth = 160;
    private const int MinRankingPaneWidth = 200;

    private readonly NetTrackPaths _paths;
    private readonly DesktopDependencies _deps;
    private readonly DesktopCollectorController _controller;
    private readonly ReportQueryService _queryService;

    // 顶部状态栏控件
    private readonly Label _lblStatusBadge;
    private Font? _statusBadgeFont;
    private readonly Label _lblCommittedInfo;
    private readonly Button _btnToggleCollect;
    private readonly Button _btnMinimizeToTray;
    private readonly Button _btnExportCsv;
    private readonly ToolTip _toolTip;

    // 日期与筛选控件
    private readonly Button _btnToday;
    private readonly Button _btnYesterday;
    private readonly DateTimePicker _datePicker;
    private readonly ComboBox _cmbNodeFilter;
    private readonly ComboBox _cmbScope;
    private readonly ComboBox _cmbSort;
    private readonly Button _btnSelectedNodes;
    private readonly Label _lblActiveFilters;
    private readonly Button _btnClearFilters;
    private readonly Button _btnRefresh;

    private IReadOnlyList<DiscoveredNodeOption> _discoveredNodes = Array.Empty<DiscoveredNodeOption>();
    private bool _suppressNodeComboEvent;
    private long? _lastSeenCommittedUtcMs;

    // 总览摘要控件
    private readonly Label _lblSummaryTraffic;
    private readonly Label _lblUploadKpi;
    private readonly Label _lblDownloadKpi;
    private readonly Label _lblTotalKpi;
    private readonly Button _btnDiagnostics;
    private DesktopCollectorViewState? _displayedCollectorState;
    private string _coverageDetails = "暂无覆盖数据";
    private string? _reportDiagnostics;
    private long? _displayedCommitUtcMs;

    // 数据网格
    private readonly Panel _reportHost;
    private readonly Panel _hoursPane;
    private readonly Splitter _reportDivider;
    private double _hoursPaneRatio = 0.38;
    private readonly CheckBox _chkHideEmptyHours;
    private readonly DataGridView _gridHours;
    private readonly TabControl _tabDimensions;
    private readonly DataGridView _gridApps;
    private readonly DataGridView _gridDomains;
    private readonly DataGridView _gridNodes;
    private readonly ContextMenuStrip _cellMenu;
    private (string Date, string Scope, string Sort, ReportFilterView Filters)? _renderedReport;
    private IReadOnlyList<ReportItem> _hourItems = [];
    private string? _cellCopyText;

    // 系统托盘与刷新定时器
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private DateOnly _selectedDateBj;
    private ReportScope _selectedScope = ReportScope.AutoNodeObserved;
    private ReportSortBy _selectedSort = ReportSortBy.Upload;
    private string? _filterApp;
    private string? _filterDomain;
    private string? _filterNode;

    private CancellationTokenSource? _queryCts;
    private bool _exitConfirmed;
    private bool _isShuttingDown;
    private bool _formDisposed;
    private bool _initialDpiSizeApplied;

    public MainForm(NetTrackPaths paths, DesktopDependencies? deps = null)
    {
        _paths = paths;
        _deps = deps ?? new DesktopDependencies();
        _controller = new DesktopCollectorController(paths, _deps);
        _queryService = new ReportQueryService(paths, _deps.Clock);
        _selectedDateBj = BeijingTime.GetBeijingDate(_deps.Clock.UtcNowMs);

        Text = "NetTrack - 代理流量账本";
        Size = new Size(1160, 740);
        MinimumSize = new Size(920, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _toolTip = new ToolTip
        {
            AutoPopDelay = 12000,
            InitialDelay = 300,
            ReshowDelay = 150,
            ShowAlways = true
        };

        // 顶栏保留采集状态、操作和提交时间。技术细节放在诊断窗口。
        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 56),
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8, 6, 8, 4),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblStatusBadge = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(6, 4, 6, 4),
            Margin = new Padding(0, 2, 6, 2),
            BackColor = Color.LightGray,
            Text = "初始化中…"
        };
        UpdateStatusBadgeFont();

        var actionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        _btnToggleCollect = new Button { Text = "停止采集", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _btnMinimizeToTray = new Button { Text = "收起到托盘", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _btnExportCsv = new Button { Text = "导出 CSV", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };

        _btnToggleCollect.Click += async (_, _) => await ToggleCollectingAsync();
        _btnMinimizeToTray.Click += (_, _) => MinimizeToTray();
        _btnExportCsv.Click += async (_, _) => await ExportCsvInteractiveAsync();

        actionsPanel.Controls.AddRange([_btnToggleCollect, _btnMinimizeToTray, _btnExportCsv]);

        _lblCommittedInfo = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 2, 10, 2),
            Margin = Padding.Empty,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = "已提交：—"
        };

        topPanel.Controls.Add(_lblStatusBadge, 0, 0);
        topPanel.Controls.Add(actionsPanel, 1, 0);
        topPanel.Controls.Add(_lblCommittedInfo, 0, 1);
        topPanel.SetColumnSpan(_lblCommittedInfo, 2);

        // 日期与筛选栏：支持窄窗自动换行，日期采用 yyyy-MM-dd 固定格式避免截断
        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 38),
            Padding = new Padding(8, 4, 8, 4),
            WrapContents = true
        };

        _btnToday = new Button { Text = "今天", AutoSize = true };
        _btnYesterday = new Button { Text = "昨天", AutoSize = true };
        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd",
            Width = 132,
            Value = _selectedDateBj.ToDateTime(TimeOnly.MinValue)
        };

        _cmbNodeFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        _cmbNodeFilter.Items.Add("全部节点");
        _cmbNodeFilter.SelectedIndex = 0;

        _cmbScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _cmbScope.Items.AddRange(["自动节点观察", "严格代理账本", "所选节点观察量"]);
        _cmbScope.SelectedIndex = 0;

        _cmbSort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        _cmbSort.Items.AddRange(["按上传排序", "按下载排序", "按合计排序"]);
        _cmbSort.SelectedIndex = 0;

        _btnSelectedNodes = new Button { Text = "付费节点…", AutoSize = true, Visible = false };
        _lblActiveFilters = new Label { AutoSize = true, Padding = new Padding(8, 6, 4, 2), Visible = false };
        _btnClearFilters = new Button { Text = "清除筛选", AutoSize = true, Enabled = false, Visible = false };
        _btnRefresh = new Button { Text = "刷新", AutoSize = true };

        _btnToday.Click += (_, _) => SetSelectedDate(BeijingTime.GetBeijingDate(_deps.Clock.UtcNowMs));
        _btnYesterday.Click += (_, _) => SetSelectedDate(BeijingTime.GetBeijingDate(_deps.Clock.UtcNowMs).AddDays(-1));
        _datePicker.ValueChanged += (_, _) =>
        {
            var picked = DateOnly.FromDateTime(_datePicker.Value);
            if (picked != _selectedDateBj)
            {
                _selectedDateBj = picked;
                ClearNodeFilterSelection();
                TriggerRefreshReport();
            }
        };
        _cmbNodeFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressNodeComboEvent)
            {
                return;
            }

            var idx = _cmbNodeFilter.SelectedIndex;
            if (idx <= 0 || idx - 1 >= _discoveredNodes.Count)
            {
                _filterNode = null;
            }
            else
            {
                var opt = _discoveredNodes[idx - 1];
                _filterNode = _selectedScope == ReportScope.ConfirmedProxy
                    ? opt.ConfirmedFilterKey
                    : opt.ObservedFilterKey;
            }
            _toolTip.SetToolTip(_cmbNodeFilter, _cmbNodeFilter.SelectedItem as string);
            OnFilterPicked();
        };
        _cmbNodeFilter.DropDown += (_, _) =>
        {
            var maxTextWidth = _cmbNodeFilter.Items.Cast<string>()
                .Select(item => TextRenderer.MeasureText(item, _cmbNodeFilter.Font).Width)
                .DefaultIfEmpty(0)
                .Max();
            _cmbNodeFilter.DropDownWidth = Math.Min(
                Math.Max(_cmbNodeFilter.Width, maxTextWidth + SystemInformation.VerticalScrollBarWidth + 12),
                Screen.FromControl(this).WorkingArea.Width);
        };
        _cmbScope.SelectedIndexChanged += (_, _) =>
        {
            _selectedScope = _cmbScope.SelectedIndex switch
            {
                1 => ReportScope.ConfirmedProxy,
                2 => ReportScope.SelectedNodesObserved,
                _ => ReportScope.AutoNodeObserved
            };
            _btnSelectedNodes.Visible = _selectedScope == ReportScope.SelectedNodesObserved;
            // 切换口径时清空旧口径节点筛选并复位下拉框，防止下拉显示「全部节点」但后台仍带旧筛选键
            ClearNodeFilterSelection();
            TriggerRefreshReport();
        };
        _cmbSort.SelectedIndexChanged += (_, _) =>
        {
            _selectedSort = _cmbSort.SelectedIndex switch
            {
                1 => ReportSortBy.Download,
                2 => ReportSortBy.Total,
                _ => ReportSortBy.Upload
            };
            TriggerRefreshReport();
        };
        _btnSelectedNodes.Click += (_, _) => EditSelectedNodesDialog();
        _btnClearFilters.Click += (_, _) =>
        {
            _filterApp = null;
            _filterDomain = null;
            _filterNode = null;
            _suppressNodeComboEvent = true;
            if (_cmbNodeFilter.Items.Count > 0)
            {
                _cmbNodeFilter.SelectedIndex = 0;
            }
            _suppressNodeComboEvent = false;
            UpdateFilterLabel();
            TriggerRefreshReport();
        };
        _btnRefresh.Click += (_, _) => TriggerRefreshReport();

        filterPanel.Controls.AddRange([_btnToday, _btnYesterday, _datePicker, _cmbNodeFilter, _cmbScope, _cmbSort, _btnSelectedNodes, _btnRefresh, _lblActiveFilters, _btnClearFilters]);

        // 主要指标与诊断入口固定在两行，诊断文字长短不影响报表高度。
        var summaryPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8, 2, 8, 6),
            BackColor = Color.White
        };
        summaryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        summaryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lblSummaryTraffic = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(30, 41, 59),
            Text = "正在读取报表…"
        };
        _btnDiagnostics = new Button { AutoSize = true, Anchor = AnchorStyles.Right, Text = "诊断详情" };
        _btnDiagnostics.Click += (_, _) => ShowDiagnostics();
        _toolTip.SetToolTip(_btnDiagnostics, _coverageDetails);
        summaryPanel.Controls.Add(_lblSummaryTraffic, 0, 0);
        summaryPanel.Controls.Add(_btnDiagnostics, 1, 0);

        var kpiPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        kpiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        kpiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        kpiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        kpiPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lblUploadKpi = CreateKpiLabel("上传", Color.FromArgb(244, 247, 251));
        _lblDownloadKpi = CreateKpiLabel("下载", Color.FromArgb(244, 247, 251));
        _lblTotalKpi = CreateKpiLabel("合计", Color.FromArgb(226, 239, 252));
        kpiPanel.Controls.Add(_lblUploadKpi, 0, 0);
        kpiPanel.Controls.Add(_lblDownloadKpi, 1, 0);
        kpiPanel.Controls.Add(_lblTotalKpi, 2, 0);
        summaryPanel.Controls.Add(kpiPanel, 0, 1);
        summaryPanel.SetColumnSpan(kpiPanel, 2);

        _reportHost = new Panel { Dock = DockStyle.Fill };
        _hoursPane = new Panel
        {
            Dock = DockStyle.Left,
            Width = MinHoursPaneWidth,
            Padding = new Padding(8, 0, 0, 8),
            BackColor = Color.White
        };
        _reportDivider = new Splitter
        {
            Dock = DockStyle.Left,
            Width = 8,
            MinSize = MinHoursPaneWidth,
            MinExtra = MinRankingPaneWidth,
            BackColor = Color.FromArgb(228, 234, 241)
        };
        var rankingsPane = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 8, 8),
            BackColor = Color.White
        };
        _reportDivider.SplitterMoved += (_, _) =>
        {
            var availableWidth = _reportHost.ClientSize.Width - _reportDivider.Width;
            if (availableWidth >= MinHoursPaneWidth + MinRankingPaneWidth)
            {
                _hoursPaneRatio = (double)_hoursPane.Width / availableWidth;
            }
        };
        _reportHost.SizeChanged += (_, _) => ResizeReportPanes();
        _cellMenu = new ContextMenuStrip();
        _cellMenu.Items.Add("复制单元格", null, (_, _) => CopyReportCell());

        _chkHideEmptyHours = new CheckBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(4, 4, 0, 4),
            Text = "隐藏空小时"
        };
        _toolTip.SetToolTip(_chkHideEmptyHours, "隐藏无采集数据和未开始的小时。已采样且用量为 0 的小时仍会显示。");
        _chkHideEmptyHours.CheckedChanged += (_, _) => FillHourGrid(keepScroll: false);
        _gridHours = CreateReadOnlyGrid();
        AddReportColumn(_gridHours, "Hour", "小时");
        AddReportColumn(_gridHours, "Total", "合计", numeric: true);
        AddReportColumn(_gridHours, "Coverage", "覆盖", fill: true);
        AddReportColumn(_gridHours, "Upload", "上传", numeric: true);
        AddReportColumn(_gridHours, "Download", "下载", numeric: true);
        _hoursPane.Controls.Add(_gridHours);
        _hoursPane.Controls.Add(_chkHideEmptyHours);

        _tabDimensions = new TabControl { Dock = DockStyle.Fill };
        _gridApps = CreateRankingGrid("应用", key => { _filterApp = key; OnFilterPicked(); });
        _gridDomains = CreateRankingGrid("域名", key => { _filterDomain = key; OnFilterPicked(); });
        _gridNodes = CreateRankingGrid("节点", key => { _filterNode = key; OnFilterPicked(); });

        var tabApp = new TabPage("应用排行");
        tabApp.Controls.Add(_gridApps);
        var tabDomain = new TabPage("域名排行");
        tabDomain.Controls.Add(_gridDomains);
        var tabNode = new TabPage("节点排行");
        tabNode.Controls.Add(_gridNodes);

        _tabDimensions.TabPages.AddRange([tabApp, tabDomain, tabNode]);
        _tabDimensions.SelectedIndexChanged += (_, _) =>
        {
            var activeGrid = _tabDimensions.SelectedIndex switch
            {
                1 => _gridDomains,
                2 => _gridNodes,
                _ => _gridApps
            };
            SizeReportColumns(activeGrid);
        };
        rankingsPane.Controls.Add(_tabDimensions);

        _reportHost.Controls.Add(rankingsPane);
        _reportHost.Controls.Add(_reportDivider);
        _reportHost.Controls.Add(_hoursPane);
        Controls.Add(_reportHost);
        Controls.Add(summaryPanel);
        Controls.Add(filterPanel);
        Controls.Add(topPanel);
        AdjustScaledControlWidths();

        // 系统托盘
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("打开窗口", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("退出", null, (_, _) => RequestApplicationExit());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "NetTrack - 代理流量账本",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        // 定时刷新视图（每 5 秒）
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (Visible && !_isShuttingDown)
            {
                TriggerRefreshReport();
            }
        };

        _controller.StateChanged += OnControllerStateChanged;
        Shown += async (_, _) =>
        {
            ApplyInitialDpiWindowSize();
            AdjustScaledControlWidths();
            ResizeReportPanes();
            await InitializeCollectorOnShownAsync();
        };
        FormClosing += OnMainFormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialDpiWindowSize();
    }

    internal static Size CalculateInitialWindowSize(int deviceDpi, Rectangle workingArea)
    {
        var scale = Math.Max(1.0, deviceDpi / 96.0);
        var desiredWidth = (int)Math.Round(DefaultLogicalWidth * scale);
        var desiredHeight = (int)Math.Round(DefaultLogicalHeight * scale);

        var maxWidth = workingArea.Width > 0
            ? Math.Max(920, (int)Math.Floor(workingArea.Width * 0.92))
            : desiredWidth;
        var maxHeight = workingArea.Height > 0
            ? Math.Max(600, (int)Math.Floor(workingArea.Height * 0.90))
            : desiredHeight;

        return new Size(
            Math.Clamp(desiredWidth, 920, Math.Max(920, maxWidth)),
            Math.Clamp(desiredHeight, 600, Math.Max(600, maxHeight)));
    }

    private void ApplyInitialDpiWindowSize()
    {
        if (_initialDpiSizeApplied || _formDisposed || WindowState != FormWindowState.Normal)
        {
            return;
        }

        _initialDpiSizeApplied = true;
        var workArea = Screen.FromControl(this).WorkingArea;
        var target = CalculateInitialWindowSize(DeviceDpi, workArea);
        if (Size != target)
        {
            Size = target;
            if (workArea.Width > 0 && workArea.Height > 0)
            {
                Location = new Point(
                    workArea.Left + Math.Max(0, (workArea.Width - Width) / 2),
                    workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));
            }
        }
    }

    private Task? _initTask;

    private void ResizeReportPanes()
    {
        var availableWidth = _reportHost.ClientSize.Width - _reportDivider.Width;
        if (_formDisposed || availableWidth < MinHoursPaneWidth + MinRankingPaneWidth)
        {
            return;
        }
        _hoursPane.Width = Math.Clamp(
            (int)Math.Round(availableWidth * _hoursPaneRatio),
            MinHoursPaneWidth,
            availableWidth - MinRankingPaneWidth);
    }

    private Task InitializeCollectorOnShownAsync()
    {
        _initTask ??= RunInitializeCollectorAsync();
        return _initTask;
    }

    private async Task RunInitializeCollectorAsync()
    {
        await _controller.TryStartCollectingAsync();
        if (!IsDisposed)
        {
            TriggerRefreshReport();
            _refreshTimer.Start();
        }
    }

    public Task WaitForStartupAsync() => InitializeCollectorOnShownAsync();

    public DesktopCollectorViewState CollectorState => _controller.CurrentState;
    public bool IsHiddenInTray => !Visible && _trayIcon.Visible;
    public string StatusBadgeText => _lblStatusBadge.Text;

    /// <summary>
    /// 收起到系统托盘（不改变后台采集状态）。
    /// </summary>
    public void MinimizeToTray()
    {
        _trayIcon.Visible = true;
        Hide();
    }

    /// <summary>
    /// 从托盘或最小化恢复窗口，并立即同步刷新当前真实的采集状态与报表。
    /// </summary>
    public void RestoreFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(RestoreFromTray);
            return;
        }

        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
        BringToFront();

        // 恢复窗口时直接读取并渲染当前真实采集状态，绝不硬编码切回「采集中」
        ApplyCollectorStateToUi(_controller.CurrentState);
        TriggerRefreshReport();
    }

    public void RequestApplicationExit()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(RequestApplicationExit);
            return;
        }

        Close();
    }

    public Task<DesktopCollectorViewState> TriggerStartCollectingForTestAsync() =>
        _controller.TryStartCollectingAsync();

    public Task<CollectorStopResult> TriggerStopCollectingForTestAsync() =>
        StopFromToolbarAsync();

    public void ExportCurrentFilterToCsv(string outputPath)
    {
        var options = _controller.LoadEffectiveOptions(out var configErr);
        if (configErr is not null)
        {
            throw new InvalidOperationException($"配置读取失败，已终止导出：{configErr}");
        }

        var rows = _queryService.QueryExportRows(
            _selectedDateBj,
            new ReportFilters(_filterApp, _filterDomain, _filterNode),
            _selectedScope,
            options.SelectedNodes);
        CsvExporter.WriteToFile(outputPath, rows, _paths);
    }

    private async Task ToggleCollectingAsync()
    {
        var mode = _controller.CurrentState.Mode;
        if (mode is DesktopCollectorMode.Collecting or DesktopCollectorMode.WaitingClash or DesktopCollectorMode.AmbiguousPipes or DesktopCollectorMode.StorageError)
        {
            await StopFromToolbarAsync();
        }
        else if (mode is DesktopCollectorMode.Stopped or DesktopCollectorMode.ReadOnlyExternal or DesktopCollectorMode.LockAccessError or DesktopCollectorMode.ConfigError)
        {
            await _controller.TryStartCollectingAsync();
            TriggerRefreshReport();
        }
    }

    private async Task<CollectorStopResult> StopFromToolbarAsync()
    {
        _btnToggleCollect.Enabled = false;
        try
        {
            var result = await _controller.StopCollectingAsync(releaseLockOnFailure: true);
            if (!result.FlushSucceeded)
            {
                var committed = result.LastCommittedSampleUtcMs.HasValue
                    ? BeijingTime.FormatDisplay(result.LastCommittedSampleUtcMs.Value)
                    : "无";
                var msg = $"停止采集时末批数据未能保存到账本：{result.ErrorMessage}\n最后已提交至：{committed}";
                if (_deps.OnStopFlushFailedAlert is not null)
                {
                    _deps.OnStopFlushFailedAlert(msg);
                }
                else
                {
                    MessageBox.Show(this, msg, "NetTrack 保存警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            TriggerRefreshReport();
            return result;
        }
        finally
        {
            ApplyCollectorStateToUi(_controller.CurrentState);
        }
    }

    private async void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitConfirmed)
        {
            return;
        }

        if (!_controller.CurrentState.HoldsCollectorLock)
        {
            // 只读模式或已停止（未持有采集锁）：直接退出，不干扰外部 CLI
            _exitConfirmed = true;
            _refreshTimer.Stop();
            _trayIcon.Visible = false;
            return;
        }

        // 持有采集锁：拦截关闭并异步执行优雅停采刷盘
        e.Cancel = true;
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _refreshTimer.Stop();
        try
        {
            var stopResult = await _controller.StopCollectingAsync(releaseLockOnFailure: false);
            while (!stopResult.FlushSucceeded)
            {
                var committed = stopResult.LastCommittedSampleUtcMs.HasValue
                    ? BeijingTime.FormatDisplay(stopResult.LastCommittedSampleUtcMs.Value)
                    : "无";
                var promptMsg = $"末批待写数据未能保存到账本：{stopResult.ErrorMessage}\n最后已提交至：{committed}\n\n请选择「重试」再次保存，「忽略/放弃」放弃未保存数据并退出，或「取消」留在应用内。";

                var decision = PromptExitFlushFailure(promptMsg);
                if (decision == ExitFlushFailureAction.Retry)
                {
                    stopResult = await _controller.RetryFinalFlushAsync();
                }
                else if (decision == ExitFlushFailureAction.AbandonAndExit)
                {
                    await _controller.AbandonPendingAndReleaseLockAsync();
                    break;
                }
                else
                {
                    // 取消退出：恢复窗口可见并保留当前状态
                    _isShuttingDown = false;
                    if (!Visible)
                    {
                        RestoreFromTray();
                    }
                    _refreshTimer.Start();
                    return;
                }
            }

            _exitConfirmed = true;
            _trayIcon.Visible = false;
            Close();
        }
        finally
        {
            if (!_exitConfirmed)
            {
                _isShuttingDown = false;
            }
        }
    }

    private ExitFlushFailureAction PromptExitFlushFailure(string message)
    {
        if (_deps.OnExitFlushFailedPrompt is not null)
        {
            return _deps.OnExitFlushFailedPrompt(message);
        }

        var res = MessageBox.Show(
            this,
            message,
            "NetTrack 末批保存失败",
            MessageBoxButtons.AbortRetryIgnore,
            MessageBoxIcon.Warning);

        return res switch
        {
            DialogResult.Retry => ExitFlushFailureAction.Retry,
            DialogResult.Ignore => ExitFlushFailureAction.AbandonAndExit,
            _ => ExitFlushFailureAction.CancelExit
        };
    }

    private void OnControllerStateChanged(DesktopCollectorViewState state)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => ApplyCollectorStateToUi(state));
            }
            catch
            {
                // 窗口正在释放时忽略回调
            }
            return;
        }

        ApplyCollectorStateToUi(state);
    }

    private void ApplyCollectorStateToUi(DesktopCollectorViewState state)
    {
        if (IsDisposed)
        {
            return;
        }

        _displayedCollectorState = state;
        _lblStatusBadge.Text = state.Mode == DesktopCollectorMode.Collecting &&
            state.AttributionVerified == false && state.LastCommittedSampleUtcMs.HasValue
            ? "采集中 · 严格归因未核验"
            : state.StatusText;
        _lblStatusBadge.BackColor = state.Mode switch
        {
            DesktopCollectorMode.Collecting when state.AttributionVerified == false => Color.FromArgb(255, 243, 205),
            DesktopCollectorMode.Collecting => Color.FromArgb(209, 245, 224),
            DesktopCollectorMode.WaitingClash => Color.FromArgb(255, 243, 205),
            DesktopCollectorMode.ReadOnlyExternal => Color.FromArgb(217, 237, 247),
            DesktopCollectorMode.StorageError or DesktopCollectorMode.LockAccessError or DesktopCollectorMode.ConfigError or DesktopCollectorMode.AmbiguousPipes => Color.FromArgb(248, 215, 218),
            _ => Color.LightGray
        };

        _toolTip.SetToolTip(
            _lblStatusBadge,
            string.IsNullOrWhiteSpace(state.DetailMessage) ? state.StatusText : $"{state.StatusText}\n{state.DetailMessage}");
        UpdateCommittedTime(state.LastCommittedSampleUtcMs);

        // 首批数据提交后立即自动刷新首页报表
        if (state.LastCommittedSampleUtcMs.HasValue && !_lastSeenCommittedUtcMs.HasValue)
        {
            _lastSeenCommittedUtcMs = state.LastCommittedSampleUtcMs;
            TriggerRefreshReport();
        }
        else if (state.LastCommittedSampleUtcMs.HasValue)
        {
            _lastSeenCommittedUtcMs = state.LastCommittedSampleUtcMs;
        }

        _btnToggleCollect.Enabled = state.Mode != DesktopCollectorMode.StoppingFlush;
        _btnToggleCollect.Text = state.Mode switch
        {
            DesktopCollectorMode.Collecting or DesktopCollectorMode.WaitingClash or DesktopCollectorMode.AmbiguousPipes or DesktopCollectorMode.StorageError => "停止采集",
            DesktopCollectorMode.ReadOnlyExternal => "重试开始采集",
            _ => "开始采集"
        };
    }

    private void SetSelectedDate(DateOnly dateBj)
    {
        _selectedDateBj = dateBj;
        ClearNodeFilterSelection();
        _datePicker.Value = dateBj.ToDateTime(TimeOnly.MinValue);
        TriggerRefreshReport();
    }

    private void ClearNodeFilterSelection()
    {
        _filterNode = null;
        _suppressNodeComboEvent = true;
        if (_cmbNodeFilter.Items.Count > 0)
        {
            _cmbNodeFilter.SelectedIndex = 0;
        }
        _suppressNodeComboEvent = false;
        UpdateFilterLabel();
    }

    private void OnFilterPicked()
    {
        UpdateFilterLabel();
        TriggerRefreshReport();
    }

    private void UpdateFilterLabel()
    {
        var parts = new List<string>();
        if (_filterApp is not null) parts.Add($"应用={_filterApp}");
        if (_filterDomain is not null) parts.Add($"域名={_filterDomain}");
        if (_filterNode is not null) parts.Add($"节点={_filterNode}");

        var hasFilters = parts.Count > 0;
        _lblActiveFilters.Text = $"已筛选 {parts.Count} 项";
        _lblActiveFilters.Visible = hasFilters;
        _toolTip.SetToolTip(_lblActiveFilters, string.Join(Environment.NewLine, parts));
        _btnClearFilters.Visible = hasFilters;
        _btnClearFilters.Enabled = hasFilters;
    }

    private void TriggerRefreshReport()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var ct = cts.Token;

        var date = _selectedDateBj;
        var scope = _selectedScope;
        var sort = _selectedSort;
        var filters = new ReportFilters(_filterApp, _filterDomain, _filterNode);
        var options = _controller.LoadEffectiveOptions(out _);

        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var hourReport = _queryService.QueryDailyReport(new ReportQueryRequest(date, ReportGroupBy.Hour, sort, filters, scope), options);
            var appReport = _queryService.QueryDailyReport(new ReportQueryRequest(date, ReportGroupBy.App, sort, filters, scope), options);
            var domainReport = _queryService.QueryDailyReport(new ReportQueryRequest(date, ReportGroupBy.Domain, sort, filters, scope), options);
            var nodeReport = _queryService.QueryDailyReport(new ReportQueryRequest(date, ReportGroupBy.Node, sort, filters, scope), options);
            var discovered = _queryService.QueryDiscoveredNodes(date);
            return (hourReport, appReport, domainReport, nodeReport, discovered);
        }, ct).ContinueWith(t =>
        {
            if (ct.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            var (hourReport, appReport, domainReport, nodeReport, discovered) = t.Result;
            try
            {
                BeginInvoke(() =>
                {
                    // 结果入队后，日期、筛选或排序仍可能在窗口线程上改变。
                    if (ct.IsCancellationRequested || IsDisposed || Disposing)
                    {
                        return;
                    }
                    BindDiscoveredNodes(discovered);
                    BindReports(hourReport, appReport, domainReport, nodeReport);
                });
            }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // 窗口已关闭，无需再绑定结果。
            }
        }, TaskScheduler.Default);
    }

    private void BindDiscoveredNodes(IReadOnlyList<DiscoveredNodeOption> discovered)
    {
        if (IsDisposed)
        {
            return;
        }

        _discoveredNodes = discovered;
        _suppressNodeComboEvent = true;
        try
        {
            _cmbNodeFilter.Items.Clear();
            _cmbNodeFilter.Items.Add("全部节点");
            var selectedIdx = 0;
            for (var i = 0; i < discovered.Count; i++)
            {
                var opt = discovered[i];
                _cmbNodeFilter.Items.Add(opt.DisplayLabel);
                var expectedKey = _selectedScope == ReportScope.ConfirmedProxy
                    ? opt.ConfirmedFilterKey
                    : opt.ObservedFilterKey;
                if (_filterNode is not null && string.Equals(_filterNode, expectedKey, StringComparison.Ordinal))
                {
                    selectedIdx = i + 1;
                }
            }
            _cmbNodeFilter.SelectedIndex = selectedIdx;
            _toolTip.SetToolTip(_cmbNodeFilter, _cmbNodeFilter.SelectedItem as string);
        }
        finally
        {
            _suppressNodeComboEvent = false;
        }
    }

    private async Task ExportCsvInteractiveAsync()
    {
        // 写文件前先校验配置可读，避免无效 settings.json 被当作空名单覆盖已有 CSV
        _ = _controller.LoadEffectiveOptions(out var preConfigErr);
        if (preConfigErr is not null)
        {
            MessageBox.Show(this, $"配置读取失败，已终止导出：{preConfigErr}", "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出代理流量汇总 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"net-track-{BeijingTime.FormatDate(_selectedDateBj)}.csv"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var outPath = dlg.FileName;
        var date = _selectedDateBj;
        var scope = _selectedScope;
        var filters = new ReportFilters(_filterApp, _filterDomain, _filterNode);
        var options = _controller.LoadEffectiveOptions(out var configErr);
        if (configErr is not null)
        {
            MessageBox.Show(this, $"配置读取失败，已终止导出：{configErr}", "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            var count = await Task.Run(() =>
            {
                var rows = _queryService.QueryExportRows(date, filters, scope, options.SelectedNodes);
                CsvExporter.WriteToFile(outPath, rows, _paths);
                return rows.Count;
            });

            MessageBox.Show(this, $"已导出 {count} 条记录至：\n{outPath}", "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出 CSV 失败：{ex.Message}", "导出错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EditSelectedNodesDialog()
    {
        var (settings, err) = SettingsLoader.Load(_paths.SettingsPath);
        if (settings is null)
        {
            MessageBox.Show(this, $"无法读取配置：{err}", "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var scale = Math.Max(1.0, DeviceDpi / 96.0);
        using var dlg = new Form
        {
            Text = "配置所选付费节点（每行一个节点原名）",
            Size = new Size((int)Math.Round(460 * scale), (int)Math.Round(340 * scale)),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Font = Font,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size((int)Math.Round(430 * scale), 0),
            Padding = new Padding(8, 6, 8, 4),
            Text = "每行一个「Provider名::节点名」（推荐，防同名冲突）或裸节点名。\r\n仅查询当前名单（清空名单后观察报表为空），保存后重启采集按新名单记录。"
        };
        var txt = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, settings.SelectedNodes ?? [])
        };
        var btnBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnOk = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        btnBar.Controls.AddRange([btnCancel, btnOk]);

        dlg.Controls.Add(txt);
        dlg.Controls.Add(hint);
        dlg.Controls.Add(btnBar);
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var lines = txt.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            settings.SelectedNodes = SettingsLoader.NormalizeSelectedNodes(lines).ToList();
            SettingsLoader.Save(_paths.SettingsPath, settings);
            TriggerRefreshReport();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateStatusBadgeFont();
        AdjustScaledControlWidths();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateStatusBadgeFont();
        AdjustScaledControlWidths();
    }

    private void UpdateStatusBadgeFont()
    {
        if (_lblStatusBadge is null || _formDisposed)
        {
            return;
        }

        var previous = _statusBadgeFont;
        _statusBadgeFont = new Font(Font, Font.Style | FontStyle.Bold);
        _lblStatusBadge.Font = _statusBadgeFont;
        previous?.Dispose();
    }

    private void AdjustScaledControlWidths()
    {
        if (_formDisposed || _datePicker is null || _cmbScope is null || _cmbSort is null || _cmbNodeFilter is null)
        {
            return;
        }

        var dropChrome = SystemInformation.VerticalScrollBarWidth;
        _datePicker.Width = Math.Max(132, TextRenderer.MeasureText("2099-12-31", _datePicker.Font).Width + dropChrome + 24);
        _cmbScope.Width = Math.Max(140, TextRenderer.MeasureText("所选节点观察量", _cmbScope.Font).Width + dropChrome + 20);
        _cmbSort.Width = Math.Max(110, TextRenderer.MeasureText("按上传排序", _cmbSort.Font).Width + dropChrome + 20);
        _cmbNodeFilter.Width = Math.Max(175, TextRenderer.MeasureText("全部节点", _cmbNodeFilter.Font).Width + dropChrome + 16);
        if (_lblSummaryTraffic is not null)
        {
            var lineHeight = TextRenderer.MeasureText("自动Ag", _lblSummaryTraffic.Font).Height + 4;
            _lblSummaryTraffic.MinimumSize = new Size(0, lineHeight);
            _lblSummaryTraffic.MaximumSize = new Size(0, lineHeight);
        }
        if (_gridNodes is not null)
        {
            AdjustGridFont(_gridHours);
            AdjustGridFont(_gridApps);
            AdjustGridFont(_gridDomains);
            AdjustGridFont(_gridNodes);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_formDisposed)
        {
            _formDisposed = true;
            var cts = Interlocked.Exchange(ref _queryCts, null);
            if (cts is not null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
            _refreshTimer.Dispose();
            _toolTip.Dispose();
            _cellMenu.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _controller.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _statusBadgeFont?.Dispose();
            _statusBadgeFont = null;
        }
    }
}
