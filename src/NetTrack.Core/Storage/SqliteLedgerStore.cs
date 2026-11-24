using Microsoft.Data.Sqlite;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;

namespace NetTrack.Core.Storage;

public sealed record CollectorStateRecord(
    long LastBatchId,
    string CollectorStatus,
    string? ActivePipeName,
    string? MihomoVersion,
    long? CurrentRunStartUtcMs,
    long? LastCommittedSampleUtcMs,
    long? LastHeartbeatUtcMs,
    long? LastStoppedUtcMs,
    string? LastErrorCode,
    string? LastErrorMessage,
    long? LastErrorUtcMs,
    string? LastRetentionCleanupDateBj,
    string? ActiveObservedTargetsJson = null,
    bool? AttributionVerified = null,
    string? AttributionReasonCode = null,
    string? AttributionReasonText = null,
    bool AutoObservedActive = false);

/// <summary>
/// 内存待提交批次，收集最多 5 秒周期内的代理增量、诊断增量、有效覆盖段、自动发现节点与最新状态。
/// </summary>
public sealed class PendingBatch
{
    public long BatchId { get; }
    public Dictionary<ProxyUsageKey, BytePair> ProxyDeltas { get; } = new();
    public Dictionary<ObservedNodeUsageKey, BytePair> ObservedNodeDeltas { get; } = new();
    public Dictionary<ObservedNodeUsageKey, BytePair> AutoObservedNodeDeltas { get; } = new();
    public Dictionary<long, HourlyDiagnosticDelta> DiagnosticDeltas { get; } = new();
    public List<CoverageSegment> CoverageSegments { get; } = new();
    public List<CoverageSegment> AutoObservedCoverageSegments { get; } = new();
    public List<ObservedCoverageSegment> ObservedCoverageSegments { get; } = new();
    public Dictionary<DiscoveredNodeKey, (long FirstSeenUtcMs, long LastSeenUtcMs)> DiscoveredNodes { get; } = new();

    public string CollectorStatus { get; set; } = "running";
    public string? ActivePipeName { get; set; }
    public string? MihomoVersion { get; set; }
    public long? CurrentRunStartUtcMs { get; set; }
    public long? LastSampleUtcMs { get; set; }
    public long? LastStoppedUtcMs { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public long? LastErrorUtcMs { get; set; }
    public string? ActiveObservedTargetsJson { get; set; }
    public bool? AttributionVerified { get; set; }
    public string? AttributionReasonCode { get; set; }
    public string? AttributionReasonText { get; set; }
    public bool AutoObservedActive { get; set; }

    public bool HasAccountingData =>
        ProxyDeltas.Count > 0 || ObservedNodeDeltas.Count > 0 || AutoObservedNodeDeltas.Count > 0 ||
        DiagnosticDeltas.Count > 0 || CoverageSegments.Count > 0 || DiscoveredNodes.Count > 0 || LastSampleUtcMs.HasValue;

    public PendingBatch(long batchId)
    {
        BatchId = batchId;
    }

    public void AddFrameResult(AccountingFrameResult frame)
    {
        LastSampleUtcMs = frame.SampleReceiveUtcMs;
        CurrentRunStartUtcMs = frame.CurrentRunStartUtcMs;

        foreach (var (key, bytes) in frame.ProxyDeltas)
        {
            ProxyDeltas[key] = ProxyDeltas.TryGetValue(key, out var existing)
                ? existing.Add(bytes.UploadBytes, bytes.DownloadBytes)
                : bytes;
        }

        foreach (var (key, bytes) in frame.ObservedSelectedNodeDeltas)
        {
            ObservedNodeDeltas[key] = ObservedNodeDeltas.TryGetValue(key, out var existing)
                ? existing.Add(bytes.UploadBytes, bytes.DownloadBytes)
                : bytes;
        }

        foreach (var (key, bytes) in frame.AutoObservedNodeDeltas)
        {
            AutoObservedNodeDeltas[key] = AutoObservedNodeDeltas.TryGetValue(key, out var existing)
                ? existing.Add(bytes.UploadBytes, bytes.DownloadBytes)
                : bytes;
        }

        if (frame.DiagnosticDelta.HasValue)
        {
            var d = frame.DiagnosticDelta.Value;
            if (DiagnosticDeltas.TryGetValue(d.HourStartUtcMs, out var existing))
            {
                DiagnosticDeltas[d.HourStartUtcMs] = new HourlyDiagnosticDelta(
                    d.HourStartUtcMs,
                    existing.SampleCount + d.SampleCount,
                    existing.UnconfirmedUploadBytes + d.UnconfirmedUploadBytes,
                    existing.UnconfirmedDownloadBytes + d.UnconfirmedDownloadBytes,
                    existing.ReconcileDiffUploadBytes + d.ReconcileDiffUploadBytes,
                    existing.ReconcileDiffDownloadBytes + d.ReconcileDiffDownloadBytes,
                    existing.ClockAnomalyCount + d.ClockAnomalyCount);
            }
            else
            {
                DiagnosticDeltas[d.HourStartUtcMs] = d;
            }
        }

        if (frame.CoverageSegments.Count > 0)
        {
            CoverageSegments.AddRange(frame.CoverageSegments);
            AutoObservedCoverageSegments.AddRange(frame.CoverageSegments);
        }

        if (frame.ObservedCoverageSegments.Count > 0)
        {
            ObservedCoverageSegments.AddRange(frame.ObservedCoverageSegments);
        }

        foreach (var nodeKey in frame.DiscoveredNodes)
        {
            if (DiscoveredNodes.TryGetValue(nodeKey, out var existing))
            {
                DiscoveredNodes[nodeKey] = (
                    Math.Min(existing.FirstSeenUtcMs, frame.SampleReceiveUtcMs),
                    Math.Max(existing.LastSeenUtcMs, frame.SampleReceiveUtcMs));
            }
            else
            {
                DiscoveredNodes[nodeKey] = (frame.SampleReceiveUtcMs, frame.SampleReceiveUtcMs);
            }
        }

        AttributionVerified = frame.AttributionVerified;
        AttributionReasonCode = frame.AttributionReasonCode;
        AttributionReasonText = frame.AttributionReasonText;
    }
}

public interface ILedgerWriter : IDisposable
{
    void Initialize();
    long GetLastBatchId();
    string? GetLastRetentionCleanupDateBj();
    void CommitBatch(PendingBatch batch, long heartbeatUtcMs, int? singleAttemptTimeoutSeconds = null);
    int CleanupExpiredData(DateOnly todayBj, int retentionDays);
    void CloseAndClearPool();
}

/// <summary>
/// SQLite WAL 账本写入实现。仅由持有 collector.lock 的唯一采集写入者使用。
/// </summary>
public sealed class SqliteLedgerStore : ILedgerWriter
{
    public const int DefaultTimeoutSeconds = 5;

    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public SqliteLedgerStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public static SqliteConnectionStringBuilder BuildWriteConnectionString(string databasePath) => new()
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = DefaultTimeoutSeconds
    };

