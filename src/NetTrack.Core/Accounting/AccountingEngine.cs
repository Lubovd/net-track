using NetTrack.Core.Abstractions;
using NetTrack.Core.Logging;
using NetTrack.Core.Mihomo;

namespace NetTrack.Core.Accounting;

public readonly record struct ProxyUsageKey(long HourStartUtcMs, string App, string Domain, string Node, string Provider = "");

public readonly record struct ObservedNodeUsageKey(long HourStartUtcMs, string App, string Domain, string Node, string Provider, bool Verified = false);

public readonly record struct ObservedCoverageSegment(long HourStartUtcMs, string TargetKey, long StartUtcMs, long EndUtcMs);

public readonly record struct DiscoveredNodeKey(string DateBj, string Node, string Provider);

public readonly record struct BytePair(long UploadBytes, long DownloadBytes)
{
    public BytePair Add(long upload, long download) => new(UploadBytes + upload, DownloadBytes + download);
}

public readonly record struct HourlyDiagnosticDelta(
    long HourStartUtcMs,
    long SampleCount,
    long UnconfirmedUploadBytes,
    long UnconfirmedDownloadBytes,
    long ReconcileDiffUploadBytes,
    long ReconcileDiffDownloadBytes,
    long ClockAnomalyCount);

public sealed class AccountingFrameResult
{
    public bool IsBaselineOnly { get; init; }
    public bool IsClockAnomaly { get; init; }
    public bool IsGapBoundary { get; init; }
    public long SampleReceiveUtcMs { get; init; }
    public long CurrentRunStartUtcMs { get; init; }
    public IReadOnlyDictionary<ProxyUsageKey, BytePair> ProxyDeltas { get; init; } = new Dictionary<ProxyUsageKey, BytePair>();
    public IReadOnlyDictionary<ObservedNodeUsageKey, BytePair> ObservedSelectedNodeDeltas { get; init; } = new Dictionary<ObservedNodeUsageKey, BytePair>();
    public IReadOnlyDictionary<ObservedNodeUsageKey, BytePair> AutoObservedNodeDeltas { get; init; } = new Dictionary<ObservedNodeUsageKey, BytePair>();
    public HourlyDiagnosticDelta? DiagnosticDelta { get; init; }
    public IReadOnlyList<CoverageSegment> CoverageSegments { get; init; } = Array.Empty<CoverageSegment>();
    public IReadOnlyList<ObservedCoverageSegment> ObservedCoverageSegments { get; init; } = Array.Empty<ObservedCoverageSegment>();
    public IReadOnlyList<DiscoveredNodeKey> DiscoveredNodes { get; init; } = Array.Empty<DiscoveredNodeKey>();
    public bool AttributionVerified { get; init; }
    public string? AttributionReasonCode { get; init; }
    public string? AttributionReasonText { get; init; }
}

/// <summary>
/// 记账核心引擎：负责夹逼节点核对、连接路径锁定、双时钟连续性、增量计算、全局对账与跨小时覆盖拆分。
/// </summary>
public sealed class AccountingEngine
{
    private const double ClockDriftToleranceMs = 1000.0;

    private readonly IClock _clock;
    private readonly ILogSink _logger;
    private readonly double _gapThresholdMs;
    private readonly IReadOnlyList<Configuration.SelectedNodeTarget> _selectedTargets;

    private ProxyCatalog? _catalogBefore;
    private long _currentGenerationSinceUtcMs;

    private SampleAnchor? _lastAnchor;
    private long? _currentRunStartUtcMs;
    private readonly Dictionary<string, TrackedConnection> _activeConnections = new(StringComparer.Ordinal);

    public AccountingEngine(
        int gapThresholdSeconds,
        IClock? clock = null,
        ILogSink? logger = null,
        IEnumerable<string>? selectedNodes = null)
    {
        _gapThresholdMs = gapThresholdSeconds * 1000.0;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogSink.Instance;
        _selectedTargets = Configuration.SelectedNodeTarget.ParseAll(
            Configuration.SettingsLoader.NormalizeSelectedNodes(selectedNodes));
    }

