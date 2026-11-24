using System.Globalization;
using System.Text.Json;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Export;
using NetTrack.Core.Locking;
using NetTrack.Core.Logging;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;

namespace NetTrack.Cli;

/// <summary>
/// 命令行依赖注入容器，方便测试替换时钟、管道、配置目录与数据库写入器。
/// </summary>
public sealed class CliDependencies
{
    public IClock Clock { get; init; } = SystemClock.Instance;
    public Func<IClashConfigLocator> CreateConfigLocator { get; init; } = () => new ClashVergeConfigLocator();
    public Func<IPipeEnumerator> CreatePipeEnumerator { get; init; } = () => new DefaultWindowsPipeEnumerator();
    public Func<IClock, IMihomoClientFactory> CreateClientFactory { get; init; } = clock => new MihomoPipeClientFactory(clock);
    public Func<IClock, IProxyCatalogSource> CreateCatalogSource { get; init; } = clock => new RuntimeYamlProxyCatalogSource(clock: clock);
    public Func<string, ILedgerWriter> CreateLedgerWriter { get; init; } = dbPath => new SqliteLedgerStore(dbPath);
    public Func<CollectorOptions, CollectorOptions>? OverrideOptions { get; init; }
}

/// <summary>
/// net-track CLI 应用主体：负责参数解析、stdout/stderr 分离、退出码与优雅停止。
/// </summary>
public sealed class CliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly CliDependencies _deps;

    public CliApp(TextWriter stdout, TextWriter stderr, CliDependencies? deps = null)
    {
        _stdout = stdout;
        _stderr = stderr;
        _deps = deps ?? new CliDependencies();
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(_stdout);
            return 0;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        // 解析内部测试注入目录参数（若提供）与剩余参数
        string? injectedDataDir = null;
        var filteredArgs = new List<string>(rest.Length);
        for (var i = 0; i < rest.Length; i++)
        {
            if (string.Equals(rest[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= rest.Length)
                {
                    _stderr.WriteLine("参数错误：--data-dir 缺少目录路径。");
                    return 2;
                }
                injectedDataDir = rest[++i];
            }
            else
            {
                filteredArgs.Add(rest[i]);
            }
        }

        // 支持环境变量测试注入（不作为公开用户配置）
        if (string.IsNullOrWhiteSpace(injectedDataDir))
        {
            var envDir = Environment.GetEnvironmentVariable("NETTRACK_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                injectedDataDir = envDir;
            }
        }

        var paths = new NetTrackPaths(injectedDataDir);

        return command switch
        {
            "collect" => await RunCollectAsync(paths, filteredArgs, cancellationToken).ConfigureAwait(false),
            "status" => RunStatus(paths, filteredArgs),
            "report" => RunReport(paths, filteredArgs),
            "export" => RunExport(paths, filteredArgs),
            _ => UnknownCommand(command)
        };
    }

    private int UnknownCommand(string command)
    {
        _stderr.WriteLine($"未知命令：{command}");
        PrintUsage(_stderr);
        return 2;
    }

    private async Task<int> RunCollectAsync(NetTrackPaths paths, List<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            _stderr.WriteLine($"参数错误：collect 不接受未知参数 {args[0]}。");
            return 2;
        }

        var (settings, settingsErr) = SettingsLoader.Load(paths.SettingsPath);
        if (settings is null)
        {
            _stderr.WriteLine($"配置错误：{settingsErr}");
            return 2;
        }

        var options = CollectorOptions.FromSettings(settings);
        if (_deps.OverrideOptions is not null)
        {
            options = _deps.OverrideOptions(options);
        }

        var lockResult = CollectorFileLock.TryAcquire(paths.LockFilePath, "cli");
        if (lockResult.Status == CollectorLockStatus.AlreadyHeld)
        {
            _stderr.WriteLine(lockResult.ErrorMessage ?? "已有采集实例正在运行。");
            return 3;
        }

        if (lockResult.Status != CollectorLockStatus.Acquired || lockResult.LockHandle is null)
        {
            _stderr.WriteLine(lockResult.ErrorMessage ?? "无法访问采集锁文件。");
            return 1;
        }

        paths.EnsureDirectoriesExist();
        var logger = new RollingFileLogger(paths.LogsDirectory, _deps.Clock);
        var discovery = new PipeDiscoveryService(
            _deps.CreateConfigLocator(),
            _deps.CreatePipeEnumerator(),
            _deps.CreateClientFactory(_deps.Clock),
            TimeSpan.FromSeconds(options.HttpTimeoutSeconds));

        using var ledger = _deps.CreateLedgerWriter(paths.DatabasePath);
        using var runner = new CollectorRunner(
            options,
            discovery,
            _deps.CreateCatalogSource(_deps.Clock),
            ledger,
            lockResult.LockHandle,
            _deps.Clock,
            logger);

        string? lastPrintedStatusLine = null;
        runner.StatusChanged += snapshot =>
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Message))
            {
                var line = $"[{snapshot.State}] {snapshot.Message}";
                if (!string.Equals(line, lastPrintedStatusLine, StringComparison.Ordinal))
                {
                    lastPrintedStatusLine = line;
                    _stderr.WriteLine(line);
                }
            }
        };

        try
        {
            runner.Start();
            _stderr.WriteLine("采集器已启动，按 Ctrl+C 停止并保存待写数据。");

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 收到 Ctrl+C 或外部取消
            }

            _stderr.WriteLine("正在停止采集并提交待写批次…");
            var stopResult = await runner.StopAsync(releaseLockOnFailure: true).ConfigureAwait(false);
            if (!stopResult.FlushSucceeded)
            {
                var committed = stopResult.LastCommittedSampleUtcMs.HasValue
                    ? BeijingTime.FormatIso(stopResult.LastCommittedSampleUtcMs.Value)
                    : "无";
                _stderr.WriteLine($"错误：末批数据未能写入账本（{stopResult.ErrorMessage}），最后已提交采样时间：{committed}。");
                return 1;
            }

            _stderr.WriteLine("采集已停止，待写数据已提交并释放锁。");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("CLI 采集运行异常。", ex);
            _stderr.WriteLine($"采集运行失败：{ex.Message}");
            return 1;
        }
    }

    private int RunStatus(NetTrackPaths paths, List<string> args)
    {
        var jsonOutput = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
            }
            else
            {
                _stderr.WriteLine($"参数错误：status 不接受未知参数 {arg}。");
                return 2;
            }
        }

        var (settings, settingsErr) = SettingsLoader.Load(paths.SettingsPath);
        if (settings is null)
        {
            _stderr.WriteLine($"配置错误：{settingsErr}");
            return 2;
        }

        var options = CollectorOptions.FromSettings(settings);
        if (_deps.OverrideOptions is not null)
        {
            options = _deps.OverrideOptions(options);
        }

        var query = new ReportQueryService(paths, _deps.Clock);
        var status = query.QueryStatus(options);

        if (jsonOutput)
        {
            _stdout.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return 0;
        }

        _stdout.WriteLine($"数据目录：{status.DataDir}");
        _stdout.WriteLine($"采集状态：{(status.CollectorActive ? "运行中" : status.CollectorStatus)}");
        _stdout.WriteLine($"当前管道：{status.ActivePipeName ?? "-"}");
        _stdout.WriteLine($"Mihomo 版本：{status.MihomoVersion ?? "-"}");
        _stdout.WriteLine($"账本已提交至：{status.LastCommittedSampleBj ?? "无采集数据"}");
        _stdout.WriteLine($"最近心跳：{status.LastHeartbeatBj ?? "-"}");
        _stdout.WriteLine($"账本健康：{(status.Ledger.Exists ? (status.Ledger.Healthy ? "正常" : "异常") : "尚未创建")}（保留 {status.Ledger.RetentionDays} 天，范围 {status.Ledger.EarliestDateBj ?? "-"} ~ {status.Ledger.LatestDateBj ?? "-"}）");
        if (status.LastError is not null)
        {
            _stdout.WriteLine($"最近错误：[{status.LastError.Code}] {status.LastError.Message} ({status.LastError.OccurredBj})");
        }

        return status.Ledger.Healthy ? 0 : 1;
    }

    private int RunReport(NetTrackPaths paths, List<string> args)
    {
        var todayBj = BeijingTime.GetBeijingDate(_deps.Clock.UtcNowMs);
        var dateBj = todayBj;
        var groupBy = ReportGroupBy.App;
        var sortBy = ReportSortBy.Upload;
        var scope = ReportScope.ConfirmedProxy;
        string? appFilter = null;
        string? domainFilter = null;
        string? nodeFilter = null;
        var jsonOutput = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
            }
            else if (string.Equals(arg, "--scope", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseScope(args[++i], out scope))
                {
                    _stderr.WriteLine("参数错误：--scope 仅支持 confirmed（严格代理账本）或 observed（所选节点观察量）。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--date", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseDateArg(args[++i], todayBj, out dateBj))
                {
                    _stderr.WriteLine("参数错误：--date 仅支持 today、yesterday 或 YYYY-MM-DD。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--by", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseGroupBy(args[++i], out groupBy))
                {
                    _stderr.WriteLine("参数错误：--by 仅支持 app、domain、node 或 hour。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--sort", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseSortBy(args[++i], out sortBy))
                {
                    _stderr.WriteLine("参数错误：--sort 仅支持 upload、download 或 total。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--app", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    _stderr.WriteLine("参数错误：--app 缺少取值。");
                    return 2;
                }
                appFilter = args[++i];
            }
            else if (string.Equals(arg, "--domain", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    _stderr.WriteLine("参数错误：--domain 缺少取值。");
                    return 2;
                }
                domainFilter = args[++i];
            }
            else if (string.Equals(arg, "--node", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    _stderr.WriteLine("参数错误：--node 缺少取值。");
                    return 2;
                }
                nodeFilter = args[++i];
            }
            else
            {
                _stderr.WriteLine($"参数错误：report 不接受未知参数 {arg}。");
                return 2;
            }
        }

        var (settings, settingsErr) = SettingsLoader.Load(paths.SettingsPath);
        if (settings is null)
        {
            _stderr.WriteLine($"配置错误：{settingsErr}");
            return 2;
        }

        var options = CollectorOptions.FromSettings(settings);
        if (_deps.OverrideOptions is not null)
        {
            options = _deps.OverrideOptions(options);
        }

        try
        {
            var query = new ReportQueryService(paths, _deps.Clock);
            var report = query.QueryDailyReport(
                new ReportQueryRequest(dateBj, groupBy, sortBy, new ReportFilters(appFilter, domainFilter, nodeFilter), scope),
                options);

            if (jsonOutput)
            {
                _stdout.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return 0;
            }

            PrintHumanReport(report);
            return 0;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"查询报表失败：{ex.Message}");
            return 1;
        }
    }

    private int RunExport(NetTrackPaths paths, List<string> args)
    {
        var todayBj = BeijingTime.GetBeijingDate(_deps.Clock.UtcNowMs);
        var dateBj = todayBj;
        var scope = ReportScope.ConfirmedProxy;
        string? appFilter = null;
        string? domainFilter = null;
        string? nodeFilter = null;
        string? outPath = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--date", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseDateArg(args[++i], todayBj, out dateBj))
                {
                    _stderr.WriteLine("参数错误：--date 仅支持 today、yesterday 或 YYYY-MM-DD。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--scope", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !TryParseScope(args[++i], out scope))
                {
                    _stderr.WriteLine("参数错误：--scope 仅支持 confirmed 或 observed。");
                    return 2;
                }
            }
            else if (string.Equals(arg, "--app", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) { _stderr.WriteLine("参数错误：--app 缺少取值。"); return 2; }
                appFilter = args[++i];
            }
            else if (string.Equals(arg, "--domain", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) { _stderr.WriteLine("参数错误：--domain 缺少取值。"); return 2; }
                domainFilter = args[++i];
            }
            else if (string.Equals(arg, "--node", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) { _stderr.WriteLine("参数错误：--node 缺少取值。"); return 2; }
                nodeFilter = args[++i];
            }
            else if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count) { _stderr.WriteLine("参数错误：--out 缺少文件路径。"); return 2; }
                outPath = args[++i];
            }
            else
            {
                _stderr.WriteLine($"参数错误：export 不接受未知参数 {arg}。");
                return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            _stderr.WriteLine("参数错误：export 必须指定 --out <csvPath>。");
            return 2;
        }

        var (settings, settingsErr) = SettingsLoader.Load(paths.SettingsPath);
        if (settings is null)
        {
            _stderr.WriteLine($"配置错误：{settingsErr}");
            return 2;
        }

        var options = CollectorOptions.FromSettings(settings);
        if (_deps.OverrideOptions is not null)
        {
            options = _deps.OverrideOptions(options);
        }

        try
        {
            CsvExporter.ValidateOutputPath(outPath, paths);
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"参数错误：{ex.Message}");
            return 2;
        }

        try
        {
            var query = new ReportQueryService(paths, _deps.Clock);
            var rows = query.QueryExportRows(dateBj, new ReportFilters(appFilter, domainFilter, nodeFilter), scope, options.SelectedNodes);
            CsvExporter.WriteToFile(outPath, rows, paths);
            _stdout.WriteLine($"已导出 {rows.Count} 条汇总记录至 {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"导出 CSV 失败：{ex.Message}");
            return 1;
        }
    }

    private void PrintHumanReport(DailyReportResult report)
    {
        var s = report.Summary;
        var scopeLabel = report.Scope switch
        {
            "selected_nodes_observed" => "所选节点观察量（独立于严格代理账本）",
            "auto_node_observed" => "自动节点观察（已排除直连/拒绝，未核验条目按未确认标示）",
            _ => "严格代理账本"
        };
        _stdout.WriteLine($"口径：{scopeLabel}");
        if (report.Scope == "selected_nodes_observed" && report.SelectedNodes.Count == 0)
        {
            _stdout.WriteLine("提示：当前所选付费节点名单为空（仅查询当前名单内节点，不包含未选历史节点）。");
        }
        _stdout.WriteLine($"日期（北京时间）：{report.DateBj}  |  账本已提交至：{report.CommittedUntilBj ?? "无采集数据"}");
        _stdout.WriteLine($"流量合计：上传 {DecimalByteFormatter.Format(s.UploadBytes)} | 下载 {DecimalByteFormatter.Format(s.DownloadBytes)} | 合计 {DecimalByteFormatter.Format(s.TotalBytes)}");
        _stdout.WriteLine($"覆盖状态：{DecimalByteFormatter.FormatCoverageStatusLabel(s.CoverageStatus)}（覆盖 {s.CoveredSeconds}s，空档 {s.GapSeconds}s，待更新 {s.PendingSeconds}s）");
        _stdout.WriteLine($"未知应用占比：{s.UnknownAppTotalRatio * 100:F2}%（{DecimalByteFormatter.Format(s.UnknownAppTotalBytes)}）");
        _stdout.WriteLine($"诊断：走向未确认（上传 {DecimalByteFormatter.Format(s.UnconfirmedUploadBytes)} / 下载 {DecimalByteFormatter.Format(s.UnconfirmedDownloadBytes)}），对账差额（上传 {DecimalByteFormatter.Format(s.ReconcileDiffUploadBytes)} / 下载 {DecimalByteFormatter.Format(s.ReconcileDiffDownloadBytes)}）{(s.ClockAnomalyCount > 0 ? $"，时钟异常 {s.ClockAnomalyCount} 次" : string.Empty)}");
        _stdout.WriteLine();

        if (report.GroupBy == "hour")
        {
            _stdout.WriteLine("小时  上传            下载            合计            状态（覆盖/空档）");
            foreach (var item in report.Items)
            {
                var statusLabel = DecimalByteFormatter.FormatCoverageStatusLabel(item.CoverageStatus ?? "no_data");
                var trafficText = item.CoverageStatus == "no_data"
                    ? "无采集数据"
                    : $"{DecimalByteFormatter.Format(item.UploadBytes),-14}  {DecimalByteFormatter.Format(item.DownloadBytes),-14}  {DecimalByteFormatter.Format(item.TotalBytes),-14}";
                _stdout.WriteLine($"{item.HourBj}    {trafficText}  {statusLabel}（{item.CoveredSeconds}s/{item.GapSeconds}s）");
            }
        }
        else
        {
            _stdout.WriteLine($"按 {report.GroupBy} 排行（排序：{report.SortBy}）：");
            if (report.Items.Count == 0)
            {
                _stdout.WriteLine(s.CoverageStatus == "no_data" ? "  无采集数据" : "  0（未观察到代理用量）");
            }
            else
            {
                foreach (var item in report.Items)
                {
                    _stdout.WriteLine($"  {item.Key,-32} 上传 {DecimalByteFormatter.Format(item.UploadBytes),10} | 下载 {DecimalByteFormatter.Format(item.DownloadBytes),10} | 合计 {DecimalByteFormatter.Format(item.TotalBytes),10}");
                }
            }
        }
    }

    private static bool TryParseDateArg(string raw, DateOnly todayBj, out DateOnly result)
    {
        if (string.Equals(raw, "today", StringComparison.OrdinalIgnoreCase))
        {
            result = todayBj;
            return true;
        }
        if (string.Equals(raw, "yesterday", StringComparison.OrdinalIgnoreCase))
        {
            result = todayBj.AddDays(-1);
            return true;
        }
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryParseGroupBy(string raw, out ReportGroupBy result)
    {
        switch (raw.ToLowerInvariant())
        {
            case "app": result = ReportGroupBy.App; return true;
            case "domain": result = ReportGroupBy.Domain; return true;
            case "node": result = ReportGroupBy.Node; return true;
            case "hour": result = ReportGroupBy.Hour; return true;
            default: result = ReportGroupBy.App; return false;
        }
    }

    private static bool TryParseSortBy(string raw, out ReportSortBy result)
    {
        switch (raw.ToLowerInvariant())
        {
            case "upload": result = ReportSortBy.Upload; return true;
            case "download": result = ReportSortBy.Download; return true;
            case "total": result = ReportSortBy.Total; return true;
            default: result = ReportSortBy.Upload; return false;
        }
    }

    private static bool TryParseScope(string raw, out ReportScope result)
    {
        switch (raw.ToLowerInvariant())
        {
            case "confirmed":
            case "confirmed_proxy":
                result = ReportScope.ConfirmedProxy;
                return true;
            case "observed":
            case "selected_nodes_observed":
                result = ReportScope.SelectedNodesObserved;
                return true;
            case "auto":
            case "auto_node_observed":
                result = ReportScope.AutoNodeObserved;
                return true;
            default:
                result = ReportScope.ConfirmedProxy;
                return false;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("用法：net-track <command> [options]");
        writer.WriteLine("命令：");
        writer.WriteLine("  collect                               前台启动采集，Ctrl+C 正常提交并退出");
        writer.WriteLine("  status [--json]                       查看采集器、管道与账本健康状态");
        writer.WriteLine("  report [--date today|yesterday|YYYY-MM-DD] [--by app|domain|node|hour]");
        writer.WriteLine("         [--app <name>] [--domain <name>] [--node <name>]");
        writer.WriteLine("         [--sort upload|download|total] [--json]");
        writer.WriteLine("  export [--date today|yesterday|YYYY-MM-DD] [--app <name>] [--domain <name>] [--node <name>] --out <csvPath>");
    }
}
