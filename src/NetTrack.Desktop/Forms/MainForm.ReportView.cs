using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Reporting;
using NetTrack.Desktop.ViewModels;

namespace NetTrack.Desktop.Forms;

public sealed partial class MainForm
{
    private static Label CreateKpiLabel(string name, Color background) => new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(3, 1, 3, 1),
        Padding = new Padding(10, 5, 8, 5),
        BackColor = background,
        ForeColor = Color.FromArgb(15, 32, 54),
        TextAlign = ContentAlignment.MiddleLeft,
        Text = $"{name}  —"
    };

    private void BindReports(
        DailyReportResult hourReport,
        DailyReportResult appReport,
        DailyReportResult domainReport,
        DailyReportResult nodeReport)
    {
        if (IsDisposed)
        {
            return;
        }

        var summary = hourReport.Summary;
        var isAuto = hourReport.Scope == "auto_node_observed";
        var isSelected = hourReport.Scope == "selected_nodes_observed";
        var isObservation = isAuto || isSelected;
        var filter = hourReport.Filters;
        var hasFilters = filter.App is not null || filter.Domain is not null || filter.Node is not null;
        var noData = summary.CoverageStatus == "no_data" && summary.TotalBytes == 0;
        var noSelectedNodes = isSelected && hourReport.SelectedNodes.Count == 0;
        var strictBlocked = !isObservation && hourReport.AttributionVerified == false && summary.TotalBytes == 0 &&
            (summary.UnconfirmedUploadBytes > 0 || summary.UnconfirmedDownloadBytes > 0);
        var waitingForFirstBatch = _controller.CurrentState.Mode == DesktopCollectorMode.Collecting &&
            !_controller.CurrentState.LastCommittedSampleUtcMs.HasValue;

        var ranking = appReport.Items.Concat(domainReport.Items).Concat(nodeReport.Items);
        var hasConfirmed = isAuto && ranking.Any(item => item.VerificationStatus == "confirmed");
        var hasUnconfirmed = isAuto && ranking.Any(item => item.VerificationStatus != "confirmed");
        var mixedAttribution = hasConfirmed && hasUnconfirmed;
        var scope = isAuto ? "自动节点观察" : isSelected ? "所选节点观察量" : "严格代理账本";
        var state = (noSelectedNodes, strictBlocked, noData, isAuto, isSelected) switch
        {
            (true, _, _, _, _) => "未选择付费节点",
            (_, true, _, _, _) => "代理归因暂不可用",
            (_, _, true, _, true) => "暂无观察数据",
            (_, _, true, _, _) when waitingForFirstBatch => "等待首批数据",
            (_, _, true, true, _) => "暂无观察数据",
            (_, _, true, _, _) => "暂无采集数据",
            (_, _, _, true, _) when mixedAttribution => "含未确认",
            (_, _, _, true, _) when hasUnconfirmed || (!hasConfirmed && hourReport.AttributionVerified != true) => "走向未确认",
            (_, _, _, true, _) => "已核验",
            (_, _, _, _, true) when summary.CoverageStatus == "no_data" => "无共同覆盖",
            _ => string.Empty
        };

        var title = $"{hourReport.DateBj}  {scope}";
        if (!string.IsNullOrEmpty(state))
        {
            title += $" · {state}";
        }
        if (summary.CoverageStatus == "partial_gap")
        {
            title += " · 有空档";
        }
        _lblSummaryTraffic.Text = title;
        _toolTip.SetToolTip(_lblSummaryTraffic, title);

        var showTraffic = !noData && !noSelectedNodes && !strictBlocked;
        _lblUploadKpi.Text = $"上传  {(showTraffic ? DecimalByteFormatter.Format(summary.UploadBytes) : "—")}";
        _lblDownloadKpi.Text = $"下载  {(showTraffic ? DecimalByteFormatter.Format(summary.DownloadBytes) : "—")}";
        _lblTotalKpi.Text = $"合计  {(showTraffic ? DecimalByteFormatter.Format(summary.TotalBytes) : "—")}";

        var coverage = DecimalByteFormatter.FormatCoverageStatusLabel(summary.CoverageStatus);
        var unknownScope = hasFilters ? "当前筛选未知应用占比" : "当日累计未知应用占比";
        _coverageDetails = string.Create(CultureInfo.InvariantCulture,
            $"覆盖状态：{coverage}（覆盖 {summary.CoveredSeconds}s，空档 {summary.GapSeconds}s，待更新 {summary.PendingSeconds}s）\n{unknownScope}：{summary.UnknownAppTotalRatio * 100:F2}%");

        var diagnostics = new List<string>();
        if (isAuto)
        {
            diagnostics.Add("自动节点观察已排除已识别的直连和拒绝流量。");
        }
        if (hourReport.AttributionVerified == false && !string.IsNullOrEmpty(hourReport.AttributionReasonText))
        {
            diagnostics.Add($"归因受阻：{hourReport.AttributionReasonText}");
        }
        var unconfirmedLabel = isObservation
            ? "全天严格账本未确认（全局诊断，可能与观察量重叠）"
            : "全天全局走向未确认";
        diagnostics.Add($"{unconfirmedLabel}：上 {DecimalByteFormatter.Format(summary.UnconfirmedUploadBytes)} / 下 {DecimalByteFormatter.Format(summary.UnconfirmedDownloadBytes)} / 合计 {DecimalByteFormatter.Format(summary.UnconfirmedUploadBytes + summary.UnconfirmedDownloadBytes)}");
        var reconcileDetail = $"全天对账差额：上 {DecimalByteFormatter.Format(summary.ReconcileDiffUploadBytes)} / 下 {DecimalByteFormatter.Format(summary.ReconcileDiffDownloadBytes)}";
        diagnostics.Add(reconcileDetail);
        var clockDetail = summary.ClockAnomalyCount > 0 ? $"时钟异常：{summary.ClockAnomalyCount} 次" : null;
        if (clockDetail is not null)
        {
            diagnostics.Add(clockDetail);
        }
        var hasUnknownApps = summary.UnknownAppTotalRatio > 0;
        var processHint = hasUnknownApps
            ? "应用识别依赖 Clash/Mihomo 的进程匹配（find-process-mode）。此前已记为未知应用的流量保留原归因。"
            : null;
        if (processHint is not null)
        {
            diagnostics.Add(processHint);
        }
        _reportDiagnostics = string.Join(Environment.NewLine, diagnostics);

        var hasReconcileDiff = summary.ReconcileDiffUploadBytes != 0 || summary.ReconcileDiffDownloadBytes != 0;
        var unknownPercent = summary.UnknownAppTotalRatio < 0.01
            ? "<1%"
            : string.Create(CultureInfo.InvariantCulture, $"{summary.UnknownAppTotalRatio * 100:0}%");
        var notice = hasUnknownApps ? $"未知应用 {unknownPercent}"
            : clockDetail is not null ? "时钟异常"
            : hasReconcileDiff ? "对账差额"
            : null;
        _btnDiagnostics.Text = notice is null ? "诊断详情" : $"{notice} · 详情";
        _btnDiagnostics.AccessibleName = notice is null ? "诊断详情" : $"{notice}，诊断详情";
        _btnDiagnostics.BackColor = notice is null ? SystemColors.Control : Color.FromArgb(255, 242, 213);
        _btnDiagnostics.UseVisualStyleBackColor = notice is null;
        _toolTip.SetToolTip(_btnDiagnostics, string.Join(Environment.NewLine,
            new[] { _coverageDetails, processHint, clockDetail, hasReconcileDiff ? reconcileDetail : null }
                .Where(line => line is not null)));

        if (DateTimeOffset.TryParse(hourReport.CommittedUntilBj, CultureInfo.InvariantCulture, DateTimeStyles.None, out var committed))
        {
            UpdateCommittedTime(committed.ToUnixTimeMilliseconds());
        }

        var view = (hourReport.DateBj, hourReport.Scope, hourReport.SortBy, hourReport.Filters);
        var keepScroll = _renderedReport.HasValue && _renderedReport.Value == view;
        _renderedReport = view;
        _hourItems = hourReport.Items;
        FillHourGrid(keepScroll);
        FillRankingGrid(_gridApps, appReport, mixedAttribution, keepScroll);
        FillRankingGrid(_gridDomains, domainReport, mixedAttribution, keepScroll);
        FillRankingGrid(_gridNodes, nodeReport, mixedAttribution, keepScroll);
    }

    private void UpdateCommittedTime(long? committedUtcMs)
    {
        if (committedUtcMs is long time && (_displayedCommitUtcMs is null || time > _displayedCommitUtcMs.Value))
        {
            _displayedCommitUtcMs = time;
            _lblCommittedInfo.Text = $"已提交：{BeijingTime.FormatDisplay(time)}";
        }
    }

    private string BuildDiagnosticsText()
    {
        var state = _displayedCollectorState ?? _controller.CurrentState;
        var report = _reportDiagnostics;
        var detail = state.DetailMessage;
        var stateText = string.IsNullOrEmpty(detail) || report?.Contains(detail, StringComparison.Ordinal) == true
            ? state.StatusText
            : $"{state.StatusText}{Environment.NewLine}{detail}";
        var pipe = state.ActivePipeName ?? "未连接";
        var version = state.MihomoVersion ?? "未知";
        return string.Join(Environment.NewLine + Environment.NewLine,
            new[]
            {
                _lblSummaryTraffic.Text,
                _coverageDetails.ReplaceLineEndings(),
                report,
                stateText,
                $"Clash 管道：{pipe}{Environment.NewLine}Mihomo 版本：{version}"
            }.Where(section => !string.IsNullOrEmpty(section)));
    }

    private void ShowDiagnostics()
    {
        var scale = Math.Max(1.0, DeviceDpi / 96.0);
        var workArea = Screen.FromControl(this).WorkingArea;
        var diagW = Math.Min((int)Math.Round(900 * scale), Math.Max(420, (int)Math.Floor(workArea.Width * 0.85)));
        var diagH = Math.Min((int)Math.Round(500 * scale), Math.Max(320, (int)Math.Floor(workArea.Height * 0.80)));
        using var dialog = new Form
        {
            Text = "NetTrack 诊断详情",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(diagW, diagH),
            MinimumSize = new Size(420, 320),
            Font = Font,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Padding = new Padding(12)
        };
        var details = new TextBox
        {
            AccessibleName = "报表与连接诊断",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            ScrollBars = ScrollBars.Vertical,
            Text = BuildDiagnosticsText()
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 0, 0)
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(close);
        dialog.Controls.Add(details);
        dialog.Controls.Add(buttons);
        dialog.CancelButton = close;
        dialog.ActiveControl = close;
        dialog.ShowDialog(this);
    }

    private DataGridView CreateReadOnlyGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ShowCellToolTips = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(228, 234, 241),
            BackgroundColor = Color.White
        };
        grid.DefaultCellStyle.Padding = new Padding(5, 0, 6, 0);
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(30, 41, 59);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 235, 251);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(17, 48, 83);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(239, 244, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = grid.DefaultCellStyle.ForeColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 0, 6, 0);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                grid.Rows[e.RowIndex].Cells[e.ColumnIndex].FormattedValue is string { Length: > 0 } text)
            {
                var row = grid.Rows[e.RowIndex];
                grid.CurrentCell = row.Cells[e.ColumnIndex];
                grid.ClearSelection();
                row.Selected = true;
                // 刷新可以替换网格行，菜单保留右键按下时的完整值。
                _cellCopyText = text;
                _cellMenu.Show(grid, grid.PointToClient(Cursor.Position));
            }
        };
        return grid;
    }

    private void CopyReportCell()
    {
        if (_cellCopyText is not string text)
        {
            return;
        }
        _cellCopyText = null;
        try
        {
            (_deps.CopyTextToClipboard ?? Clipboard.SetText)(text);
        }
        catch (ExternalException)
        {
            MessageBox.Show(this, "剪贴板暂时不可用，请稍后重试。", "复制单元格", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static DataGridViewColumn AddReportColumn(
        DataGridView grid,
        string name,
        string header,
        bool fill = false,
        bool numeric = false,
        DataGridViewColumnSortMode sortMode = DataGridViewColumnSortMode.NotSortable)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            SortMode = sortMode,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
        };
        if (numeric)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        grid.Columns.Add(column);
        return column;
    }

    private static void AddTrafficColumns(DataGridView grid, DataGridViewColumnSortMode sortMode)
    {
        AddReportColumn(grid, "Upload", "上传", numeric: true, sortMode: sortMode);
        AddReportColumn(grid, "Download", "下载", numeric: true, sortMode: sortMode);
        AddReportColumn(grid, "Total", "合计", numeric: true, sortMode: sortMode);
    }

    private DataGridView CreateRankingGrid(string firstHeader, Action<string> onRowPicked)
    {
        var grid = CreateReadOnlyGrid();
        AddReportColumn(grid, "Key", firstHeader, fill: true);
        AddTrafficColumns(grid, DataGridViewColumnSortMode.Programmatic);
        var share = AddReportColumn(grid, "Share", "占比", numeric: true);
        share.ToolTipText = "本行合计字节 ÷ 当前日期和筛选范围的合计字节";
        AddReportColumn(grid, "Attribution", "归因").Visible = false;

        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.ColumnIndex is >= 1 and <= 3)
            {
                var targetSortIndex = e.ColumnIndex - 1;
                if (_cmbSort.SelectedIndex != targetSortIndex)
                {
                    _cmbSort.SelectedIndex = targetSortIndex;
                }
            }
        };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is string { Length: > 0 } filterKey)
            {
                onRowPicked(filterKey);
            }
        };
        return grid;
    }

    private void FillHourGrid(bool keepScroll)
    {
        var firstRow = keepScroll ? _gridHours.FirstDisplayedScrollingRowIndex : 0;
        var offset = keepScroll ? _gridHours.HorizontalScrollingOffset : 0;
        _gridHours.Rows.Clear();
        foreach (var hour in _hourItems)
        {
            var noSample = hour.TotalBytes == 0 && hour.CoverageStatus is "no_data" or "future";
            if (_chkHideEmptyHours.Checked && noSample)
            {
                continue;
            }
            var coverage = DecimalByteFormatter.FormatCoverageStatusLabel(hour.CoverageStatus ?? "no_data");
            var shortCoverage = hour.CoverageStatus switch
            {
                "no_data" => "无数据",
                "complete" => "完整",
                _ => coverage
            };
            var onlyStatus = hour.CoverageStatus is "no_data" or "future";
            var detail = onlyStatus
                ? coverage
                : $"{coverage}（覆盖 {hour.CoveredSeconds}s，空档 {hour.GapSeconds}s，待更新 {hour.PendingSeconds}s）";
            var missing = hour.CoverageStatus == "no_data" && hour.TotalBytes == 0;
            var index = _gridHours.Rows.Add(
                hour.HourBj ?? string.Empty,
                missing ? "—" : DecimalByteFormatter.Format(hour.TotalBytes),
                shortCoverage,
                missing ? "—" : DecimalByteFormatter.Format(hour.UploadBytes),
                missing ? "—" : DecimalByteFormatter.Format(hour.DownloadBytes));
            _gridHours.Rows[index].Cells["Coverage"].ToolTipText = detail;
        }
        SizeReportColumns(_gridHours);
        RestoreGridScroll(_gridHours, firstRow, offset);
    }

    private static void FillRankingGrid(DataGridView grid, DailyReportResult report, bool showAttribution, bool keepScroll)
    {
        var firstRow = keepScroll ? grid.FirstDisplayedScrollingRowIndex : 0;
        var offset = keepScroll ? grid.HorizontalScrollingOffset : 0;
        grid.Rows.Clear();
        grid.Columns["Attribution"]!.Visible = showAttribution;
        var denominator = report.Summary.TotalBytes;
        foreach (var item in report.Items)
        {
            var share = denominator > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{100.0 * item.TotalBytes / denominator:F2}%")
                : "—";
            var attribution = item.VerificationStatus switch
            {
                "confirmed" => "已核验",
                "unconfirmed" => "未确认",
                _ => string.Empty
            };
            var index = grid.Rows.Add(
                item.Key,
                DecimalByteFormatter.Format(item.UploadBytes),
                DecimalByteFormatter.Format(item.DownloadBytes),
                DecimalByteFormatter.Format(item.TotalBytes),
                share,
                attribution);
            var row = grid.Rows[index];
            row.Tag = item.FilterKey ?? item.Key;
            row.Cells["Key"].ToolTipText = item.Key;
        }

        var active = report.SortBy switch
        {
            "download" => "Download",
            "total" => "Total",
            _ => "Upload"
        };
        foreach (DataGridViewColumn column in grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = column.Name == active ? SortOrder.Descending : SortOrder.None;
        }
        SizeReportColumns(grid);
        RestoreGridScroll(grid, firstRow, offset);
    }

    private static void RestoreGridScroll(DataGridView grid, int firstRow, int offset)
    {
        if (firstRow > 0 && grid.Rows.Count > 0 && grid.IsHandleCreated && grid.Height > grid.ColumnHeadersHeight)
        {
            grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
        }
        grid.HorizontalScrollingOffset = offset;
    }

    private static void AdjustGridFont(DataGridView grid)
    {
        var height = TextRenderer.MeasureText("Ag", grid.Font).Height;
        grid.RowTemplate.Height = Math.Max(26, height + 12);
        grid.ColumnHeadersHeight = Math.Max(26, height + 10);
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Height = grid.RowTemplate.Height;
        }
        SizeReportColumns(grid);
    }

    private static void SizeReportColumns(DataGridView grid)
    {
        if (!grid.IsHandleCreated)
        {
            return;
        }

        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!column.Visible)
            {
                continue;
            }
            if (column.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill)
            {
                var minimumWidth = column.Name == "Coverage"
                    ? column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, fixedHeight: true)
                    : TextRenderer.MeasureText("示例域名", grid.Font).Width + grid.DefaultCellStyle.Padding.Horizontal;
                column.MinimumWidth = Math.Max(minimumWidth, column.HeaderCell.PreferredSize.Width);
            }
            else if (column.Tag is not int automaticWidth || column.Width == automaticWidth)
            {
                // 固定列记住自动测量宽度。用户调整后的宽度由用户决定。
                grid.AutoResizeColumn(column.Index, DataGridViewAutoSizeColumnMode.AllCells);
                column.Tag = column.Width;
            }
        }
    }
}