    public long? CurrentRunStartUtcMs => _currentRunStartUtcMs;
    public bool IsSessionInitialized => _catalogBefore is not null;

    /// <summary>
    /// 首次连接建立会话时初始化首份节点表基准，并清空基线。
    /// </summary>
    public void InitializeSession(ProxyCatalog initialCatalog)
    {
        _catalogBefore = initialCatalog;
        _currentGenerationSinceUtcMs = initialCatalog.FetchedAtUtcMs;
        ForceResetBaseline();
    }

    /// <summary>
    /// 管道短暂失联后重连时调用：不主动清空已确认连接与锚点，交由下一帧按内核身份、间隔与计数连续性判定。
    /// </summary>
    public void OnReconnected(ProxyCatalog refreshedCatalog)
    {
        if (_catalogBefore is null)
        {
            InitializeSession(refreshedCatalog);
            return;
        }

        if (!_catalogBefore.HasSameDefinitions(refreshedCatalog))
        {
            _currentGenerationSinceUtcMs = refreshedCatalog.FetchedAtUtcMs;
            _logger.Info($"重连后检测到节点定义变更，已推进配置世代起点（成员数={refreshedCatalog.Members.Count}）。");
        }

        _catalogBefore = refreshedCatalog;
    }

    /// <summary>
    /// 外部定期刷新节点表时同步更新锚点；若发现配置变更则推进当前配置世代起点，防止后续误判。
    /// </summary>
    public void OnPeriodicCatalogRefreshed(ProxyCatalog refreshedCatalog)
    {
        if (_catalogBefore is not null && !_catalogBefore.HasSameDefinitions(refreshedCatalog))
        {
            _currentGenerationSinceUtcMs = refreshedCatalog.FetchedAtUtcMs;
            _logger.Info($"检测到周期节点表变更，已推进配置世代起点（成员数={refreshedCatalog.Members.Count}）。");
        }

        _catalogBefore = refreshedCatalog;
    }

    /// <summary>
    /// 在写入故障恢复、停采重启或断线重连时强制重建基线。
    /// </summary>
    public void ForceResetBaseline()
    {
        _lastAnchor = null;
        _currentRunStartUtcMs = null;
        _activeConnections.Clear();
    }

