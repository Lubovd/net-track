using System.Diagnostics;
using System.Globalization;

namespace NetTrack.Core.Abstractions;

/// <summary>
/// 统一时钟抽象，同时提供 UTC 壁钟与高分辨率单调时间，便于测试注入时钟跳变与回拨。
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    long UtcNowMs { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// 生产环境系统时钟实现。
/// </summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>
/// 固定使用 UTC+08:00 的北京时间换算工具，不依赖操作系统本地时区设置。
/// </summary>
public static class BeijingTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public const long HourMs = 3_600_000L;

    public static DateTimeOffset FromUtcMs(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(Offset);

    public static DateOnly GetBeijingDate(long utcMs) =>
        DateOnly.FromDateTime(FromUtcMs(utcMs).DateTime);

    public static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatIso(long utcMs) =>
        FromUtcMs(utcMs).ToString("yyyy-MM-dd'T'HH:mm:ss'+08:00'", CultureInfo.InvariantCulture);

    public static string FormatDisplay(long utcMs) =>
        FromUtcMs(utcMs).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 计算指定 UTC 毫秒时间所在的北京时间自然小时整点对应的 UTC 毫秒时间戳。
    /// </summary>
    public static long GetHourStartUtcMs(long utcMs)
    {
        var bj = FromUtcMs(utcMs);
        var hourStartBj = new DateTimeOffset(bj.Year, bj.Month, bj.Day, bj.Hour, 0, 0, Offset);
        return hourStartBj.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 获取北京时间某自然日 00:00:00 对应的 UTC 毫秒时间戳。
    /// </summary>
    public static long GetDayStartUtcMs(DateOnly dateBj)
    {
        var startBj = new DateTimeOffset(dateBj.Year, dateBj.Month, dateBj.Day, 0, 0, 0, Offset);
        return startBj.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 获取北京时间某自然日指定小时（0-23）整点对应的 UTC 毫秒时间戳。
    /// </summary>
    public static long GetHourStartUtcMs(DateOnly dateBj, int hourBj)
    {
        var startBj = new DateTimeOffset(dateBj.Year, dateBj.Month, dateBj.Day, hourBj, 0, 0, Offset);
        return startBj.ToUnixTimeMilliseconds();
    }

    public static int GetBeijingHour(long utcMs) => FromUtcMs(utcMs).Hour;
}
