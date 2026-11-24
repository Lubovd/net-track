using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Configuration;
using NetTrack.Core.Storage;

namespace NetTrack.Core.Reporting;

public enum ReportGroupBy
{
    App,
    Domain,
    Node,
    Hour
}

public enum ReportSortBy
{
    Upload,
    Download,
    Total
}

public sealed record ReportFilters(string? App = null, string? Domain = null, string? Node = null)
{
    public ReportFilters Normalize() => new(
        App: string.IsNullOrWhiteSpace(App) ? null : App.Trim().ToLowerInvariant(),
        Domain: string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim().TrimEnd('.').ToLowerInvariant(),
        Node: string.IsNullOrWhiteSpace(Node) ? null : (Node.Contains(SelectedNodeTarget.Separator, StringComparison.Ordinal) ? Node : Node.Trim()));
}

public enum ReportScope
{
    ConfirmedProxy,
    SelectedNodesObserved,
    AutoNodeObserved
}

public sealed record ReportQueryRequest(
    DateOnly DateBj,
    ReportGroupBy GroupBy = ReportGroupBy.App,
    ReportSortBy SortBy = ReportSortBy.Upload,
    ReportFilters? Filters = null,
    ReportScope Scope = ReportScope.ConfirmedProxy);

public sealed record StatusLedgerHealth(
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("retention_days")] int RetentionDays,
    [property: JsonPropertyName("earliest_date_bj")] string? EarliestDateBj,
    [property: JsonPropertyName("latest_date_bj")] string? LatestDateBj);

public sealed record StatusErrorInfo(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("occurred_bj")] string? OccurredBj);

public sealed record StatusReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("collector_active")] bool CollectorActive,
    [property: JsonPropertyName("collector_status")] string CollectorStatus,
    [property: JsonPropertyName("active_pipe_name")] string? ActivePipeName,
    [property: JsonPropertyName("mihomo_version")] string? MihomoVersion,
    [property: JsonPropertyName("last_committed_sample_bj")] string? LastCommittedSampleBj,
    [property: JsonPropertyName("last_heartbeat_bj")] string? LastHeartbeatBj,
    [property: JsonPropertyName("last_error")] StatusErrorInfo? LastError,
    [property: JsonPropertyName("ledger")] StatusLedgerHealth Ledger);

public sealed record ReportFilterView(
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("domain")] string? Domain,
    [property: JsonPropertyName("node")] string? Node);

public sealed record DailyReportSummary(
    [property: JsonPropertyName("upload_bytes")] long UploadBytes,
    [property: JsonPropertyName("download_bytes")] long DownloadBytes,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("unknown_app_upload_bytes")] long UnknownAppUploadBytes,
    [property: JsonPropertyName("unknown_app_total_bytes")] long UnknownAppTotalBytes,
    [property: JsonPropertyName("unknown_app_total_ratio")] double UnknownAppTotalRatio,
    [property: JsonPropertyName("coverage_status")] string CoverageStatus,
    [property: JsonPropertyName("covered_seconds")] int CoveredSeconds,
    [property: JsonPropertyName("gap_seconds")] int GapSeconds,
    [property: JsonPropertyName("pending_seconds")] int PendingSeconds,
    [property: JsonPropertyName("covered_ms")] long CoveredMs,
    [property: JsonPropertyName("gap_ms")] long GapMs,
    [property: JsonPropertyName("pending_ms")] long PendingMs,
    [property: JsonPropertyName("unconfirmed_upload_bytes")] long UnconfirmedUploadBytes,
    [property: JsonPropertyName("unconfirmed_download_bytes")] long UnconfirmedDownloadBytes,
    [property: JsonPropertyName("reconcile_diff_upload_bytes")] long ReconcileDiffUploadBytes,
    [property: JsonPropertyName("reconcile_diff_download_bytes")] long ReconcileDiffDownloadBytes,
    [property: JsonPropertyName("clock_anomaly_count")] long ClockAnomalyCount);

