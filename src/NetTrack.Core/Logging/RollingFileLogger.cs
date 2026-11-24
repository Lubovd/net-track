using System.Globalization;
using System.Text;
using NetTrack.Core.Abstractions;

namespace NetTrack.Core.Logging;

public interface ILogSink
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 按北京时间自然日滚动、单文件 10 MiB 轮换、保留 14 天的轻量运行日志器。
/// 只记录状态、重试与错误，严禁写入敏感连接内容。
/// </summary>
public sealed class RollingFileLogger : ILogSink
{
    public const long DefaultMaxFileBytes = 10L * 1024 * 1024;
    public const int DefaultRetentionDays = 14;

    private readonly string _logsDirectory;
    private readonly IClock _clock;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly object _sync = new();
    private DateOnly? _lastCleanupDateBj;

    public RollingFileLogger(
        string logsDirectory,
        IClock? clock = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int retentionDays = DefaultRetentionDays)
    {
        _logsDirectory = logsDirectory;
        _clock = clock ?? SystemClock.Instance;
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logsDirectory);
                var nowMs = _clock.UtcNowMs;
                var todayBj = BeijingTime.GetBeijingDate(nowMs);

                if (_lastCleanupDateBj != todayBj)
                {
                    CleanupExpiredLogs(todayBj);
                    _lastCleanupDateBj = todayBj;
                }

                var dateTag = BeijingTime.FormatDate(todayBj);
                var targetFile = ResolveLogFilePath(dateTag);
                var timestamp = BeijingTime.FormatIso(nowMs);
                var line = exception is null
                    ? $"{timestamp} [{level}] {SanitizeLine(message)}{Environment.NewLine}"
                    : $"{timestamp} [{level}] {SanitizeLine(message)} | {exception.GetType().Name}: {SanitizeLine(exception.Message)}{Environment.NewLine}";

                File.AppendAllText(targetFile, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 日志磁盘满或不可写时静默降级，不中断采集器运行
        }
    }

    private string ResolveLogFilePath(string dateTag)
    {
        var basePath = Path.Combine(_logsDirectory, $"net-track-{dateTag}.log");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < _maxFileBytes)
        {
            return basePath;
        }

        var index = 1;
        while (true)
        {
            var rotatedPath = Path.Combine(_logsDirectory, $"net-track-{dateTag}.{index}.log");
            if (!File.Exists(rotatedPath) || new FileInfo(rotatedPath).Length < _maxFileBytes)
            {
                return rotatedPath;
            }
            index++;
        }
    }

    private void CleanupExpiredLogs(DateOnly todayBj)
    {
        var cutoff = todayBj.AddDays(-(_retentionDays - 1));
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logsDirectory, "net-track-*.log"))
            {
                var name = Path.GetFileName(file);
                if (TryParseDateFromFileName(name, out var fileDate) && fileDate < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略单文件删除失败
                    }
                }
            }
        }
        catch
        {
            // 忽略目录扫描异常
        }
    }

    private static bool TryParseDateFromFileName(string fileName, out DateOnly date)
    {
        // 格式：net-track-YYYY-MM-DD.log 或 net-track-YYYY-MM-DD.N.log
        date = default;
        if (!fileName.StartsWith("net-track-", StringComparison.OrdinalIgnoreCase) || fileName.Length < 20)
        {
            return false;
        }

        var datePart = fileName.Substring("net-track-".Length, 10);
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string SanitizeLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>
/// 空日志实现，供不需要文件输出的场景或单元测试使用。
/// </summary>
public sealed class NullLogSink : ILogSink
{
    public static NullLogSink Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
