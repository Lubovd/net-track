using System.Text.Json;
using System.Text.Json.Serialization;
using NetTrack.Core.Mihomo;

namespace NetTrack.Core.Configuration;

/// <summary>
/// 首版公开的 settings.json 配置模型。
/// </summary>
public sealed class NetTrackSettings
{
    [JsonPropertyName("pipeName")]
    public string? PipeName { get; set; }

    [JsonPropertyName("gapThresholdSeconds")]
    public int GapThresholdSeconds { get; set; } = 5;

    /// <summary>
    /// 用户关注的付费节点名称列表（用于独立的「所选节点观察量」报表，不影响严格代理账本口径）。
    /// </summary>
    [JsonPropertyName("selectedNodes")]
    public List<string>? SelectedNodes { get; set; }
}

/// <summary>
/// 采集器内部运行参数。默认值严格沿用方案约定，测试可通过构造注入覆盖。
/// </summary>
public sealed record CollectorOptions
{
    public string? ExplicitPipeName { get; init; }
    public IReadOnlyList<string> SelectedNodes { get; init; } = Array.Empty<string>();
    public int PollIntervalSeconds { get; init; } = 1;
    public int CommitIntervalSeconds { get; init; } = 5;
    public int GapThresholdSeconds { get; init; } = 5;
    public int RetentionDays { get; init; } = 90;
    public int ProxyRefreshIntervalSeconds { get; init; } = 60;
    public int HttpTimeoutSeconds { get; init; } = 3;
    public int ShutdownRetryWindowSeconds { get; init; } = 5;
    public int ShutdownSingleAttemptTimeoutSeconds { get; init; } = 1;

    /// <summary>
    /// 待更新短尾段最大容忍窗口（毫秒），默认为提交周期 + 空档阈值。
    /// </summary>
    public long PendingMaxWindowMs => (CommitIntervalSeconds + GapThresholdSeconds) * 1000L;

    public static CollectorOptions FromSettings(NetTrackSettings settings) => new()
    {
        ExplicitPipeName = string.IsNullOrWhiteSpace(settings.PipeName) ? null : settings.PipeName.Trim(),
        SelectedNodes = SettingsLoader.NormalizeSelectedNodes(settings.SelectedNodes),
        GapThresholdSeconds = settings.GapThresholdSeconds
    };
}

/// <summary>
/// 加载并校验 settings.json。
/// </summary>
public static class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static (NetTrackSettings? Settings, string? Error) Load(string settingsPath, int pollIntervalSeconds = 1)
    {
        if (Directory.Exists(settingsPath))
        {
            return (null, $"无法读取配置文件 settings.json：目标路径被同名目录占用（{settingsPath}）。");
        }

        string raw;
        try
        {
            raw = File.ReadAllText(settingsPath);
        }
        catch (FileNotFoundException)
        {
            return (new NetTrackSettings(), null);
        }
        catch (DirectoryNotFoundException)
        {
            return (new NetTrackSettings(), null);
        }
        catch (Exception ex)
        {
            return (null, $"无法读取配置文件 settings.json：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "settings.json 内容为空，请检查配置是否被截断或损坏。");
        }

        NetTrackSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<NetTrackSettings>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, $"settings.json JSON 语法无效：{ex.Message}");
        }

        if (parsed is null)
        {
            return (null, "settings.json 内容不能为空对象。");
        }

        if (parsed.GapThresholdSeconds <= pollIntervalSeconds || parsed.GapThresholdSeconds > 3600)
        {
            return (null, $"gapThresholdSeconds 必须严格大于采样周期（{pollIntervalSeconds} 秒）且不超过 3600 秒。");
        }

        if (!string.IsNullOrWhiteSpace(parsed.PipeName))
        {
            if (!PipeNameValidator.TryNormalize(parsed.PipeName, out var normalized, out var pipeErr))
            {
                return (null, $"pipeName 配置无效：{pipeErr}");
            }

            parsed.PipeName = normalized;
        }
        else
        {
            parsed.PipeName = null;
        }

        parsed.SelectedNodes = NormalizeSelectedNodes(parsed.SelectedNodes).ToList();
        return (parsed, null);
    }

    private static readonly HashSet<string> BuiltInNonProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DIRECT",
        "REJECT",
        "REJECT-DROP",
        "COMPATIBLE",
        "PASS"
    };

    public static bool IsBuiltInNonProxy(string? name) =>
        string.IsNullOrWhiteSpace(name) || BuiltInNonProxyNames.Contains(name.Trim());

    /// <summary>
    /// 规范化所选付费节点列表：支持「节点名」或「Provider名::节点名」，去空白、去重并排除内置直连/拒绝策略名。
    /// </summary>
    public static IReadOnlyList<string> NormalizeSelectedNodes(IEnumerable<string>? rawNodes)
    {
        if (rawNodes is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawNodes)
        {
            var target = SelectedNodeTarget.TryParse(item);
            if (target is null || BuiltInNonProxyNames.Contains(target.Node))
            {
                continue;
            }
            if (seen.Add(target.CanonicalKey))
            {
                result.Add(target.CanonicalKey);
            }
        }
        return result;
    }

    public static void Save(string settingsPath, NetTrackSettings settings)
    {
        var dir = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sanitized = new NetTrackSettings
        {
            PipeName = string.IsNullOrWhiteSpace(settings.PipeName) ? null : settings.PipeName.Trim(),
            GapThresholdSeconds = settings.GapThresholdSeconds,
            SelectedNodes = NormalizeSelectedNodes(settings.SelectedNodes).ToList()
        };

        var json = JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }
}