    public static SqliteConnectionStringBuilder BuildReadOnlyConnectionString(string databasePath) => new()
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = true,
        DefaultTimeout = DefaultTimeoutSeconds
    };

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection(BuildWriteConnectionString(_databasePath).ToString());
        _connection.Open();

        ExecuteNonQuery(_connection, null, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            """, DefaultTimeoutSeconds);

        using var tx = _connection.BeginTransaction();
        ExecuteNonQuery(_connection, tx, """
            CREATE TABLE IF NOT EXISTS schema_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('schema_version', '1');

            CREATE TABLE IF NOT EXISTS hourly_proxy_usage (
                hour_start_utc_ms INTEGER NOT NULL,
                app TEXT NOT NULL,
                domain TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                upload_bytes INTEGER NOT NULL DEFAULT 0 CHECK (upload_bytes >= 0),
                download_bytes INTEGER NOT NULL DEFAULT 0 CHECK (download_bytes >= 0),
                PRIMARY KEY (hour_start_utc_ms, app, domain, node, provider)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_node
                ON hourly_proxy_usage (hour_start_utc_ms, node);
            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_app
                ON hourly_proxy_usage (hour_start_utc_ms, app);
            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_domain
                ON hourly_proxy_usage (hour_start_utc_ms, domain);

            -- 所选付费节点独立观察量表（独立于严格代理账本）
            CREATE TABLE IF NOT EXISTS hourly_observed_node_usage (
                hour_start_utc_ms INTEGER NOT NULL,
                app TEXT NOT NULL,
                domain TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                upload_bytes INTEGER NOT NULL DEFAULT 0 CHECK (upload_bytes >= 0),
                download_bytes INTEGER NOT NULL DEFAULT 0 CHECK (download_bytes >= 0),
                PRIMARY KEY (hour_start_utc_ms, app, domain, node, provider)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS idx_observed_usage_hour_node
                ON hourly_observed_node_usage (hour_start_utc_ms, node);
            CREATE INDEX IF NOT EXISTS idx_observed_usage_hour_app
                ON hourly_observed_node_usage (hour_start_utc_ms, app);
            CREATE INDEX IF NOT EXISTS idx_observed_usage_hour_domain
                ON hourly_observed_node_usage (hour_start_utc_ms, domain);

            -- 自动节点观察量表（无需手填名单，自动记录非直连/非拒绝/非内置出站流量，逐行持久化核验状态）
            CREATE TABLE IF NOT EXISTS hourly_auto_observed_usage (
                hour_start_utc_ms INTEGER NOT NULL,
                app TEXT NOT NULL,
                domain TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                verified INTEGER NOT NULL DEFAULT 0,
                upload_bytes INTEGER NOT NULL DEFAULT 0 CHECK (upload_bytes >= 0),
                download_bytes INTEGER NOT NULL DEFAULT 0 CHECK (download_bytes >= 0),
                PRIMARY KEY (hour_start_utc_ms, app, domain, node, provider, verified)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_node
                ON hourly_auto_observed_usage (hour_start_utc_ms, node);
            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_app
                ON hourly_auto_observed_usage (hour_start_utc_ms, app);
            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_domain
                ON hourly_auto_observed_usage (hour_start_utc_ms, domain);

            CREATE TABLE IF NOT EXISTS hourly_diagnostics (
                hour_start_utc_ms INTEGER PRIMARY KEY,
                sample_count INTEGER NOT NULL DEFAULT 0,
                unconfirmed_upload_bytes INTEGER NOT NULL DEFAULT 0,
                unconfirmed_download_bytes INTEGER NOT NULL DEFAULT 0,
                reconcile_diff_upload_bytes INTEGER NOT NULL DEFAULT 0,
                reconcile_diff_download_bytes INTEGER NOT NULL DEFAULT 0,
                clock_anomaly_count INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS coverage_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_start_utc_ms INTEGER NOT NULL,
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms INTEGER NOT NULL CHECK (end_utc_ms >= start_utc_ms)
            );

            CREATE INDEX IF NOT EXISTS idx_coverage_hour
                ON coverage_segments (hour_start_utc_ms, start_utc_ms);

            -- 自动节点观察独立覆盖区间（与升级前旧版 coverage_segments 严格隔离，防止旧时段被误判为「完整覆盖 0 B」）
            CREATE TABLE IF NOT EXISTS auto_observed_coverage_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_start_utc_ms INTEGER NOT NULL,
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms INTEGER NOT NULL CHECK (end_utc_ms >= start_utc_ms)
            );

            CREATE INDEX IF NOT EXISTS idx_auto_obs_coverage_hour
                ON auto_observed_coverage_segments (hour_start_utc_ms, start_utc_ms);

            -- 所选付费节点独立观察覆盖区间（用于区分「未启用观察」与「已观察且流量为 0」）
            CREATE TABLE IF NOT EXISTS observed_coverage_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_start_utc_ms INTEGER NOT NULL,
                target_key TEXT NOT NULL,
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms INTEGER NOT NULL CHECK (end_utc_ms >= start_utc_ms)
            );

            CREATE INDEX IF NOT EXISTS idx_observed_coverage_hour_target
                ON observed_coverage_segments (hour_start_utc_ms, target_key, start_utc_ms);

            -- 从连接快照自动发现的实际出站节点（供桌面与 CLI 筛选候选，独立于手工配置名单）
            CREATE TABLE IF NOT EXISTS discovered_nodes (
                date_bj TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                first_seen_utc_ms INTEGER NOT NULL,
                last_seen_utc_ms INTEGER NOT NULL,
                PRIMARY KEY (date_bj, node, provider)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS collector_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                last_batch_id INTEGER NOT NULL DEFAULT 0,
                collector_status TEXT NOT NULL DEFAULT 'stopped',
                active_pipe_name TEXT NULL,
                mihomo_version TEXT NULL,
                current_run_start_utc_ms INTEGER NULL,
                last_committed_sample_utc_ms INTEGER NULL,
                last_heartbeat_utc_ms INTEGER NULL,
                last_stopped_utc_ms INTEGER NULL,
                last_error_code TEXT NULL,
                last_error_message TEXT NULL,
                last_error_utc_ms INTEGER NULL,
                last_retention_cleanup_date_bj TEXT NULL,
                active_observed_targets_json TEXT NULL,
                attribution_verified INTEGER NULL,
                attribution_reason_code TEXT NULL,
                attribution_reason_text TEXT NULL,
                auto_observed_active INTEGER NOT NULL DEFAULT 0
            );

            INSERT OR IGNORE INTO collector_state (id, last_batch_id, collector_status)
            VALUES (1, 0, 'stopped');
            """, DefaultTimeoutSeconds);

        // 兼容旧版库自动补充新增列与主键维度迁移
        EnsureProxyUsageHasProviderColumn(_connection, tx);
        EnsureAutoObservedUsageHasVerifiedColumn(_connection, tx);
        EnsureColumnExists(_connection, tx, "collector_state", "active_observed_targets_json", "TEXT NULL");
        EnsureColumnExists(_connection, tx, "collector_state", "attribution_verified", "INTEGER NULL");
        EnsureColumnExists(_connection, tx, "collector_state", "attribution_reason_code", "TEXT NULL");
        EnsureColumnExists(_connection, tx, "collector_state", "attribution_reason_text", "TEXT NULL");
        EnsureColumnExists(_connection, tx, "collector_state", "auto_observed_active", "INTEGER NOT NULL DEFAULT 0");

        tx.Commit();
    }

    public long GetLastBatchId()
    {
        var conn = RequireConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = DefaultTimeoutSeconds;
        cmd.CommandText = "SELECT last_batch_id FROM collector_state WHERE id = 1;";
        var result = cmd.ExecuteScalar();
        return result is long l ? l : Convert.ToInt64(result ?? 0L);
    }

    public string? GetLastRetentionCleanupDateBj()
    {
        var conn = RequireConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = DefaultTimeoutSeconds;
        cmd.CommandText = "SELECT last_retention_cleanup_date_bj FROM collector_state WHERE id = 1;";
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToString(result);
    }

    public void CommitBatch(PendingBatch batch, long heartbeatUtcMs, int? singleAttemptTimeoutSeconds = null)
    {
        var conn = RequireConnection();
        var cmdTimeout = singleAttemptTimeoutSeconds ?? DefaultTimeoutSeconds;
        var busyTimeoutMs = cmdTimeout * 1000;

        ExecuteNonQuery(conn, null, $"PRAGMA busy_timeout = {busyTimeoutMs};", cmdTimeout);

        try
        {
            using var tx = conn.BeginTransaction();

            // 幂等保护：同一 batch_id 绝不重复累加
            long currentBatchId;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandTimeout = cmdTimeout;
                checkCmd.CommandText = "SELECT last_batch_id FROM collector_state WHERE id = 1;";
                currentBatchId = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0L);
            }

            if (batch.BatchId <= currentBatchId)
            {
                tx.Commit();
                return;
            }

            // 1. UPSERT 代理流量汇总（保留 provider 维度，避免不同供应商同名节点混算）
            if (batch.ProxyDeltas.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO hourly_proxy_usage (hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes)
                    VALUES ($hour, $app, $domain, $node, $provider, $up, $down)
                    ON CONFLICT (hour_start_utc_ms, app, domain, node, provider)
                    DO UPDATE SET
                        upload_bytes = upload_bytes + excluded.upload_bytes,
                        download_bytes = download_bytes + excluded.download_bytes;
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
                var pDomain = cmd.Parameters.Add("$domain", SqliteType.Text);
                var pNode = cmd.Parameters.Add("$node", SqliteType.Text);
                var pProvider = cmd.Parameters.Add("$provider", SqliteType.Text);
                var pUp = cmd.Parameters.Add("$up", SqliteType.Integer);
                var pDown = cmd.Parameters.Add("$down", SqliteType.Integer);

                foreach (var (key, bytes) in batch.ProxyDeltas)
                {
                    if (bytes.UploadBytes == 0 && bytes.DownloadBytes == 0)
                    {
                        continue;
                    }

                    pHour.Value = key.HourStartUtcMs;
                    pApp.Value = key.App;
                    pDomain.Value = key.Domain;
                    pNode.Value = key.Node;
                    pProvider.Value = key.Provider;
                    pUp.Value = bytes.UploadBytes;
                    pDown.Value = bytes.DownloadBytes;
                    cmd.ExecuteNonQuery();
                }
            }

            // 1b. UPSERT 所选付费节点独立观察量（独立于严格代理账本）
            if (batch.ObservedNodeDeltas.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO hourly_observed_node_usage (hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes)
                    VALUES ($hour, $app, $domain, $node, $provider, $up, $down)
                    ON CONFLICT (hour_start_utc_ms, app, domain, node, provider)
                    DO UPDATE SET
                        upload_bytes = upload_bytes + excluded.upload_bytes,
                        download_bytes = download_bytes + excluded.download_bytes;
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
                var pDomain = cmd.Parameters.Add("$domain", SqliteType.Text);
                var pNode = cmd.Parameters.Add("$node", SqliteType.Text);
                var pProvider = cmd.Parameters.Add("$provider", SqliteType.Text);
                var pUp = cmd.Parameters.Add("$up", SqliteType.Integer);
                var pDown = cmd.Parameters.Add("$down", SqliteType.Integer);

                foreach (var (key, bytes) in batch.ObservedNodeDeltas)
                {
                    if (bytes.UploadBytes == 0 && bytes.DownloadBytes == 0)
                    {
                        continue;
                    }

                    pHour.Value = key.HourStartUtcMs;
                    pApp.Value = key.App;
                    pDomain.Value = key.Domain;
                    pNode.Value = key.Node;
                    pProvider.Value = key.Provider;
                    pUp.Value = bytes.UploadBytes;
                    pDown.Value = bytes.DownloadBytes;
                    cmd.ExecuteNonQuery();
                }
            }

            // 1c. UPSERT 自动节点观察量（独立于严格代理账本与手工观察名单，逐行持久化核验状态）
            if (batch.AutoObservedNodeDeltas.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO hourly_auto_observed_usage (hour_start_utc_ms, app, domain, node, provider, verified, upload_bytes, download_bytes)
                    VALUES ($hour, $app, $domain, $node, $provider, $verified, $up, $down)
                    ON CONFLICT (hour_start_utc_ms, app, domain, node, provider, verified)
                    DO UPDATE SET
                        upload_bytes = upload_bytes + excluded.upload_bytes,
                        download_bytes = download_bytes + excluded.download_bytes;
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
                var pDomain = cmd.Parameters.Add("$domain", SqliteType.Text);
                var pNode = cmd.Parameters.Add("$node", SqliteType.Text);
                var pProvider = cmd.Parameters.Add("$provider", SqliteType.Text);
                var pVerified = cmd.Parameters.Add("$verified", SqliteType.Integer);
                var pUp = cmd.Parameters.Add("$up", SqliteType.Integer);
                var pDown = cmd.Parameters.Add("$down", SqliteType.Integer);

                foreach (var (key, bytes) in batch.AutoObservedNodeDeltas)
                {
                    if (bytes.UploadBytes == 0 && bytes.DownloadBytes == 0)
                    {
                        continue;
                    }

                    pHour.Value = key.HourStartUtcMs;
                    pApp.Value = key.App;
                    pDomain.Value = key.Domain;
                    pNode.Value = key.Node;
                    pProvider.Value = key.Provider;
                    pVerified.Value = key.Verified ? 1 : 0;
                    pUp.Value = bytes.UploadBytes;
                    pDown.Value = bytes.DownloadBytes;
                    cmd.ExecuteNonQuery();
                }
            }

            // 2. UPSERT 小时诊断
            if (batch.DiagnosticDeltas.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO hourly_diagnostics (
                        hour_start_utc_ms, sample_count,
                        unconfirmed_upload_bytes, unconfirmed_download_bytes,
                        reconcile_diff_upload_bytes, reconcile_diff_download_bytes,
                        clock_anomaly_count)
                    VALUES ($hour, $samples, $uUp, $uDown, $rUp, $rDown, $clockErr)
                    ON CONFLICT (hour_start_utc_ms)
                    DO UPDATE SET
                        sample_count = sample_count + excluded.sample_count,
                        unconfirmed_upload_bytes = unconfirmed_upload_bytes + excluded.unconfirmed_upload_bytes,
                        unconfirmed_download_bytes = unconfirmed_download_bytes + excluded.unconfirmed_download_bytes,
                        reconcile_diff_upload_bytes = reconcile_diff_upload_bytes + excluded.reconcile_diff_upload_bytes,
                        reconcile_diff_download_bytes = reconcile_diff_download_bytes + excluded.reconcile_diff_download_bytes,
                        clock_anomaly_count = clock_anomaly_count + excluded.clock_anomaly_count;
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pSamples = cmd.Parameters.Add("$samples", SqliteType.Integer);
                var pUUp = cmd.Parameters.Add("$uUp", SqliteType.Integer);
                var pUDown = cmd.Parameters.Add("$uDown", SqliteType.Integer);
                var pRUp = cmd.Parameters.Add("$rUp", SqliteType.Integer);
                var pRDown = cmd.Parameters.Add("$rDown", SqliteType.Integer);
                var pClock = cmd.Parameters.Add("$clockErr", SqliteType.Integer);

                foreach (var d in batch.DiagnosticDeltas.Values)
                {
                    pHour.Value = d.HourStartUtcMs;
                    pSamples.Value = d.SampleCount;
                    pUUp.Value = d.UnconfirmedUploadBytes;
                    pUDown.Value = d.UnconfirmedDownloadBytes;
                    pRUp.Value = d.ReconcileDiffUploadBytes;
                    pRDown.Value = d.ReconcileDiffDownloadBytes;
                    pClock.Value = d.ClockAnomalyCount;
                    cmd.ExecuteNonQuery();
                }
            }

            // 3. 插入合并后的有效覆盖段
            if (batch.CoverageSegments.Count > 0)
            {
                var merged = CoverageCalculator.MergeSegments(batch.CoverageSegments);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO coverage_segments (hour_start_utc_ms, start_utc_ms, end_utc_ms)
                    VALUES ($hour, $start, $end);
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pStart = cmd.Parameters.Add("$start", SqliteType.Integer);
                var pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);

                foreach (var seg in merged)
                {
                    pHour.Value = seg.HourStartUtcMs;
                    pStart.Value = seg.StartUtcMs;
                    pEnd.Value = seg.EndUtcMs;
                    cmd.ExecuteNonQuery();
                }
            }

            // 3a. 插入合并后的自动节点观察独立覆盖段
            if (batch.AutoObservedCoverageSegments.Count > 0)
            {
                var merged = CoverageCalculator.MergeSegments(batch.AutoObservedCoverageSegments);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO auto_observed_coverage_segments (hour_start_utc_ms, start_utc_ms, end_utc_ms)
                    VALUES ($hour, $start, $end);
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pStart = cmd.Parameters.Add("$start", SqliteType.Integer);
                var pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);

                foreach (var seg in merged)
                {
                    pHour.Value = seg.HourStartUtcMs;
                    pStart.Value = seg.StartUtcMs;
                    pEnd.Value = seg.EndUtcMs;
                    cmd.ExecuteNonQuery();
                }
            }

            // 3b. 插入合并后的所选节点独立观察覆盖段
            if (batch.ObservedCoverageSegments.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO observed_coverage_segments (hour_start_utc_ms, target_key, start_utc_ms, end_utc_ms)
                    VALUES ($hour, $target, $start, $end);
                    """;

                var pHour = cmd.Parameters.Add("$hour", SqliteType.Integer);
                var pTarget = cmd.Parameters.Add("$target", SqliteType.Text);
                var pStart = cmd.Parameters.Add("$start", SqliteType.Integer);
                var pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);

                foreach (var byTarget in batch.ObservedCoverageSegments.GroupBy(s => s.TargetKey, StringComparer.Ordinal))
                {
                    var merged = CoverageCalculator.MergeSegments(
                        byTarget.Select(s => new CoverageSegment(s.HourStartUtcMs, s.StartUtcMs, s.EndUtcMs)));
                    foreach (var seg in merged)
                    {
                        pHour.Value = seg.HourStartUtcMs;
                        pTarget.Value = byTarget.Key;
                        pStart.Value = seg.StartUtcMs;
                        pEnd.Value = seg.EndUtcMs;
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            // 3c. UPSERT 自动发现的实际出站节点
            if (batch.DiscoveredNodes.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = cmdTimeout;
                cmd.CommandText = """
                    INSERT INTO discovered_nodes (date_bj, node, provider, first_seen_utc_ms, last_seen_utc_ms)
                    VALUES ($date, $node, $provider, $first, $last)
                    ON CONFLICT (date_bj, node, provider)
                    DO UPDATE SET
                        first_seen_utc_ms = MIN(first_seen_utc_ms, excluded.first_seen_utc_ms),
                        last_seen_utc_ms = MAX(last_seen_utc_ms, excluded.last_seen_utc_ms);
                    """;

                var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
                var pNode = cmd.Parameters.Add("$node", SqliteType.Text);
                var pProvider = cmd.Parameters.Add("$provider", SqliteType.Text);
                var pFirst = cmd.Parameters.Add("$first", SqliteType.Integer);
                var pLast = cmd.Parameters.Add("$last", SqliteType.Integer);

                foreach (var (key, seen) in batch.DiscoveredNodes)
                {
                    pDate.Value = key.DateBj;
                    pNode.Value = key.Node;
                    pProvider.Value = key.Provider;
                    pFirst.Value = seen.FirstSeenUtcMs;
                    pLast.Value = seen.LastSeenUtcMs;
                    cmd.ExecuteNonQuery();
                }
            }

            // 4. 原子更新采集器状态与提交进度
            using (var stateCmd = conn.CreateCommand())
            {
                stateCmd.Transaction = tx;
                stateCmd.CommandTimeout = cmdTimeout;
                stateCmd.CommandText = """
                    UPDATE collector_state
                    SET last_batch_id = $batchId,
                        collector_status = $status,
                        active_pipe_name = $pipeName,
                        mihomo_version = $version,
                        current_run_start_utc_ms = $runStart,
                        last_committed_sample_utc_ms = COALESCE($lastSample, last_committed_sample_utc_ms),
                        last_heartbeat_utc_ms = $heartbeat,
                        last_stopped_utc_ms = COALESCE($lastStopped, last_stopped_utc_ms),
                        last_error_code = $errCode,
                        last_error_message = $errMsg,
                        last_error_utc_ms = $errUtc,
                        active_observed_targets_json = $obsTargets,
                        attribution_verified = COALESCE($attrVerified, attribution_verified),
                        attribution_reason_code = CASE WHEN $attrVerified IS NOT NULL THEN $attrCode ELSE attribution_reason_code END,
                        attribution_reason_text = CASE WHEN $attrVerified IS NOT NULL THEN $attrText ELSE attribution_reason_text END,
                        auto_observed_active = $autoObsActive
                    WHERE id = 1;
                    """;

                stateCmd.Parameters.AddWithValue("$batchId", batch.BatchId);
                stateCmd.Parameters.AddWithValue("$status", batch.CollectorStatus);
                stateCmd.Parameters.AddWithValue("$pipeName", (object?)batch.ActivePipeName ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$version", (object?)batch.MihomoVersion ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$runStart", (object?)batch.CurrentRunStartUtcMs ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$lastSample", (object?)batch.LastSampleUtcMs ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$heartbeat", heartbeatUtcMs);
                stateCmd.Parameters.AddWithValue("$lastStopped", (object?)batch.LastStoppedUtcMs ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$errCode", (object?)batch.LastErrorCode ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$errMsg", (object?)batch.LastErrorMessage ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$errUtc", (object?)batch.LastErrorUtcMs ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$obsTargets", (object?)batch.ActiveObservedTargetsJson ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$attrVerified", batch.AttributionVerified.HasValue ? (batch.AttributionVerified.Value ? 1 : 0) : DBNull.Value);
                stateCmd.Parameters.AddWithValue("$attrCode", (object?)batch.AttributionReasonCode ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$attrText", (object?)batch.AttributionReasonText ?? DBNull.Value);
                stateCmd.Parameters.AddWithValue("$autoObsActive", batch.AutoObservedActive ? 1 : 0);
                stateCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            if (singleAttemptTimeoutSeconds.HasValue)
            {
                try
                {
                    ExecuteNonQuery(conn, null, $"PRAGMA busy_timeout = {DefaultTimeoutSeconds * 1000};", DefaultTimeoutSeconds);
                }
                catch
                {
                    // 忽略恢复 PRAGMA 失败
                }
            }
        }
    }

    public int CleanupExpiredData(DateOnly todayBj, int retentionDays)
    {
        var conn = RequireConnection();
        var keepStartDateBj = todayBj.AddDays(-(retentionDays - 1));
        var cutoffUtcMs = BeijingTime.GetDayStartUtcMs(keepStartDateBj);
        var todayText = BeijingTime.FormatDate(todayBj);

        using var tx = conn.BeginTransaction();
        var deleted = 0;

        deleted += ExecuteDelete(conn, tx, "DELETE FROM hourly_proxy_usage WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM hourly_observed_node_usage WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM hourly_auto_observed_usage WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM hourly_diagnostics WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM coverage_segments WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM auto_observed_coverage_segments WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);
        deleted += ExecuteDelete(conn, tx, "DELETE FROM observed_coverage_segments WHERE hour_start_utc_ms < $cutoff;", cutoffUtcMs);

        using (var delDiscovered = conn.CreateCommand())
        {
            delDiscovered.Transaction = tx;
            delDiscovered.CommandTimeout = DefaultTimeoutSeconds;
            delDiscovered.CommandText = "DELETE FROM discovered_nodes WHERE date_bj < $keepDate;";
            delDiscovered.Parameters.AddWithValue("$keepDate", BeijingTime.FormatDate(keepStartDateBj));
            deleted += delDiscovered.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandTimeout = DefaultTimeoutSeconds;
            cmd.CommandText = "UPDATE collector_state SET last_retention_cleanup_date_bj = $today WHERE id = 1;";
            cmd.Parameters.AddWithValue("$today", todayText);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return deleted;
    }

    public void CloseAndClearPool()
    {
        if (_connection is not null)
        {
            var conn = _connection;
            _connection = null;
            try
            {
                conn.Close();
            }
            catch
            {
                // 忽略关闭异常
            }
            try
            {
                SqliteConnection.ClearPool(conn);
            }
            catch
            {
                // 忽略清池异常
            }
            conn.Dispose();
        }
    }

    public void Dispose() => CloseAndClearPool();

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("SqliteLedgerStore 尚未初始化。");

    private static void ExecuteNonQuery(SqliteConnection conn, SqliteTransaction? tx, string sql, int timeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int ExecuteDelete(SqliteConnection conn, SqliteTransaction tx, string sql, long cutoffUtcMs)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DefaultTimeoutSeconds;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtcMs);
        return cmd.ExecuteNonQuery();
    }

    private static void EnsureProxyUsageHasProviderColumn(SqliteConnection conn, SqliteTransaction tx)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandTimeout = DefaultTimeoutSeconds;
            check.CommandText = "PRAGMA table_info(hourly_proxy_usage);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "provider", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        // 旧版库迁移：重建带 provider 主键维度的 hourly_proxy_usage 并保留已有汇总行
        ExecuteNonQuery(conn, tx, """
            CREATE TABLE hourly_proxy_usage_v2 (
                hour_start_utc_ms INTEGER NOT NULL,
                app TEXT NOT NULL,
                domain TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                upload_bytes INTEGER NOT NULL DEFAULT 0 CHECK (upload_bytes >= 0),
                download_bytes INTEGER NOT NULL DEFAULT 0 CHECK (download_bytes >= 0),
                PRIMARY KEY (hour_start_utc_ms, app, domain, node, provider)
            ) WITHOUT ROWID;

            INSERT INTO hourly_proxy_usage_v2 (hour_start_utc_ms, app, domain, node, provider, upload_bytes, download_bytes)
            SELECT hour_start_utc_ms, app, domain, node, '', upload_bytes, download_bytes
            FROM hourly_proxy_usage;

            DROP TABLE hourly_proxy_usage;
            ALTER TABLE hourly_proxy_usage_v2 RENAME TO hourly_proxy_usage;

            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_node
                ON hourly_proxy_usage (hour_start_utc_ms, node);
            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_app
                ON hourly_proxy_usage (hour_start_utc_ms, app);
            CREATE INDEX IF NOT EXISTS idx_hourly_usage_hour_domain
                ON hourly_proxy_usage (hour_start_utc_ms, domain);
            """, DefaultTimeoutSeconds);
    }

    private static void EnsureAutoObservedUsageHasVerifiedColumn(SqliteConnection conn, SqliteTransaction tx)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandTimeout = DefaultTimeoutSeconds;
            check.CommandText = "PRAGMA table_info(hourly_auto_observed_usage);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "verified", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        // 旧版库迁移：为 hourly_auto_observed_usage 补充 verified 主键列，历史行默认保守标为 0（未确认）
        ExecuteNonQuery(conn, tx, """
            CREATE TABLE hourly_auto_observed_usage_v2 (
                hour_start_utc_ms INTEGER NOT NULL,
                app TEXT NOT NULL,
                domain TEXT NOT NULL,
                node TEXT NOT NULL,
                provider TEXT NOT NULL DEFAULT '',
                verified INTEGER NOT NULL DEFAULT 0,
                upload_bytes INTEGER NOT NULL DEFAULT 0 CHECK (upload_bytes >= 0),
                download_bytes INTEGER NOT NULL DEFAULT 0 CHECK (download_bytes >= 0),
                PRIMARY KEY (hour_start_utc_ms, app, domain, node, provider, verified)
            ) WITHOUT ROWID;

            INSERT INTO hourly_auto_observed_usage_v2 (hour_start_utc_ms, app, domain, node, provider, verified, upload_bytes, download_bytes)
            SELECT hour_start_utc_ms, app, domain, node, provider, 0, upload_bytes, download_bytes
            FROM hourly_auto_observed_usage;

            DROP TABLE hourly_auto_observed_usage;
            ALTER TABLE hourly_auto_observed_usage_v2 RENAME TO hourly_auto_observed_usage;

            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_node
                ON hourly_auto_observed_usage (hour_start_utc_ms, node);
            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_app
                ON hourly_auto_observed_usage (hour_start_utc_ms, app);
            CREATE INDEX IF NOT EXISTS idx_auto_observed_usage_hour_domain
                ON hourly_auto_observed_usage (hour_start_utc_ms, domain);
            """, DefaultTimeoutSeconds);
    }

    private static void EnsureColumnExists(SqliteConnection conn, SqliteTransaction tx, string tableName, string columnName, string columnDef)
    {
        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandTimeout = DefaultTimeoutSeconds;
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        ExecuteNonQuery(conn, tx, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};", DefaultTimeoutSeconds);
    }
}