    /// <summary>
    /// 判断本帧快照是否包含尚未拥有锁定结果（或待核对）的连接，以决定是否合并触发一次 GET /proxies。
    /// </summary>
    public bool NeedsCatalogVerification(MihomoConnectionsSnapshot snapshot)
    {
        foreach (var conn in snapshot.Connections)
        {
            if (!_activeConnections.TryGetValue(conn.Id, out var tracked))
            {
                return true;
            }

            if (tracked.VerificationState == ConnectionVerificationState.PendingCatalogCheck)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 处理单帧 /connections 快照与（可选的）后置夹逼 /proxies 结果，产出本轮增量与覆盖段。
    /// </summary>
    public AccountingFrameResult ProcessSnapshot(
        MihomoConnectionsSnapshot snapshot,
        ProxyCatalog? catalogAfter,
        bool catalogFetchFailed)
    {
        if (_catalogBefore is null)
        {
            throw new InvalidOperationException("AccountingEngine 必须先调用 InitializeSession 初始化节点表。");
        }

        // 1. 判定连续性与是否需要重建基线
        var isBaselineOnly = false;
        var isClockAnomaly = false;
        var isGapBoundary = false;
        var kernelChangedOrReset = false;

        if (_lastAnchor is null)
        {
            isBaselineOnly = true;
        }
        else if (snapshot.ServerIdentity is null || !snapshot.ServerIdentity.IsConfirmedSameKernel(_lastAnchor.ServerIdentity))
        {
            isBaselineOnly = true;
            isGapBoundary = true;
            kernelChangedOrReset = true;
            _logger.Info("管道服务端身份变化或无法确认同一内核，清空旧连接状态并重建基线。");
        }
        else if (snapshot.UploadTotal < _lastAnchor.GlobalUploadTotal ||
                 snapshot.DownloadTotal < _lastAnchor.GlobalDownloadTotal)
        {
            isBaselineOnly = true;
            isGapBoundary = true;
            kernelChangedOrReset = true;
            _logger.Warn("检测到 Mihomo 全局字节计数回落，清空旧连接状态并重建基线。");
        }
        else
        {
            var deltaWallMs = (double)(snapshot.ReceiveUtcMs - _lastAnchor.ReceiveUtcMs);
            var deltaMonoMs = _clock.GetElapsedTime(_lastAnchor.ReceiveMonoTicks, snapshot.ReceiveMonoTicks).TotalMilliseconds;

            if (deltaWallMs < 0 || Math.Abs(deltaMonoMs - deltaWallMs) > ClockDriftToleranceMs)
            {
                isBaselineOnly = true;
                isClockAnomaly = true;
                isGapBoundary = true;
                _logger.Warn($"检测到时钟异常（壁钟间隔={deltaWallMs:F0}ms，单调间隔={deltaMonoMs:F0}ms），切断覆盖并重建基线。");
            }
            else if (deltaMonoMs > _gapThresholdMs || deltaWallMs > _gapThresholdMs)
            {
                isBaselineOnly = true;
                isGapBoundary = true;
                _logger.Info($"采样间隔超过阈值（壁钟={deltaWallMs:F0}ms，单调={deltaMonoMs:F0}ms），标记空档并重建基线。");
            }
        }

        if (kernelChangedOrReset)
        {
            _activeConnections.Clear();
            if (!catalogFetchFailed && catalogAfter is not null)
            {
                _currentGenerationSinceUtcMs = catalogAfter.FetchedAtUtcMs;
            }
        }

        // 2. 分析前后夹逼节点表状态
        var catalogBeforeSnapshot = _catalogBefore;
        var generationSinceBefore = _currentGenerationSinceUtcMs;
        var reloadDetectedInThisFrame = false;
        if (!catalogFetchFailed && catalogAfter is not null)
        {
            if (!catalogBeforeSnapshot.HasSameDefinitions(catalogAfter))
            {
                reloadDetectedInThisFrame = true;
                _currentGenerationSinceUtcMs = catalogAfter.FetchedAtUtcMs;
                _logger.Info($"夹逼核对检测到配置热重载（成员数={catalogAfter.Members.Count}），跨重载未确认连接维持走向未确认。");
            }

            _catalogBefore = catalogAfter;
        }

        if (isBaselineOnly)
        {
            _currentRunStartUtcMs = snapshot.ReceiveUtcMs;
        }

        var hourStartUtcMs = BeijingTime.GetHourStartUtcMs(snapshot.ReceiveUtcMs);
        var proxyDeltas = new Dictionary<ProxyUsageKey, BytePair>();
        var observedDeltas = new Dictionary<ObservedNodeUsageKey, BytePair>();
        var autoObservedDeltas = new Dictionary<ObservedNodeUsageKey, BytePair>();
        long directUp = 0, directDown = 0;
        long rejectUp = 0, rejectDown = 0;
        long unconfirmedUp = 0, unconfirmedDown = 0;
        long proxyUpTotal = 0, proxyDownTotal = 0;

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var conn in snapshot.Connections)
        {
            seenIds.Add(conn.Id);

            if (_activeConnections.TryGetValue(conn.Id, out var tracked))
            {
                // 若此前处于 PendingCatalogCheck，尝试用同世代夹逼证据改判（仅影响改判后的后续增量）
                var promotedThisFrame = false;
                if (tracked.VerificationState == ConnectionVerificationState.PendingCatalogCheck)
                {
                    if (!catalogFetchFailed && catalogAfter is not null)
                    {
                        var anchor = tracked.PendingAnchorCatalog!;
                        if (!reloadDetectedInThisFrame &&
                            catalogAfter.IsVerifiedForKernel(snapshot.ServerIdentity) &&
                            anchor.HasSameDefinitions(catalogAfter) &&
                            tracked.StartUtcMs.HasValue &&
                            tracked.StartUtcMs.Value >= tracked.PendingGenerationSinceUtcMs &&
                            tracked.StartUtcMs.Value >= _currentGenerationSinceUtcMs)
                        {
                            var cls = ProxyClassifier.Classify(conn.Chains, catalogAfter);
                            if (cls.Kind == ConnectionPathKind.Unconfirmed)
                            {
                                tracked.VerificationState = ConnectionVerificationState.PermanentUnconfirmed;
                                tracked.Classification = cls;
                            }
                            else
                            {
                                tracked.VerificationState = ConnectionVerificationState.Confirmed;
                                tracked.Classification = cls;
                                promotedThisFrame = true;
                            }
                            tracked.PendingAnchorCatalog = null;
                        }
                        else
                        {
                            // 故障期间发生配置变更或缺乏时间夹逼证据，永久维持未确认
                            tracked.VerificationState = ConnectionVerificationState.PermanentUnconfirmed;
                            tracked.PendingAnchorCatalog = null;
                        }
                    }
                }

                if (!isBaselineOnly)
                {
                    var du = conn.Upload - tracked.LastUpload;
                    var dd = conn.Download - tracked.LastDownload;
                    if (du < 0 || dd < 0)
                    {
                        // 单条连接计数异常回退：仅重建该连接基线
                        _logger.Warn("检测到单条连接字节计数回退，已重建该连接基线。");
                    }
                    else if (du > 0 || dd > 0)
                    {
                        // 本帧刚刚由 PendingCatalogCheck 改判为已确认时，本帧增量发生于确认之前，归入未确认；确认后的后续帧才计入新归类
                        var effectiveKind = promotedThisFrame
                            ? ConnectionPathKind.Unconfirmed
                            : tracked.Classification.Kind;

                        var provider = conn.ProviderChains is { Count: > 0 } ? conn.ProviderChains[0] : string.Empty;
                        AccumulateDelta(
                            effectiveKind,
                            tracked.Classification.NodeName,
                            provider,
                            tracked.App,
                            tracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            proxyDeltas,
                            ref proxyUpTotal,
                            ref proxyDownTotal,
                            ref directUp,
                            ref directDown,
                            ref rejectUp,
                            ref rejectDown,
                            ref unconfirmedUp,
                            ref unconfirmedDown);

                        // 观察入账传入实际已知分类（tracked.Classification.Kind），确保核验恢复并确认为 Direct/Reject 的当帧也不生成观察记录
                        TryAccumulateObservedSelectedNode(
                            tracked.Classification.Kind,
                            conn,
                            tracked.App,
                            tracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            observedDeltas);

                        TryAccumulateAutoObservedNode(
                            tracked.Classification.Kind,
                            verified: effectiveKind == ConnectionPathKind.Proxy,
                            conn,
                            tracked.App,
                            tracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            autoObservedDeltas);
                    }
                }

                tracked.LastUpload = conn.Upload;
                tracked.LastDownload = conn.Download;
            }
            else
            {
                // 首次见到该连接：先执行归一化与夹逼归类
                var app = MetadataNormalizer.NormalizeApp(conn.ProcessPath, conn.Process);
                var domain = MetadataNormalizer.NormalizeDomain(conn.Host, conn.SniffHost);
                var hasValidStart = MetadataNormalizer.TryParseStartWithTimezone(conn.StartRaw, out var startUtcMs);

                var newTracked = CreateTrackedConnection(
                    conn,
                    app,
                    domain,
                    hasValidStart ? startUtcMs : null,
                    snapshot.ReceiveUtcMs,
                    snapshot.ServerIdentity,
                    catalogBeforeSnapshot,
                    generationSinceBefore,
                    catalogAfter,
                    catalogFetchFailed,
                    reloadDetectedInThisFrame);

                _activeConnections[conn.Id] = newTracked;

                // 仅当本帧为连续采样且 start 在 [prev.RequestStartUtcMs, snapshot.ReceiveUtcMs] 内时，从零起算
                if (!isBaselineOnly &&
                    _lastAnchor is not null &&
                    hasValidStart &&
                    startUtcMs >= _lastAnchor.RequestStartUtcMs &&
                    startUtcMs <= snapshot.ReceiveUtcMs)
                {
                    var du = Math.Max(0L, conn.Upload);
                    var dd = Math.Max(0L, conn.Download);
                    if (du > 0 || dd > 0)
                    {
                        var provider = conn.ProviderChains is { Count: > 0 } ? conn.ProviderChains[0] : string.Empty;
                        AccumulateDelta(
                            newTracked.Classification.Kind,
                            newTracked.Classification.NodeName,
                            provider,
                            newTracked.App,
                            newTracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            proxyDeltas,
                            ref proxyUpTotal,
                            ref proxyDownTotal,
                            ref directUp,
                            ref directDown,
                            ref rejectUp,
                            ref rejectDown,
                            ref unconfirmedUp,
                            ref unconfirmedDown);

                        TryAccumulateObservedSelectedNode(
                            newTracked.Classification.Kind,
                            conn,
                            newTracked.App,
                            newTracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            observedDeltas);

                        TryAccumulateAutoObservedNode(
                            newTracked.Classification.Kind,
                            verified: newTracked.Classification.Kind == ConnectionPathKind.Proxy,
                            conn,
                            newTracked.App,
                            newTracked.Domain,
                            hourStartUtcMs,
                            du,
                            dd,
                            autoObservedDeltas);
                    }
                }
            }
        }

        // 清理已关闭连接，防止内存泄漏
        if (_activeConnections.Count > seenIds.Count)
        {
            var toRemove = new List<string>();
            foreach (var id in _activeConnections.Keys)
            {
                if (!seenIds.Contains(id))
                {
                    toRemove.Add(id);
                }
            }
            foreach (var id in toRemove)
            {
                _activeConnections.Remove(id);
            }
        }

        // 3. 自动发现本帧所有非内置、非直连/拒绝的实际出站节点（供桌面端实时筛选下拉使用，无需手填名单）
        var dateBj = BeijingTime.FormatDate(BeijingTime.GetBeijingDate(snapshot.ReceiveUtcMs));
        var discoveredSet = new HashSet<DiscoveredNodeKey>();
        foreach (var conn in snapshot.Connections)
        {
            if (conn.Chains.Count == 0)
            {
                continue;
            }

            var node = conn.Chains[0];
            if (Configuration.SettingsLoader.IsBuiltInNonProxy(node))
            {
                continue;
            }

            if (_activeConnections.TryGetValue(conn.Id, out var tr) &&
                tr.Classification.Kind is ConnectionPathKind.Direct or ConnectionPathKind.Reject)
            {
                continue;
            }

            var provider = conn.ProviderChains is { Count: > 0 } ? conn.ProviderChains[0] : string.Empty;
            discoveredSet.Add(new DiscoveredNodeKey(dateBj, node, provider));
        }

        // 4. 评估当前帧内核配置核验与归因状态（本帧配置读取失败时严禁退回上一份已核验节点表）
        bool attrVerified;
        string? attrReasonCode;
        string? attrReasonText;
        if (catalogFetchFailed)
        {
            attrVerified = false;
            attrReasonCode = "config_read_failed";
            attrReasonText = "本次读取本机 Clash 运行时配置失败，新连接已保守归入走向未确认";
        }
        else
        {
            var evalCatalog = catalogAfter ?? _catalogBefore;
            (attrVerified, attrReasonCode, attrReasonText) = evalCatalog is not null
                ? evalCatalog.EvaluateKernelVerification(snapshot.ServerIdentity)
                : (false, "config_missing", "未加载本机 Clash 节点定义");
        }

        // 5. 计算全局对账与覆盖段（区分通用管道覆盖与已启用观察目标的独立覆盖）
        HourlyDiagnosticDelta? diagnosticDelta = null;
        IReadOnlyList<CoverageSegment> coverageSegments = Array.Empty<CoverageSegment>();
        IReadOnlyList<ObservedCoverageSegment> observedCoverageSegments = Array.Empty<ObservedCoverageSegment>();

        if (!isBaselineOnly && _lastAnchor is not null)
        {
            var globalUpDelta = snapshot.UploadTotal - _lastAnchor.GlobalUploadTotal;
            var globalDownDelta = snapshot.DownloadTotal - _lastAnchor.GlobalDownloadTotal;

            var visibleUpDelta = proxyUpTotal + directUp + rejectUp + unconfirmedUp;
            var visibleDownDelta = proxyDownTotal + directDown + rejectDown + unconfirmedDown;

            var reconcileUp = globalUpDelta - visibleUpDelta;
            var reconcileDown = globalDownDelta - visibleDownDelta;

            diagnosticDelta = new HourlyDiagnosticDelta(
                hourStartUtcMs,
                SampleCount: 1,
                UnconfirmedUploadBytes: unconfirmedUp,
                UnconfirmedDownloadBytes: unconfirmedDown,
                ReconcileDiffUploadBytes: reconcileUp,
                ReconcileDiffDownloadBytes: reconcileDown,
                ClockAnomalyCount: 0);

            coverageSegments = CoverageCalculator.SplitAcrossBeijingHours(_lastAnchor.ReceiveUtcMs, snapshot.ReceiveUtcMs);

            if (_selectedTargets.Count > 0 && coverageSegments.Count > 0)
            {
                var obsSegs = new List<ObservedCoverageSegment>(coverageSegments.Count * _selectedTargets.Count);
                foreach (var target in _selectedTargets)
                {
                    foreach (var seg in coverageSegments)
                    {
                        obsSegs.Add(new ObservedCoverageSegment(seg.HourStartUtcMs, target.CanonicalKey, seg.StartUtcMs, seg.EndUtcMs));
                    }
                }
                observedCoverageSegments = obsSegs;
            }
        }
        else if (isClockAnomaly)
        {
            diagnosticDelta = new HourlyDiagnosticDelta(
                hourStartUtcMs,
                SampleCount: 0,
                UnconfirmedUploadBytes: 0,
                UnconfirmedDownloadBytes: 0,
                ReconcileDiffUploadBytes: 0,
                ReconcileDiffDownloadBytes: 0,
                ClockAnomalyCount: 1);
        }

        // 更新锚点
        _lastAnchor = new SampleAnchor(
            snapshot.RequestStartUtcMs,
            snapshot.ReceiveUtcMs,
            snapshot.ReceiveMonoTicks,
            snapshot.ServerIdentity,
            snapshot.UploadTotal,
            snapshot.DownloadTotal);

        return new AccountingFrameResult
        {
            IsBaselineOnly = isBaselineOnly,
            IsClockAnomaly = isClockAnomaly,
            IsGapBoundary = isGapBoundary,
            SampleReceiveUtcMs = snapshot.ReceiveUtcMs,
            CurrentRunStartUtcMs = _currentRunStartUtcMs ?? snapshot.ReceiveUtcMs,
            ProxyDeltas = proxyDeltas,
            ObservedSelectedNodeDeltas = observedDeltas,
            AutoObservedNodeDeltas = autoObservedDeltas,
            DiagnosticDelta = diagnosticDelta,
            CoverageSegments = coverageSegments,
            ObservedCoverageSegments = observedCoverageSegments,
            DiscoveredNodes = discoveredSet.Count > 0 ? discoveredSet.ToList() : Array.Empty<DiscoveredNodeKey>(),
            AttributionVerified = attrVerified,
            AttributionReasonCode = attrReasonCode,
            AttributionReasonText = attrReasonText
        };
    }

    /// <summary>
    /// 独立记录用户选定付费节点的观察量：
    /// 1. 已明确判定为 Direct 或 Reject 的连接严禁生成观察记录（遵守不落盘直连流量约定）；
    /// 2. 支持按「Provider名::节点名」精确区分不同 Provider 下的同名节点。
    /// </summary>
    private void TryAccumulateObservedSelectedNode(
        ConnectionPathKind effectiveKind,
        MihomoConnectionRecord conn,
        string app,
        string domain,
        long hourStartUtcMs,
        long du,
        long dd,
        Dictionary<ObservedNodeUsageKey, BytePair> observedDeltas)
    {
        if (effectiveKind is ConnectionPathKind.Direct or ConnectionPathKind.Reject)
        {
            return;
        }

        if (_selectedTargets.Count == 0 || conn.Chains.Count == 0)
        {
            return;
        }

        var node = conn.Chains[0];
        var provider = conn.ProviderChains is { Count: > 0 } ? conn.ProviderChains[0] : string.Empty;
        if (!Configuration.SelectedNodeTarget.MatchesAny(_selectedTargets, node, provider))
        {
            return;
        }

        var key = new ObservedNodeUsageKey(hourStartUtcMs, app, domain, node, provider);
        observedDeltas[key] = observedDeltas.TryGetValue(key, out var existing)
            ? existing.Add(du, dd)
            : new BytePair(du, dd);
    }

    /// <summary>
    /// 自动节点观察记账（方案 B 主视图）：无需手填名单或重启采集，自动记录所有非内置、非直连/拒绝的实际出站流量。
    /// 已知 Direct/Reject 及 DIRECT/REJECT 等内置策略严格排除；未核验连接会记录在此观察表并在主界面明示为未确认。
    /// </summary>
    private static void TryAccumulateAutoObservedNode(
        ConnectionPathKind knownKind,
        bool verified,
        MihomoConnectionRecord conn,
        string app,
        string domain,
        long hourStartUtcMs,
        long du,
        long dd,
        Dictionary<ObservedNodeUsageKey, BytePair> autoObservedDeltas)
    {
        if (knownKind is ConnectionPathKind.Direct or ConnectionPathKind.Reject || conn.Chains.Count == 0)
        {
            return;
        }

        var node = conn.Chains[0];
        if (Configuration.SettingsLoader.IsBuiltInNonProxy(node))
        {
            return;
        }

        var provider = conn.ProviderChains is { Count: > 0 } ? conn.ProviderChains[0] : string.Empty;
        var key = new ObservedNodeUsageKey(hourStartUtcMs, app, domain, node, provider, verified);
        autoObservedDeltas[key] = autoObservedDeltas.TryGetValue(key, out var existing)
            ? existing.Add(du, dd)
            : new BytePair(du, dd);
    }

    private TrackedConnection CreateTrackedConnection(
        MihomoConnectionRecord conn,
        string app,
        string domain,
        long? startUtcMs,
        long snapshotReceiveUtcMs,
        PipeServerIdentity? serverIdentity,
        ProxyCatalog catalogBefore,
        long generationSinceBefore,
        ProxyCatalog? catalogAfter,
        bool catalogFetchFailed,
        bool reloadDetectedInThisFrame)
    {
        // 情况 C：节点表读取失败，无法核对 -> 暂标 PendingCatalogCheck（若 start 本身早于当前配置世代起点，仍无验证可能）
        if (catalogFetchFailed || catalogAfter is null)
        {
            var canEverVerify = startUtcMs.HasValue &&
                                startUtcMs.Value >= generationSinceBefore &&
                                startUtcMs.Value <= snapshotReceiveUtcMs;

            return new TrackedConnection(
                app,
                domain,
                startUtcMs,
                conn.Upload,
                conn.Download,
                new ConnectionClassification(ConnectionPathKind.Unconfirmed, null),
                canEverVerify ? ConnectionVerificationState.PendingCatalogCheck : ConnectionVerificationState.PermanentUnconfirmed,
                canEverVerify ? catalogBefore : null,
                generationSinceBefore);
        }

        // 情况 B：本帧检测到配置重载，或节点表缺乏与运行中内核已加载版本的关联证据 -> 维持 Unconfirmed
        if (reloadDetectedInThisFrame || !catalogAfter.IsVerifiedForKernel(serverIdentity))
        {
            return new TrackedConnection(
                app,
                domain,
                startUtcMs,
                conn.Upload,
                conn.Download,
                new ConnectionClassification(ConnectionPathKind.Unconfirmed, null),
                ConnectionVerificationState.PermanentUnconfirmed,
                null,
                _currentGenerationSinceUtcMs);
        }

        // 情况 A：前后两次节点表观测一致 -> 仍须严格校验连接建立时间被当前未变更配置世代夹逼覆盖
        // 启动前既有连接（start < _currentGenerationSinceUtcMs）或 start 缺失/晚于接收，无法证明建立时节点定义，维持 Unconfirmed
        var isBracketed = startUtcMs.HasValue &&
                          startUtcMs.Value >= generationSinceBefore &&
                          startUtcMs.Value <= snapshotReceiveUtcMs;

        if (!isBracketed)
        {
            return new TrackedConnection(
                app,
                domain,
                startUtcMs,
                conn.Upload,
                conn.Download,
                new ConnectionClassification(ConnectionPathKind.Unconfirmed, null),
                ConnectionVerificationState.PermanentUnconfirmed,
                null,
                generationSinceBefore);
        }

        var classification = ProxyClassifier.Classify(conn.Chains, catalogAfter);
        var state = classification.Kind == ConnectionPathKind.Unconfirmed
            ? ConnectionVerificationState.PermanentUnconfirmed
            : ConnectionVerificationState.Confirmed;

        return new TrackedConnection(
            app,
            domain,
            startUtcMs,
            conn.Upload,
            conn.Download,
            classification,
            state,
            null,
            generationSinceBefore);
    }

    private static void AccumulateDelta(
        ConnectionPathKind kind,
        string? nodeName,
        string provider,
        string app,
        string domain,
        long hourStartUtcMs,
        long du,
        long dd,
        Dictionary<ProxyUsageKey, BytePair> proxyDeltas,
        ref long proxyUpTotal,
        ref long proxyDownTotal,
        ref long directUp,
        ref long directDown,
        ref long rejectUp,
        ref long rejectDown,
        ref long unconfirmedUp,
        ref long unconfirmedDown)
    {
        switch (kind)
        {
            case ConnectionPathKind.Proxy:
                var key = new ProxyUsageKey(hourStartUtcMs, app, domain, nodeName!, provider);
                proxyDeltas[key] = proxyDeltas.TryGetValue(key, out var existing)
                    ? existing.Add(du, dd)
                    : new BytePair(du, dd);
                proxyUpTotal += du;
                proxyDownTotal += dd;
                break;

            case ConnectionPathKind.Direct:
                directUp += du;
                directDown += dd;
                break;

            case ConnectionPathKind.Reject:
                rejectUp += du;
                rejectDown += dd;
                break;

            default:
                unconfirmedUp += du;
                unconfirmedDown += dd;
                break;
        }
    }

    private sealed record SampleAnchor(
        long RequestStartUtcMs,
        long ReceiveUtcMs,
        long ReceiveMonoTicks,
        PipeServerIdentity? ServerIdentity,
        long GlobalUploadTotal,
        long GlobalDownloadTotal);

    private enum ConnectionVerificationState
    {
        Confirmed,
        PendingCatalogCheck,
        PermanentUnconfirmed
    }

    private sealed class TrackedConnection
    {
        public string App { get; }
        public string Domain { get; }
        public long? StartUtcMs { get; }
        public long LastUpload { get; set; }
        public long LastDownload { get; set; }
        public ConnectionClassification Classification { get; set; }
        public ConnectionVerificationState VerificationState { get; set; }
        public ProxyCatalog? PendingAnchorCatalog { get; set; }
        public long PendingGenerationSinceUtcMs { get; }

        public TrackedConnection(
            string app,
            string domain,
            long? startUtcMs,
            long lastUpload,
            long lastDownload,
            ConnectionClassification classification,
            ConnectionVerificationState verificationState,
            ProxyCatalog? pendingAnchorCatalog,
            long pendingGenerationSinceUtcMs)
        {
            App = app;
            Domain = domain;
            StartUtcMs = startUtcMs;
            LastUpload = lastUpload;
            LastDownload = lastDownload;
            Classification = classification;
            VerificationState = verificationState;
            PendingAnchorCatalog = pendingAnchorCatalog;
            PendingGenerationSinceUtcMs = pendingGenerationSinceUtcMs;
        }
    }
}