/// <summary>
/// 表示一个所选付费节点匹配目标，支持「Provider名::节点名」（精确限定 Provider，含空 Provider 的「::节点名」）或裸「节点名」。
/// 默认完全兼容旧版字面分隔符（不对反斜杠做转义，且保留 Provider/节点中的空格）；
/// 仅当 Provider 包含「::」、冒号拼接冲突、或存在边界空格等歧义场景时，使用结构化前缀「ntv2::["provider","node"]」确保全局唯一可逆。
/// </summary>
public sealed record SelectedNodeTarget(string? Provider, string Node)
{
    public const string Separator = "::";
    private const string StructuredProviderTag = "ntv2";
    private const string StructuredPrefix = "ntv2::[";

    public bool IsProviderScoped => Provider is not null;

    public string CanonicalKey => Provider is null
        ? Node
        : FormatExactTargetKey(Node, Provider);

    public bool Matches(string node, string provider)
    {
        if (!string.Equals(Node, node, StringComparison.Ordinal))
        {
            return false;
        }

        return !IsProviderScoped || string.Equals(Provider, provider, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断本观察目标（例如裸名 HK-01 或 paid::HK-01）是否覆盖了查询目标（例如 paid::HK-01）。
    /// 裸名目标覆盖同一节点名下的所有 Provider；限定 Provider 的目标仅覆盖同 Provider。
    /// </summary>
    public bool Covers(SelectedNodeTarget queried)
    {
        if (!string.Equals(Node, queried.Node, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsProviderScoped)
        {
            return true;
        }

        return queried.IsProviderScoped && string.Equals(Provider, queried.Provider, StringComparison.Ordinal);
    }

    public static SelectedNodeTarget? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 1. 优先识别显式结构化格式：ntv2::["provider","node"]
        if (TryParseStructured(raw, out var structured))
        {
            return structured;
        }

        // 2. 兼容上一版反斜杠/冒号转义格式（例如 paid\:\:bundle::HK-01、paid\:::HK-01 或 paid\\east::HK-01）：
        // 仅当包含「\:」或「\\」且按转义扫描仍能找到未转义的「::」分隔符时才按转义解码；
        // 更早版本的字面反斜杠（如 paid\::HK-01）在此扫描中找不到未转义「::」，会自动回退到字面解析保留 Provider="paid\"。
        if (raw.Contains("\\:", StringComparison.Ordinal) || raw.Contains("\\\\", StringComparison.Ordinal))
        {
            var escapedParsed = TryParseLegacyColonEscaped(raw);
            if (escapedParsed is not null)
            {
                return escapedParsed;
            }
        }

        // 3. 兼容旧版字面解析：按首个「::」拆分，不对反斜杠做转义，也不裁剪 Provider/节点内原有的空格
        var sepIdx = raw.IndexOf(Separator, StringComparison.Ordinal);
        if (sepIdx < 0)
        {
            return new SelectedNodeTarget(null, raw.Trim());
        }

        var provider = raw.Substring(0, sepIdx);
        var node = raw.Substring(sepIdx + Separator.Length);
        if (string.IsNullOrEmpty(node))
        {
            return null;
        }

        return new SelectedNodeTarget(provider, node);
    }

    private static SelectedNodeTarget? TryParseLegacyColonEscaped(string raw)
    {
        var providerBuf = new System.Text.StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '\\')
            {
                // 上一版转义格式中 Provider 内所有反斜杠均已成对转义为 \\ 或 \:；
                // 若出现孤立反斜杠，则必非上一版转义格式，立即回退字面解析
                if (i + 1 >= raw.Length || (raw[i + 1] != ':' && raw[i + 1] != '\\'))
                {
                    return null;
                }

                providerBuf.Append(raw[i + 1]);
                i += 2;
                continue;
            }

            if (raw[i] == ':')
            {
                // 上一版转义格式中 Provider 内所有冒号均已转义为 \:；
                // 因此未转义冒号只能是分隔符「::」的起点，若后一字符不是「:」（如 paid\::HK::01 中的第二个冒号），立即回退字面解析
                if (i + 1 < raw.Length && raw[i + 1] == ':')
                {
                    var node = raw.Substring(i + Separator.Length);
                    return string.IsNullOrEmpty(node)
                        ? null
                        : new SelectedNodeTarget(providerBuf.ToString(), node);
                }

                return null;
            }

            providerBuf.Append(raw[i]);
            i++;
        }

        return null;
    }