public sealed record ReportItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("upload_bytes")] long UploadBytes,
    [property: JsonPropertyName("download_bytes")] long DownloadBytes,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("hour_bj"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HourBj = null,
    [property: JsonPropertyName("coverage_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CoverageStatus = null,
    [property: JsonPropertyName("covered_seconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CoveredSeconds = null,
    [property: JsonPropertyName("gap_seconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GapSeconds = null,
    [property: JsonPropertyName("pending_seconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PendingSeconds = null,
    [property: JsonPropertyName("covered_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CoveredMs = null,
    [property: JsonPropertyName("gap_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? GapMs = null,
    [property: JsonPropertyName("pending_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PendingMs = null,
    [property: JsonPropertyName("clock_anomaly_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ClockAnomalyCount = null,
    [property: JsonPropertyName("filter_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FilterKey = null,
    [property: JsonPropertyName("verification_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerificationStatus = null);

public sealed record DailyReportResult(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("date_bj")] string DateBj,
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("sort_by")] string SortBy,
    [property: JsonPropertyName("filters")] ReportFilterView Filters,
    [property: JsonPropertyName("selected_nodes")] IReadOnlyList<string> SelectedNodes,
    [property: JsonPropertyName("committed_until_bj")] string? CommittedUntilBj,
    [property: JsonPropertyName("summary")] DailyReportSummary Summary,
    [property: JsonPropertyName("items")] IReadOnlyList<ReportItem> Items,
    [property: JsonPropertyName("attribution_verified"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AttributionVerified = null,
    [property: JsonPropertyName("attribution_reason_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttributionReasonCode = null,
    [property: JsonPropertyName("attribution_reason_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttributionReasonText = null);

public sealed record DiscoveredNodeOption(
    string Node,
    string Provider,
    string DisplayLabel,
    string ConfirmedFilterKey,
    string ObservedFilterKey);

public sealed record CsvExportRow(
    string DateBj,
    string HourBj,
    string App,
    string Domain,
    string Node,
    long UploadBytes,
    long DownloadBytes,
    long TotalBytes);

/// <summary>
/// 人读流量十进制格式化器（1 MB = 1,000,000 B）。
/// </summary>
public static class DecimalByteFormatter
{
    public static string Format(long bytes)
    {
        var sign = bytes < 0 ? "-" : string.Empty;
        var abs = Math.Abs((double)bytes);
        if (abs < 1_000)
        {
            return $"{sign}{(long)abs} B";
        }
        if (abs < 1_000_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 1_000.0:F2} KB");
        }
        if (abs < 1_000_000_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 1_000_000.0:F2} MB");
        }
        if (abs < 1_000_000_000_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 1_000_000_000.0:F2} GB");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 1_000_000_000_000.0:F2} TB");
    }

    public static string FormatCoverageStatusLabel(string status) => status switch
    {
        "future" => "未开始",
        "pending" => "待更新",
        "updating" => "待更新",
        "no_data" => "无采集数据",
        "partial_gap" => "有空档",
        "complete" => "完整覆盖",
        _ => status
    };
}

/// <summary>
/// 只读报表与状态查询服务。绝不触碰 collector.lock，也绝不创建数据库文件。
/// </summary>
public sealed class ReportQueryService
{
    private readonly NetTrackPaths _paths;
    private readonly IClock _clock;

    public ReportQueryService(NetTrackPaths paths, IClock? clock = null)
    {
        _paths = paths;
        _clock = clock ?? SystemClock.Instance;
    }

    public StatusReport QueryStatus(CollectorOptions options)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return new StatusReport(
                SchemaVersion: 1,
                DataDir: _paths.DataDirectory,
                CollectorActive: false,
                CollectorStatus: "stopped",
                ActivePipeName: null,
                MihomoVersion: null,
                LastCommittedSampleBj: null,
                LastHeartbeatBj: null,
                LastError: null,
                Ledger: new StatusLedgerHealth(false, true, options.RetentionDays, null, null));
        }

        try
        {
            using var conn = OpenReadOnly();
            var state = ReadCollectorState(conn);
            var nowMs = _clock.UtcNowMs;
            var active = IsCollectorActive(state, nowMs, options.PendingMaxWindowMs);

            var (earliestDate, latestDate) = ReadDateBounds(conn);
            StatusErrorInfo? lastErr = state.LastErrorMessage is not null
                ? new StatusErrorInfo(
                    state.LastErrorCode,
                    state.LastErrorMessage,
                    state.LastErrorUtcMs.HasValue ? BeijingTime.FormatIso(state.LastErrorUtcMs.Value) : null)
                : null;

            return new StatusReport(
                SchemaVersion: 1,
                DataDir: _paths.DataDirectory,
                CollectorActive: active,
                CollectorStatus: active ? state.CollectorStatus : (state.CollectorStatus == "running" ? "stopped" : state.CollectorStatus),
                ActivePipeName: state.ActivePipeName,
                MihomoVersion: state.MihomoVersion,
                LastCommittedSampleBj: state.LastCommittedSampleUtcMs.HasValue ? BeijingTime.FormatIso(state.LastCommittedSampleUtcMs.Value) : null,
                LastHeartbeatBj: state.LastHeartbeatUtcMs.HasValue ? BeijingTime.FormatIso(state.LastHeartbeatUtcMs.Value) : null,
                LastError: lastErr,
                Ledger: new StatusLedgerHealth(true, true, options.RetentionDays, earliestDate, latestDate));
        }
        catch (Exception ex)
        {
            return new StatusReport(
                SchemaVersion: 1,
                DataDir: _paths.DataDirectory,
                CollectorActive: false,
                CollectorStatus: "error",
                ActivePipeName: null,
                MihomoVersion: null,
                LastCommittedSampleBj: null,
                LastHeartbeatBj: null,
                LastError: new StatusErrorInfo("ledger_unreadable", ex.Message, BeijingTime.FormatIso(_clock.UtcNowMs)),
                Ledger: new StatusLedgerHealth(true, false, options.RetentionDays, null, null));
        }
    }

    public DailyReportResult QueryDailyReport(ReportQueryRequest request, CollectorOptions options)
    {
        var filters = (request.Filters ?? new ReportFilters()).Normalize();
        var dayStartMs = BeijingTime.GetDayStartUtcMs(request.DateBj);
        var dayEndMs = dayStartMs + 24L * BeijingTime.HourMs;
        var nowMs = _clock.UtcNowMs;

        CollectorStateRecord? state = null;
        var usageRows = new List<(long HourMs, string App, string Domain, string Node, string? NodeFilterKey, long Up, long Down, bool? Verified)>();
        var diagByHour = new Dictionary<long, HourlyDiagnosticDelta>();
        var segmentsByHour = new Dictionary<long, List<(long Start, long End)>>();
        var observedSegsByHour = new Dictionary<long, List<(SelectedNodeTarget Target, long Start, long End)>>();

        var queriedObservedTargets = ResolveQueriedObservedTargets(options.SelectedNodes, filters.Node);

        if (File.Exists(_paths.DatabasePath))
        {
            using var conn = OpenReadOnly();
            state = ReadCollectorState(conn);
            if (request.Scope == ReportScope.SelectedNodesObserved)
            {
                usageRows = ReadObservedUsageRows(conn, dayStartMs, dayEndMs, filters, options.SelectedNodes);
                observedSegsByHour = ReadAllObservedCoverageSegments(conn, dayStartMs, dayEndMs);
            }
            else if (request.Scope == ReportScope.AutoNodeObserved)
            {
                usageRows = ReadAutoObservedUsageRows(conn, dayStartMs, dayEndMs, filters);
                segmentsByHour = ReadAutoObservedCoverageSegments(conn, dayStartMs, dayEndMs);
            }
            else
            {
                usageRows = ReadUsageRows(conn, dayStartMs, dayEndMs, filters);
                segmentsByHour = ReadCoverageSegments(conn, dayStartMs, dayEndMs);
            }
            diagByHour = ReadDiagnostics(conn, dayStartMs, dayEndMs);
        }

        // 计算全局待更新短尾段：
        // - 所选节点观察口径：仅当运行中的采集器正在观察全部被查询目标时允许待更新
        // - 自动节点观察口径：仅当运行中的采集器实际启用了自动观察（auto_observed_active = 1）时允许待更新，防止旧版采集器心跳误报待更新
        var canApplyObservedPending = request.Scope switch
        {
            ReportScope.SelectedNodesObserved => AreAllTargetsActivelyObserved(queriedObservedTargets, state?.ActiveObservedTargetsJson),
            ReportScope.AutoNodeObserved => state?.AutoObservedActive == true,
            _ => true
        };

        var (hasPendingTail, pendingTailStartMs, pendingTailEndMs) = canApplyObservedPending
            ? ComputeGlobalPendingTail(state, nowMs, options.PendingMaxWindowMs)
            : (false, 0L, 0L);

        // 按 24 小时精确计算毫秒级覆盖/空档/待更新
        long dayElapsedMs = 0, dayCoveredMs = 0, dayGapMs = 0, dayPendingMs = 0;
        long dayUnconfirmedUp = 0, dayUnconfirmedDown = 0;
        long dayReconcileUp = 0, dayReconcileDown = 0;
        long dayClockAnomalies = 0;

        var hourlyItems = new List<ReportItem>(24);
        var usageByHour = usageRows
            .GroupBy(r => r.HourMs)
            .ToDictionary(g => g.Key, g => (Up: g.Sum(x => x.Up), Down: g.Sum(x => x.Down)));

        for (var h = 0; h < 24; h++)
        {
            var hStart = dayStartMs + h * BeijingTime.HourMs;
            var hEnd = hStart + BeijingTime.HourMs;

            long elapsedMs;
            long coveredMs;
            long pendingMs;
            long gapMs;

            if (hStart >= nowMs)
            {
                elapsedMs = 0;
                coveredMs = 0;
                pendingMs = 0;
                gapMs = 0;
            }
            else
            {
                var effectiveEnd = Math.Min(hEnd, nowMs);
                elapsedMs = effectiveEnd - hStart;

                List<(long Start, long End)> coveredIntervals;
                if (request.Scope == ReportScope.SelectedNodesObserved)
                {
                    // 多节点观察合计取各目标覆盖并集的交集（只有所有被查询节点都被观察到的时段才算覆盖）
                    var hourRaw = observedSegsByHour.TryGetValue(hStart, out var olist)
                        ? olist
                        : (IReadOnlyList<(SelectedNodeTarget Target, long Start, long End)>)Array.Empty<(SelectedNodeTarget, long, long)>();

                    if (queriedObservedTargets.Count == 0)
                    {
                        coveredIntervals = new List<(long, long)>();
                        coveredMs = 0L;
                    }
                    else
                    {
                        var perTargetUnions = new List<List<(long Start, long End)>>(queriedObservedTargets.Count);
                        foreach (var qTarget in queriedObservedTargets)
                        {
                            var matching = hourRaw
                                .Where(seg => seg.Target.Covers(qTarget))
                                .Select(seg => (seg.Start, seg.End));
                            var (tUnion, _) = CoverageCalculator.ComputeUnion(matching, hStart, effectiveEnd);
                            perTargetUnions.Add(tUnion);
                        }

                        var (inter, interMs) = CoverageCalculator.ComputeIntersection(perTargetUnions);
                        coveredIntervals = inter;
                        coveredMs = interMs;
                    }
                }
                else
                {
                    var rawSegs = segmentsByHour.TryGetValue(hStart, out var list)
                        ? list
                        : (IReadOnlyList<(long Start, long End)>)Array.Empty<(long, long)>();

                    var (unionIntervals, covMs) = CoverageCalculator.ComputeUnion(rawSegs, hStart, effectiveEnd);
                    coveredIntervals = unionIntervals;
                    coveredMs = covMs;
                }

                if (hasPendingTail)
                {
                    var pStart = Math.Max(hStart, pendingTailStartMs);
                    var pEnd = Math.Min(effectiveEnd, pendingTailEndMs);
                    pendingMs = CoverageCalculator.ComputePendingWithoutOverlapMs(pStart, pEnd, coveredIntervals);
                }
                else
                {
                    pendingMs = 0;
                }

                gapMs = Math.Max(0L, elapsedMs - coveredMs - pendingMs);
            }

            var status = ClassifyCoverageStatusFromMs(elapsedMs, coveredMs, gapMs, pendingMs);
            var (covSec, gapSec, pendSec) = CoverageCalculator.RoundMsToDisplaySeconds(coveredMs, gapMs, pendingMs, elapsedMs);

            dayElapsedMs += elapsedMs;
            dayCoveredMs += coveredMs;
            dayGapMs += gapMs;
            dayPendingMs += pendingMs;

            long hClockAnomalies = 0;
            if (diagByHour.TryGetValue(hStart, out var diag))
            {
                dayUnconfirmedUp += diag.UnconfirmedUploadBytes;
                dayUnconfirmedDown += diag.UnconfirmedDownloadBytes;
                dayReconcileUp += diag.ReconcileDiffUploadBytes;
                dayReconcileDown += diag.ReconcileDiffDownloadBytes;
                dayClockAnomalies += diag.ClockAnomalyCount;
                hClockAnomalies = diag.ClockAnomalyCount;
            }

            var (hUp, hDown) = usageByHour.TryGetValue(hStart, out var pair) ? pair : (0L, 0L);
            var hourLabel = h.ToString("00", CultureInfo.InvariantCulture);

            hourlyItems.Add(new ReportItem(
                Key: hourLabel,
                UploadBytes: hUp,
                DownloadBytes: hDown,
                TotalBytes: hUp + hDown,
                HourBj: hourLabel,
                CoverageStatus: status,
                CoveredSeconds: covSec,
                GapSeconds: gapSec,
                PendingSeconds: pendSec,
                CoveredMs: coveredMs,
                GapMs: gapMs,
                PendingMs: pendingMs,
                ClockAnomalyCount: hClockAnomalies));
        }

        var dayUp = usageRows.Sum(r => r.Up);
        var dayDown = usageRows.Sum(r => r.Down);
        var dayTotal = dayUp + dayDown;

        var unknownAppUp = usageRows.Where(r => r.App == MetadataNormalizer.UnknownApp).Sum(r => r.Up);
        var unknownAppTotal = usageRows.Where(r => r.App == MetadataNormalizer.UnknownApp).Sum(r => r.Up + r.Down);
        var unknownRatio = dayTotal > 0 ? Math.Round((double)unknownAppTotal / dayTotal, 6) : 0.0;

        var dayStatus = ClassifyCoverageStatusFromMs(dayElapsedMs, dayCoveredMs, dayGapMs, dayPendingMs);
        var (dayCovSec, dayGapSec, dayPendSec) = CoverageCalculator.RoundMsToDisplaySeconds(dayCoveredMs, dayGapMs, dayPendingMs, dayElapsedMs);

        var summary = new DailyReportSummary(
            UploadBytes: dayUp,
            DownloadBytes: dayDown,
            TotalBytes: dayTotal,
            UnknownAppUploadBytes: unknownAppUp,
            UnknownAppTotalBytes: unknownAppTotal,
            UnknownAppTotalRatio: unknownRatio,
            CoverageStatus: dayStatus,
            CoveredSeconds: dayCovSec,
            GapSeconds: dayGapSec,
            PendingSeconds: dayPendSec,
            CoveredMs: dayCoveredMs,
            GapMs: dayGapMs,
            PendingMs: dayPendingMs,
            UnconfirmedUploadBytes: dayUnconfirmedUp,
            UnconfirmedDownloadBytes: dayUnconfirmedDown,
            ReconcileDiffUploadBytes: dayReconcileUp,
            ReconcileDiffDownloadBytes: dayReconcileDown,
            ClockAnomalyCount: dayClockAnomalies);

        IReadOnlyList<ReportItem> items = request.GroupBy == ReportGroupBy.Hour
            ? hourlyItems
            : BuildDimensionRanking(usageRows, request.GroupBy, request.SortBy);

        var committedUntilBj = state?.LastCommittedSampleUtcMs.HasValue == true
            ? BeijingTime.FormatIso(state.LastCommittedSampleUtcMs.Value)
            : null;

        var scopeText = request.Scope switch
        {
            ReportScope.SelectedNodesObserved => "selected_nodes_observed",
            ReportScope.AutoNodeObserved => "auto_node_observed",
            _ => "confirmed_proxy"
        };

        return new DailyReportResult(
            SchemaVersion: 1,
            Scope: scopeText,
            DateBj: BeijingTime.FormatDate(request.DateBj),
            GroupBy: request.GroupBy.ToString().ToLowerInvariant(),
            SortBy: request.SortBy.ToString().ToLowerInvariant(),
            Filters: new ReportFilterView(filters.App, filters.Domain, filters.Node),
            SelectedNodes: options.SelectedNodes,
            CommittedUntilBj: committedUntilBj,
            Summary: summary,
            Items: items,
            AttributionVerified: state?.AttributionVerified,
            AttributionReasonCode: state?.AttributionReasonCode,
            AttributionReasonText: state?.AttributionReasonText);
    }

    /// <summary>
    /// 查询指定北京时间自然日内自动发现及已记录的所有实际出站节点，供桌面筛选下拉实时使用。
    /// 同名节点存在多个供应商时自动附带供应商名称区分，并排除 DIRECT/REJECT 等内置非代理项。
    /// </summary>
    public IReadOnlyList<DiscoveredNodeOption> QueryDiscoveredNodes(DateOnly dateBj)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return Array.Empty<DiscoveredNodeOption>();
        }

        var dateText = BeijingTime.FormatDate(dateBj);
        var dayStartMs = BeijingTime.GetDayStartUtcMs(dateBj);
        var dayEndMs = dayStartMs + 24L * BeijingTime.HourMs;
        var pairs = new HashSet<(string Node, string Provider)>();

        using var conn = OpenReadOnly();

        // 1. 从 discovered_nodes 读取自动发现的出站节点（含 Provider）
        if (TableExists(conn, "discovered_nodes"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
            cmd.CommandText = "SELECT node, provider FROM discovered_nodes WHERE date_bj = $date;";
            cmd.Parameters.AddWithValue("$date", dateText);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var node = reader.GetString(0);
                var provider = reader.GetString(1);
                if (!SettingsLoader.IsBuiltInNonProxy(node))
                {
                    pairs.Add((node, provider));
                }
            }
        }

        // 2. 补充 hourly_auto_observed_usage 与 hourly_observed_node_usage 中已有流量的节点
        foreach (var table in new[] { "hourly_auto_observed_usage", "hourly_observed_node_usage" })
        {
            if (!TableExists(conn, table))
            {
                continue;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
            cmd.CommandText = $"""
                SELECT DISTINCT node, provider
                FROM {table}
                WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end;
                """;
            cmd.Parameters.AddWithValue("$start", dayStartMs);
            cmd.Parameters.AddWithValue("$end", dayEndMs);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var node = reader.GetString(0);
                var provider = reader.GetString(1);
                if (!SettingsLoader.IsBuiltInNonProxy(node))
                {
                    pairs.Add((node, provider));
                }
            }
        }

        // 3. 补充 hourly_proxy_usage 中已确认的节点（保留 provider 维度）
        if (TableExists(conn, "hourly_proxy_usage"))
        {
            var hasProviderCol = HasColumn(conn, "hourly_proxy_usage", "provider");
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
            cmd.CommandText = hasProviderCol
                ? """
                  SELECT DISTINCT node, provider
                  FROM hourly_proxy_usage
                  WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end;
                  """
                : """
                  SELECT DISTINCT node
                  FROM hourly_proxy_usage
                  WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end;
                  """;
            cmd.Parameters.AddWithValue("$start", dayStartMs);
            cmd.Parameters.AddWithValue("$end", dayEndMs);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var node = reader.GetString(0);
                var provider = hasProviderCol ? reader.GetString(1) : string.Empty;
                if (!SettingsLoader.IsBuiltInNonProxy(node))
                {
                    if (hasProviderCol || !pairs.Any(p => string.Equals(p.Node, node, StringComparison.Ordinal)))
                    {
                        pairs.Add((node, provider));
                    }
                }
            }
        }

        // 同名节点跨多个供应商时显示供应商名称，否则直接显示节点原名
        var providerCountsByNode = pairs
            .GroupBy(p => p.Node, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return pairs
            .OrderBy(p => p.Node, StringComparer.Ordinal)
            .ThenBy(p => p.Provider, StringComparer.Ordinal)
            .Select(p =>
            {
                var hasDuplicateName = providerCountsByNode[p.Node] > 1;
                var label = hasDuplicateName
                    ? SelectedNodeTarget.FormatDisplayKey(p.Node, p.Provider)
                    : (string.IsNullOrEmpty(p.Provider) ? p.Node : $"{p.Node} ({p.Provider})");
                var exactKey = SelectedNodeTarget.FormatExactTargetKey(p.Node, p.Provider);
                var confirmedKey = SelectedNodeTarget.FormatStructuredKey(p.Provider, p.Node);
                return new DiscoveredNodeOption(
                    Node: p.Node,
                    Provider: p.Provider,
                    DisplayLabel: label,
                    ConfirmedFilterKey: confirmedKey,
                    ObservedFilterKey: exactKey);
            })
            .ToList();
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlyList<CsvExportRow> QueryExportRows(
        DateOnly dateBj,
        ReportFilters? rawFilters = null,
        ReportScope scope = ReportScope.ConfirmedProxy,
        IReadOnlyList<string>? selectedNodes = null)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return Array.Empty<CsvExportRow>();
        }

        var filters = (rawFilters ?? new ReportFilters()).Normalize();
        var dayStartMs = BeijingTime.GetDayStartUtcMs(dateBj);
        var dayEndMs = dayStartMs + 24L * BeijingTime.HourMs;
        var dateText = BeijingTime.FormatDate(dateBj);

        using var conn = OpenReadOnly();
        var rows = scope switch
        {
            ReportScope.SelectedNodesObserved => ReadObservedUsageRows(conn, dayStartMs, dayEndMs, filters, selectedNodes),
            ReportScope.AutoNodeObserved => ReadAutoObservedUsageRows(conn, dayStartMs, dayEndMs, filters),
            _ => ReadUsageRows(conn, dayStartMs, dayEndMs, filters)
        };

        return rows
            .GroupBy(r => (r.HourMs, r.App, r.Domain, r.Node))
            .Select(g =>
            {
                var up = g.Sum(x => x.Up);
                var down = g.Sum(x => x.Down);
                return new CsvExportRow(
                    DateBj: dateText,
                    HourBj: BeijingTime.GetBeijingHour(g.Key.HourMs).ToString("00", CultureInfo.InvariantCulture),
                    App: g.Key.App,
                    Domain: g.Key.Domain,
                    Node: g.Key.Node,
                    UploadBytes: up,
                    DownloadBytes: down,
                    TotalBytes: up + down);
            })
            .OrderBy(r => r.HourBj, StringComparer.Ordinal)
            .ThenByDescending(r => r.UploadBytes)
            .ThenByDescending(r => r.DownloadBytes)
            .ThenBy(r => r.App, StringComparer.Ordinal)
            .ThenBy(r => r.Domain, StringComparer.Ordinal)
            .ThenBy(r => r.Node, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 严格基于原始毫秒判定小时/当日覆盖状态，严禁使用取整后的秒数。
    /// </summary>
    public static string ClassifyCoverageStatusFromMs(long elapsedMs, long coveredMs, long gapMs, long pendingMs)
    {
        if (elapsedMs <= 0)
        {
            return "future";
        }

        if (coveredMs <= 0)
        {
            return pendingMs > 0 ? "pending" : "no_data";
        }

        if (gapMs > 0)
        {
            return "partial_gap";
        }

        // 尾段仍有未提交数据时，完整覆盖尚未成立
        if (pendingMs > 0)
        {
            return "updating";
        }

        return "complete";
    }

    private static bool IsCollectorActive(CollectorStateRecord state, long nowUtcMs, long pendingMaxWindowMs) =>
        string.Equals(state.CollectorStatus, "running", StringComparison.OrdinalIgnoreCase) &&
        state.LastHeartbeatUtcMs.HasValue &&
        (nowUtcMs - state.LastHeartbeatUtcMs.Value) >= 0 &&
        (nowUtcMs - state.LastHeartbeatUtcMs.Value) <= pendingMaxWindowMs;

    private static (bool HasTail, long TailStartMs, long TailEndMs) ComputeGlobalPendingTail(
        CollectorStateRecord? state,
        long nowUtcMs,
        long pendingMaxWindowMs)
    {
        if (state is null || !IsCollectorActive(state, nowUtcMs, pendingMaxWindowMs))
        {
            return (false, 0, 0);
        }

        long? tailAnchor = null;
        if (state.CurrentRunStartUtcMs.HasValue && state.LastCommittedSampleUtcMs.HasValue)
        {
            tailAnchor = Math.Max(state.CurrentRunStartUtcMs.Value, state.LastCommittedSampleUtcMs.Value);
        }
        else if (state.CurrentRunStartUtcMs.HasValue)
        {
            tailAnchor = state.CurrentRunStartUtcMs.Value;
        }
        else if (state.LastCommittedSampleUtcMs.HasValue)
        {
            tailAnchor = state.LastCommittedSampleUtcMs.Value;
        }

        if (!tailAnchor.HasValue || tailAnchor.Value >= nowUtcMs)
        {
            return (false, 0, 0);
        }

        if ((nowUtcMs - tailAnchor.Value) > pendingMaxWindowMs)
        {
            return (false, 0, 0);
        }

        return (true, tailAnchor.Value, nowUtcMs);
    }

    private static List<ReportItem> BuildDimensionRanking(
        List<(long HourMs, string App, string Domain, string Node, string? NodeFilterKey, long Up, long Down, bool? Verified)> rows,
        ReportGroupBy groupBy,
        ReportSortBy sortBy)
    {
        var grouped = rows
            .GroupBy(r => groupBy switch
            {
                ReportGroupBy.App => r.App,
                ReportGroupBy.Domain => r.Domain,
                ReportGroupBy.Node => r.Node,
                _ => r.App
            }, StringComparer.Ordinal)
            .Select(g =>
            {
                var up = g.Sum(x => x.Up);
                var down = g.Sum(x => x.Down);
                string? filterKey = groupBy == ReportGroupBy.Node ? g.First().NodeFilterKey : null;
                string? verificationStatus = null;
                if (g.Any(x => x.Verified.HasValue))
                {
                    verificationStatus = g.All(x => x.Verified == true) ? "confirmed" : "unconfirmed";
                }
                return new ReportItem(g.Key, up, down, up + down, FilterKey: filterKey, VerificationStatus: verificationStatus);
            });

        var sorted = sortBy switch
        {
            ReportSortBy.Download => grouped
                .OrderByDescending(i => i.DownloadBytes)
                .ThenByDescending(i => i.UploadBytes)
                .ThenBy(i => i.Key, StringComparer.Ordinal),
            ReportSortBy.Total => grouped
                .OrderByDescending(i => i.TotalBytes)
                .ThenByDescending(i => i.UploadBytes)
                .ThenBy(i => i.Key, StringComparer.Ordinal),
            _ => grouped
                .OrderByDescending(i => i.UploadBytes)
                .ThenByDescending(i => i.DownloadBytes)
                .ThenBy(i => i.Key, StringComparer.Ordinal)
        };

        return sorted.ToList();
    }

    private SqliteConnection OpenReadOnly()
    {
        var conn = new SqliteConnection(SqliteLedgerStore.BuildReadOnlyConnectionString(_paths.DatabasePath).ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static CollectorStateRecord ReadCollectorState(SqliteConnection conn)
    {
        var hasObsCol = HasColumn(conn, "collector_state", "active_observed_targets_json");
        var hasAttrCols = HasColumn(conn, "collector_state", "attribution_verified");
        var hasAutoObsCol = HasColumn(conn, "collector_state", "auto_observed_active");
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = (hasObsCol, hasAttrCols, hasAutoObsCol) switch
        {
            (true, true, true) => """
                SELECT last_batch_id, collector_status, active_pipe_name, mihomo_version,
                       current_run_start_utc_ms, last_committed_sample_utc_ms, last_heartbeat_utc_ms,
                       last_stopped_utc_ms, last_error_code, last_error_message, last_error_utc_ms,
                       last_retention_cleanup_date_bj, active_observed_targets_json,
                       attribution_verified, attribution_reason_code, attribution_reason_text,
                       auto_observed_active
                FROM collector_state
                WHERE id = 1;
                """,
            (true, true, false) => """
                SELECT last_batch_id, collector_status, active_pipe_name, mihomo_version,
                       current_run_start_utc_ms, last_committed_sample_utc_ms, last_heartbeat_utc_ms,
                       last_stopped_utc_ms, last_error_code, last_error_message, last_error_utc_ms,
                       last_retention_cleanup_date_bj, active_observed_targets_json,
                       attribution_verified, attribution_reason_code, attribution_reason_text
                FROM collector_state
                WHERE id = 1;
                """,
            (true, false, _) => """
                SELECT last_batch_id, collector_status, active_pipe_name, mihomo_version,
                       current_run_start_utc_ms, last_committed_sample_utc_ms, last_heartbeat_utc_ms,
                       last_stopped_utc_ms, last_error_code, last_error_message, last_error_utc_ms,
                       last_retention_cleanup_date_bj, active_observed_targets_json
                FROM collector_state
                WHERE id = 1;
                """,
            _ => """
                SELECT last_batch_id, collector_status, active_pipe_name, mihomo_version,
                       current_run_start_utc_ms, last_committed_sample_utc_ms, last_heartbeat_utc_ms,
                       last_stopped_utc_ms, last_error_code, last_error_message, last_error_utc_ms,
                       last_retention_cleanup_date_bj
                FROM collector_state
                WHERE id = 1;
                """
        };

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new CollectorStateRecord(0, "stopped", null, null, null, null, null, null, null, null, null, null, null, null, null, null, false);
        }

        return new CollectorStateRecord(
            LastBatchId: reader.GetInt64(0),
            CollectorStatus: reader.GetString(1),
            ActivePipeName: reader.IsDBNull(2) ? null : reader.GetString(2),
            MihomoVersion: reader.IsDBNull(3) ? null : reader.GetString(3),
            CurrentRunStartUtcMs: reader.IsDBNull(4) ? null : reader.GetInt64(4),
            LastCommittedSampleUtcMs: reader.IsDBNull(5) ? null : reader.GetInt64(5),
            LastHeartbeatUtcMs: reader.IsDBNull(6) ? null : reader.GetInt64(6),
            LastStoppedUtcMs: reader.IsDBNull(7) ? null : reader.GetInt64(7),
            LastErrorCode: reader.IsDBNull(8) ? null : reader.GetString(8),
            LastErrorMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
            LastErrorUtcMs: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            LastRetentionCleanupDateBj: reader.IsDBNull(11) ? null : reader.GetString(11),
            ActiveObservedTargetsJson: hasObsCol && !reader.IsDBNull(12) ? reader.GetString(12) : null,
            AttributionVerified: hasObsCol && hasAttrCols && !reader.IsDBNull(13) ? reader.GetInt64(13) != 0 : null,
            AttributionReasonCode: hasObsCol && hasAttrCols && !reader.IsDBNull(14) ? reader.GetString(14) : null,
            AttributionReasonText: hasObsCol && hasAttrCols && !reader.IsDBNull(15) ? reader.GetString(15) : null,
            AutoObservedActive: hasObsCol && hasAttrCols && hasAutoObsCol && !reader.IsDBNull(16) && reader.GetInt64(16) != 0);
    }

    private static bool HasColumn(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static (string? Earliest, string? Latest) ReadDateBounds(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = """
            SELECT MIN(h), MAX(h) FROM (
                SELECT hour_start_utc_ms AS h FROM hourly_proxy_usage
                UNION ALL
                SELECT hour_start_utc_ms AS h FROM hourly_diagnostics
                UNION ALL
                SELECT hour_start_utc_ms AS h FROM coverage_segments
            );
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (null, null);
        }

        var minMs = reader.GetInt64(0);
        var maxMs = reader.GetInt64(1);
        return (
            BeijingTime.FormatDate(BeijingTime.GetBeijingDate(minMs)),
            BeijingTime.FormatDate(BeijingTime.GetBeijingDate(maxMs)));
    }

    private static List<(long HourMs, string App, string Domain, string Node, string? NodeFilterKey, long Up, long Down, bool? Verified)> ReadUsageRows(
        SqliteConnection conn,
        long dayStartMs,
        long dayEndMs,
        ReportFilters filters)
    {
        var hasProviderCol = HasColumn(conn, "hourly_proxy_usage", "provider");
        // 严格账本筛选：仅当显式传入结构化键 ntv2::["provider","node"] 时按 (provider, node) 精确匹配；
        // 其余一律按节点原名精确字面匹配（兼容原名含 :: 的节点如 HK::01，且 "::" 不会退回全量）
        var hasStructuredNodeFilter = SelectedNodeTarget.TryParseStructured(filters.Node, out var structuredFilter);

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;

        var sql = hasProviderCol
            ? """
              SELECT hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes
              FROM hourly_proxy_usage
              WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
              """
            : """
              SELECT hour_start_utc_ms, app, domain, node, upload_bytes, download_bytes
              FROM hourly_proxy_usage
              WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
              """;

        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        if (filters.App is not null)
        {
            sql += " AND app = $app";
            cmd.Parameters.AddWithValue("$app", filters.App);
        }
        if (filters.Domain is not null)
        {
            sql += " AND domain = $domain";
            cmd.Parameters.AddWithValue("$domain", filters.Domain);
        }

        cmd.CommandText = sql + ";";

        var list = new List<(long, string, string, string, string?, long, long, bool?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = reader.GetString(3);
            var provider = hasProviderCol ? reader.GetString(4) : string.Empty;
            var up = reader.GetInt64(hasProviderCol ? 5 : 4);
            var down = reader.GetInt64(hasProviderCol ? 6 : 5);

            if (filters.Node is not null)
            {
                if (hasStructuredNodeFilter)
                {
                    if (!structuredFilter!.Matches(node, provider))
                    {
                        continue;
                    }
                }
                else if (!string.Equals(node, filters.Node, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var displayNode = SelectedNodeTarget.FormatDisplayKey(node, provider);
            var exactFilterKey = SelectedNodeTarget.FormatStructuredKey(provider, node);
            list.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                displayNode,
                exactFilterKey,
                up,
                down,
                null));
        }

        return list;
    }

    private static List<(long HourMs, string App, string Domain, string Node, string? NodeFilterKey, long Up, long Down, bool? Verified)> ReadAutoObservedUsageRows(
        SqliteConnection conn,
        long dayStartMs,
        long dayEndMs,
        ReportFilters filters)
    {
        if (!TableExists(conn, "hourly_auto_observed_usage"))
        {
            return new List<(long, string, string, string, string?, long, long, bool?)>();
        }

        SelectedNodeTarget? nodeFilterTarget = null;
        if (filters.Node is not null)
        {
            nodeFilterTarget = SelectedNodeTarget.TryParse(filters.Node);
            // 非法节点筛选键（例如 "::"）绝不退回全量查询，直接返回空集
            if (nodeFilterTarget is null)
            {
                return new List<(long, string, string, string, string?, long, long, bool?)>();
            }
        }

        var hasVerifiedCol = HasColumn(conn, "hourly_auto_observed_usage", "verified");

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;

        var sql = hasVerifiedCol
            ? """
              SELECT hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes, verified
              FROM hourly_auto_observed_usage
              WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
              """
            : """
              SELECT hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes
              FROM hourly_auto_observed_usage
              WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
              """;

        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        if (filters.App is not null)
        {
            sql += " AND app = $app";
            cmd.Parameters.AddWithValue("$app", filters.App);
        }
        if (filters.Domain is not null)
        {
            sql += " AND domain = $domain";
            cmd.Parameters.AddWithValue("$domain", filters.Domain);
        }

        cmd.CommandText = sql + ";";

        var list = new List<(long, string, string, string, string?, long, long, bool?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = reader.GetString(3);
            var provider = reader.GetString(4);

            if (nodeFilterTarget is not null && !nodeFilterTarget.Matches(node, provider))
            {
                continue;
            }

            var displayNode = SelectedNodeTarget.FormatDisplayKey(node, provider);
            var exactFilterKey = SelectedNodeTarget.FormatExactTargetKey(node, provider);
            var verified = hasVerifiedCol && reader.GetInt64(7) != 0;
            list.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                displayNode,
                exactFilterKey,
                reader.GetInt64(5),
                reader.GetInt64(6),
                verified));
        }

        return list;
    }

    private static List<(long HourMs, string App, string Domain, string Node, string? NodeFilterKey, long Up, long Down, bool? Verified)> ReadObservedUsageRows(
        SqliteConnection conn,
        long dayStartMs,
        long dayEndMs,
        ReportFilters filters,
        IReadOnlyList<string>? selectedNodes)
    {
        // 明确口径：所选节点观察报表与导出始终只查询当前所选名单；名单为空时返回空集，绝不扩大到全部历史节点
        var selectedTargets = SelectedNodeTarget.ParseAll(selectedNodes);
        if (selectedTargets.Count == 0)
        {
            return new List<(long, string, string, string, string?, long, long, bool?)>();
        }

        // 兼容旧版尚未建表的只读库
        using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
            existsCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'hourly_observed_node_usage';";
            if (existsCmd.ExecuteScalar() is null)
            {
                return new List<(long, string, string, string, string?, long, long, bool?)>();
            }
        }

        SelectedNodeTarget? nodeFilterTarget = null;
        if (filters.Node is not null)
        {
            nodeFilterTarget = SelectedNodeTarget.TryParse(filters.Node);
            if (nodeFilterTarget is null)
            {
                return new List<(long, string, string, string, string?, long, long, bool?)>();
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;

        // 严格保留 provider 维度，绝不把不同 Provider 下的同名节点合并成一条记录
        var sql = """
            SELECT hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes
            FROM hourly_observed_node_usage
            WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
            """;

        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        if (filters.App is not null)
        {
            sql += " AND app = $app";
            cmd.Parameters.AddWithValue("$app", filters.App);
        }
        if (filters.Domain is not null)
        {
            sql += " AND domain = $domain";
            cmd.Parameters.AddWithValue("$domain", filters.Domain);
        }

        cmd.CommandText = sql + ";";

        var list = new List<(long, string, string, string, string?, long, long, bool?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var node = reader.GetString(3);
            var provider = reader.GetString(4);

            if (!SelectedNodeTarget.MatchesAny(selectedTargets, node, provider))
            {
                continue;
            }

            if (nodeFilterTarget is not null && !nodeFilterTarget.Matches(node, provider))
            {
                continue;
            }

            var displayNode = SelectedNodeTarget.FormatDisplayKey(node, provider);
            var exactFilterKey = SelectedNodeTarget.FormatExactTargetKey(node, provider);
            list.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                displayNode,
                exactFilterKey,
                reader.GetInt64(5),
                reader.GetInt64(6),
                null));
        }

        return list;
    }

    private static IReadOnlyList<SelectedNodeTarget> ResolveQueriedObservedTargets(
        IReadOnlyList<string>? selectedNodes,
        string? nodeFilter)
    {
        var selectedTargets = SelectedNodeTarget.ParseAll(selectedNodes);
        if (selectedTargets.Count == 0)
        {
            return Array.Empty<SelectedNodeTarget>();
        }

        if (string.IsNullOrWhiteSpace(nodeFilter))
        {
            return selectedTargets;
        }

        var filterTarget = SelectedNodeTarget.TryParse(nodeFilter);
        if (filterTarget is null)
        {
            return Array.Empty<SelectedNodeTarget>();
        }

        // 严格取「当前名单」与「节点筛选条件」的交集：
        // 1. 若名单中存在目标覆盖该筛选目标（例如名单为 HK-01，筛选为 paid::HK-01 或 ::HK-01），交集为该精确筛选目标
        if (selectedTargets.Any(t => t.Covers(filterTarget)))
        {
            return new[] { filterTarget };
        }

        // 2. 若筛选目标比名单更宽泛（例如名单为 paid::HK-01，筛选为裸名 HK-01），交集为名单中被筛选命中的具体目标集合
        return selectedTargets.Where(t => filterTarget.Covers(t)).ToList();
    }

    private static bool AreAllTargetsActivelyObserved(
        IReadOnlyList<SelectedNodeTarget> queriedTargets,
        string? activeObservedTargetsJson)
    {
        if (queriedTargets.Count == 0 || string.IsNullOrWhiteSpace(activeObservedTargetsJson))
        {
            return false;
        }

        List<string>? rawActive;
        try
        {
            rawActive = System.Text.Json.JsonSerializer.Deserialize<List<string>>(activeObservedTargetsJson);
        }
        catch
        {
            return false;
        }

        var activeTargets = SelectedNodeTarget.ParseAll(rawActive);
        if (activeTargets.Count == 0)
        {
            return false;
        }

        // 必须每一个被查询目标都被当前运行中的采集器所覆盖，才允许计算待更新尾段
        return queriedTargets.All(q => activeTargets.Any(a => a.Covers(q)));
    }

    private static Dictionary<long, List<(SelectedNodeTarget Target, long Start, long End)>> ReadAllObservedCoverageSegments(
        SqliteConnection conn,
        long dayStartMs,
        long dayEndMs)
    {
        var dict = new Dictionary<long, List<(SelectedNodeTarget, long, long)>>();

        using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
            existsCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'observed_coverage_segments';";
            if (existsCmd.ExecuteScalar() is null)
            {
                return dict;
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = """
            SELECT hour_start_utc_ms, target_key, start_utc_ms, end_utc_ms
            FROM observed_coverage_segments
            WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
            ORDER BY hour_start_utc_ms, start_utc_ms;
            """;
        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var parsedTarget = SelectedNodeTarget.TryParse(reader.GetString(1));
            if (parsedTarget is null)
            {
                continue;
            }

            var h = reader.GetInt64(0);
            if (!dict.TryGetValue(h, out var list))
            {
                list = new List<(SelectedNodeTarget, long, long)>();
                dict[h] = list;
            }
            list.Add((parsedTarget, reader.GetInt64(2), reader.GetInt64(3)));
        }

        return dict;
    }

    private static Dictionary<long, HourlyDiagnosticDelta> ReadDiagnostics(SqliteConnection conn, long dayStartMs, long dayEndMs)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = """
            SELECT hour_start_utc_ms, sample_count,
                   unconfirmed_upload_bytes, unconfirmed_download_bytes,
                   reconcile_diff_upload_bytes, reconcile_diff_download_bytes,
                   clock_anomaly_count
            FROM hourly_diagnostics
            WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end;
            """;
        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        var dict = new Dictionary<long, HourlyDiagnosticDelta>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var h = reader.GetInt64(0);
            dict[h] = new HourlyDiagnosticDelta(
                h,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6));
        }

        return dict;
    }

    private static Dictionary<long, List<(long Start, long End)>> ReadAutoObservedCoverageSegments(SqliteConnection conn, long dayStartMs, long dayEndMs)
    {
        var dict = new Dictionary<long, List<(long, long)>>();
        if (!TableExists(conn, "auto_observed_coverage_segments"))
        {
            return dict;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = """
            SELECT hour_start_utc_ms, start_utc_ms, end_utc_ms
            FROM auto_observed_coverage_segments
            WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
            ORDER BY hour_start_utc_ms, start_utc_ms;
            """;
        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var h = reader.GetInt64(0);
            if (!dict.TryGetValue(h, out var list))
            {
                list = new List<(long, long)>();
                dict[h] = list;
            }
            list.Add((reader.GetInt64(1), reader.GetInt64(2)));
        }

        return dict;
    }

    private static Dictionary<long, List<(long Start, long End)>> ReadCoverageSegments(SqliteConnection conn, long dayStartMs, long dayEndMs)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = SqliteLedgerStore.DefaultTimeoutSeconds;
        cmd.CommandText = """
            SELECT hour_start_utc_ms, start_utc_ms, end_utc_ms
            FROM coverage_segments
            WHERE hour_start_utc_ms >= $start AND hour_start_utc_ms < $end
            ORDER BY hour_start_utc_ms, start_utc_ms;
            """;
        cmd.Parameters.AddWithValue("$start", dayStartMs);
        cmd.Parameters.AddWithValue("$end", dayEndMs);

        var dict = new Dictionary<long, List<(long, long)>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var h = reader.GetInt64(0);
            if (!dict.TryGetValue(h, out var list))
            {
                list = new List<(long, long)>();
                dict[h] = list;
            }
            list.Add((reader.GetInt64(1), reader.GetInt64(2)));
        }

        return dict;
    }
}
