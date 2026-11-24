using System.Globalization;
using System.Net;

namespace NetTrack.Core.Accounting;

/// <summary>
/// 域名、应用名及连接开始时间的归一化与校验工具。
/// </summary>
public static class MetadataNormalizer
{
    public const string UnknownDomain = "未知域名";
    public const string UnknownApp = "未知应用";

    /// <summary>
    /// 按 host -> sniffHost 顺序取第一个有效域名，去空白、去末尾根点、转小写并排除纯 IP。
    /// </summary>
    public static string NormalizeDomain(string? host, string? sniffHost)
    {
        if (TryNormalizeSingleDomain(host, out var normalized))
        {
            return normalized;
        }

        if (TryNormalizeSingleDomain(sniffHost, out normalized))
        {
            return normalized;
        }

        return UnknownDomain;
    }

    public static bool TryNormalizeSingleDomain(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Trim().TrimEnd('.');
        if (candidate.Length == 0 || candidate.Length > 253)
        {
            return false;
        }

        // 排除纯 IPv4 / IPv6（含带方括号的 IPv6）
        var ipCandidate = candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']'
            ? candidate.Substring(1, candidate.Length - 2)
            : candidate;
        if (IPAddress.TryParse(ipCandidate, out _))
        {
            return false;
        }

        var lower = candidate.ToLowerInvariant();
        var labels = lower.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                var isAsciiLabelChar = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
                if (!isAsciiLabelChar)
                {
                    return false;
                }
            }
        }

        normalized = lower;
        return true;
    }

    /// <summary>
    /// 优先从 processPath 提取文件名，再尝试 process，统一 ToLowerInvariant()。
    /// </summary>
    public static string NormalizeApp(string? processPath, string? process)
    {
        if (TryExtractExecutableName(processPath, out var name))
        {
            return name;
        }

        if (TryExtractExecutableName(process, out name))
        {
            return name;
        }

        return UnknownApp;
    }

    public static bool TryExtractExecutableName(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim().Trim('"', '\'').Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // 同时兼容 Windows 与 POSIX 路径分隔符
        var lastSep = trimmed.LastIndexOfAny(['\\', '/']);
        var fileName = lastSep >= 0 ? trimmed.Substring(lastSep + 1) : trimmed;
        fileName = fileName.Trim().Trim('"', '\'').Trim();

        if (fileName.Length == 0 || fileName == "." || fileName == "..")
        {
            return false;
        }

        normalized = fileName.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// 仅当连接 start 字段包含显式时区偏移（Z 或 ±HH:MM）且解析成功时返回 UTC 毫秒。
    /// </summary>
    public static bool TryParseStartWithTimezone(string? startRaw, out long startUtcMs)
    {
        startUtcMs = 0;
        if (string.IsNullOrWhiteSpace(startRaw))
        {
            return false;
        }

        var trimmed = startRaw.Trim();
        if (!HasExplicitTimezoneSuffix(trimmed))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto))
        {
            return false;
        }

        startUtcMs = dto.ToUnixTimeMilliseconds();
        return true;
    }

    private static bool HasExplicitTimezoneSuffix(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        // 匹配末尾 +HH:MM / -HH:MM
        if (value.Length >= 6)
        {
            var sign = value[^6];
            if ((sign == '+' || sign == '-') &&
                char.IsDigit(value[^5]) && char.IsDigit(value[^4]) &&
                value[^3] == ':' &&
                char.IsDigit(value[^2]) && char.IsDigit(value[^1]))
            {
                return true;
            }
        }

        // 匹配末尾 +HHMM / -HHMM
        if (value.Length >= 5)
        {
            var sign = value[^5];
            if ((sign == '+' || sign == '-') &&
                char.IsDigit(value[^4]) && char.IsDigit(value[^3]) &&
                char.IsDigit(value[^2]) && char.IsDigit(value[^1]))
            {
                return true;
            }
        }

        return false;
    }
}