    public static IReadOnlyList<SelectedNodeTarget> ParseAll(IEnumerable<string>? rawList)
    {
        if (rawList is null)
        {
            return Array.Empty<SelectedNodeTarget>();
        }

        var list = new List<SelectedNodeTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawList)
        {
            var parsed = TryParse(item);
            if (parsed is not null && seen.Add(parsed.CanonicalKey))
            {
                list.Add(parsed);
            }
        }
        return list;
    }

    public static bool MatchesAny(IReadOnlyList<SelectedNodeTarget> targets, string node, string provider)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Matches(node, provider))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 生成精确到 Provider 维度的筛选键（无 Provider 时返回「::节点名」，存在歧义或边界空格时使用结构化编码）。
    /// </summary>
    public static string FormatExactTargetKey(string node, string provider)
    {
        if (NeedsStructuredEncoding(provider, node) || HasEdgeWhitespace(provider) || HasEdgeWhitespace(node))
        {
            return FormatStructuredKey(provider, node);
        }

        return $"{provider}{Separator}{node}";
    }

    /// <summary>
    /// 生成全局无冲突显示键：普通 Provider 返回「Provider::节点名」，含「::」等歧义的 Provider 使用结构化编码；
    /// 无 Provider 但节点原名包含「::」时前置「::」以避免与 Provider 键混淆。
    /// </summary>
    public static string FormatDisplayKey(string node, string provider)
    {
        if (!string.IsNullOrEmpty(provider))
        {
            return NeedsStructuredEncoding(provider, node)
                ? FormatStructuredKey(provider, node)
                : $"{provider}{Separator}{node}";
        }

        return node.Contains(Separator, StringComparison.Ordinal)
            ? $"{Separator}{node}"
            : node;
    }

    private static bool NeedsStructuredEncoding(string provider, string node)
    {
        // Provider 含「::」、含反斜杠「\」（避免与旧版转义键冲突）或以单个冒号结尾时，统一使用结构化编码
        if (provider.Contains(Separator, StringComparison.Ordinal) ||
            provider.Contains('\\') ||
            provider.EndsWith(':'))
        {
            return true;
        }

        return string.Equals(provider, StructuredProviderTag, StringComparison.Ordinal);
    }

    private static bool HasEdgeWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    public static bool TryParseStructured(string? raw, out SelectedNodeTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmedOuter = raw.Trim();
        if (!trimmedOuter.StartsWith(StructuredPrefix, StringComparison.Ordinal) ||
            !trimmedOuter.EndsWith(']'))
        {
            return false;
        }

        var jsonArray = trimmedOuter.Substring(StructuredProviderTag.Length + Separator.Length);
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(jsonArray);
            if (parts is { Length: 2 } && parts[0] is not null && !string.IsNullOrEmpty(parts[1]))
            {
                target = new SelectedNodeTarget(parts[0], parts[1]);
                return true;
            }
        }
        catch
        {
            // 非法 JSON 返回 false
        }

        return false;
    }

    public static string FormatStructuredKey(string provider, string node) =>
        $"{StructuredProviderTag}{Separator}{JsonSerializer.Serialize(new[] { provider, node })}";
}
