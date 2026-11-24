using System.Text.Json;

namespace NetTrack.Core.Mihomo;

/// <summary>
/// 管道服务端进程身份，用于识别重连前后是否为同一内核进程。
/// </summary>
public sealed record PipeServerIdentity(
    string PipeName,
    uint? ServerPid,
    long? ProcessCreationUtcTicks,
    string? MihomoVersion)
{
    /// <summary>
    /// 仅当能够明确识别双方服务端 PID 一致（且进程创建时间与版本均未变化）时，才确认为同一内核。
    /// </summary>
    public bool IsConfirmedSameKernel(PipeServerIdentity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (!string.Equals(PipeName, other.PipeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ServerPid.HasValue || !other.ServerPid.HasValue || ServerPid.Value != other.ServerPid.Value)
        {
            return false;
        }

        if (ProcessCreationUtcTicks.HasValue && other.ProcessCreationUtcTicks.HasValue &&
            ProcessCreationUtcTicks.Value != other.ProcessCreationUtcTicks.Value)
        {
            return false;
        }

        if (!string.Equals(MihomoVersion, other.MihomoVersion, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}

public sealed record MihomoVersionResponse(string Version, bool IsMeta);

/// <summary>
/// /proxies 中单个成员的定义信息（区分 dialer-proxy 缺失、非空与明确为空字符串）。
/// </summary>
public sealed record ProxyMemberInfo(
    string Name,
    string Type,
    bool HasDialerProxyProperty,
    string? DialerProxyValue);

/// <summary>
/// 某次加载得到的不可变节点类型快照。
/// </summary>
public sealed class ProxyCatalog
{
    public long FetchedAtUtcMs { get; }
    public IReadOnlyDictionary<string, ProxyMemberInfo> Members { get; }

    /// <summary>
    /// 若节点表来自本机 YAML 文件，记录所有已读文件（主配置及本地 provider 缓存）的最大最后修改时间（UTC ticks）。
    /// </summary>
    public long? SourceMaxLastWriteUtcTicks { get; }

    /// <summary>
    /// 若节点表来自本机 YAML 文件，记录其声明并规范化后的 external-controller-pipe 管道名。
    /// </summary>
    public string? DeclaredPipeName { get; }

    /// <summary>
    /// 是否已直接验证该节点表对应运行中内核加载的配置版本。
    /// </summary>
    public bool IsKernelVersionVerified { get; }

    public ProxyCatalog(
        long fetchedAtUtcMs,
        IReadOnlyDictionary<string, ProxyMemberInfo> members,
        long? sourceMaxLastWriteUtcTicks = null,
        bool isKernelVersionVerified = true,
        string? declaredPipeName = null)
    {
        FetchedAtUtcMs = fetchedAtUtcMs;
        Members = members;
        SourceMaxLastWriteUtcTicks = sourceMaxLastWriteUtcTicks;
        IsKernelVersionVerified = isKernelVersionVerified;
        DeclaredPipeName = declaredPipeName;
    }

    /// <summary>
    /// 评估该节点表是否具有内核已加载版本的直接核验证据，并返回不可核验时的具体原因码与说明。
    /// 仅凭磁盘 YAML 的管道名与修改时间仍无法排除内核从另一份配置启动、服务模式改写管道名或热重载后未生效的情形，
    /// 因此只有显式经过内核版本认证（IsKernelVersionVerified == true）的节点表才允许进入严格代理账本。
    /// </summary>
    public (bool Verified, string? ReasonCode, string? ReasonText) EvaluateKernelVerification(PipeServerIdentity? serverIdentity)
    {
        if (serverIdentity is null)
        {
            return (false, "no_kernel_identity", "尚未获取 Clash 内核进程身份");
        }

        if (IsKernelVersionVerified)
        {
            return (true, null, null);
        }

        if (!SourceMaxLastWriteUtcTicks.HasValue)
        {
            return (false, "config_missing", "未找到或无法读取本机 Clash 运行时配置文件");
        }

        if (!string.IsNullOrWhiteSpace(DeclaredPipeName) &&
            !string.Equals(DeclaredPipeName, serverIdentity.PipeName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "pipe_mismatch", "运行时配置声明的管道与当前连接的 Clash 管道不一致（例如服务模式覆盖）");
        }

        if (serverIdentity.ProcessCreationUtcTicks.HasValue &&
            SourceMaxLastWriteUtcTicks.Value > serverIdentity.ProcessCreationUtcTicks.Value)
        {
            return (false, "config_modified_after_kernel_start", "配置文件在内核启动后发生修改（热重载后无法仅凭磁盘文件核验）");
        }

        return (false, "unverified_disk_yaml", "仅凭本地 YAML 无法证明当前内核加载的配置版本，已保守归入走向未确认");
    }

    /// <summary>
    /// 判断该节点表是否有足够证据证明与当前运行内核加载的配置版本一致。
    /// </summary>
    public bool IsVerifiedForKernel(PipeServerIdentity? serverIdentity) =>
        EvaluateKernelVerification(serverIdentity).Verified;

    /// <summary>
    /// 当本机配置文件缺失或不可读时返回的空未核验节点表，确保采集循环不中断并继续记录未确认流量与所选节点观察量。
    /// </summary>
    public static ProxyCatalog CreateEmptyUnverified(long fetchedAtUtcMs) =>
        new(fetchedAtUtcMs, new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal), null, isKernelVersionVerified: false);

    public bool TryGetMember(string name, out ProxyMemberInfo member) =>
        Members.TryGetValue(name, out member!);

    /// <summary>
    /// 比较两份节点表在所有成员上的分类定义（名称、类型、dialer-proxy 存在性与值）是否完全一致。
    /// </summary>
    public bool HasSameDefinitions(ProxyCatalog? other)
    {
        if (other is null || Members.Count != other.Members.Count)
        {
            return false;
        }

        foreach (var (name, left) in Members)
        {
            if (!other.Members.TryGetValue(name, out var right))
            {
                return false;
            }

            if (!string.Equals(left.Type, right.Type, StringComparison.Ordinal) ||
                left.HasDialerProxyProperty != right.HasDialerProxyProperty ||
                !string.Equals(left.DialerProxyValue, right.DialerProxyValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// /connections 中单条活跃连接记录（绝不保留 destinationIP）。
/// </summary>
public sealed record MihomoConnectionRecord(
    string Id,
    long Upload,
    long Download,
    string? StartRaw,
    IReadOnlyList<string> Chains,
    string? Host,
    string? SniffHost,
    string? ProcessPath,
    string? Process,
    IReadOnlyList<string>? ProviderChains = null);

/// <summary>
/// 单次 /connections 快照及其时间与服务端身份锚点。
/// </summary>
public sealed record MihomoConnectionsSnapshot(
    long UploadTotal,
    long DownloadTotal,
    IReadOnlyList<MihomoConnectionRecord> Connections,
    long RequestStartUtcMs,
    long ReceiveUtcMs,
    long ReceiveMonoTicks,
    PipeServerIdentity? ServerIdentity);

/// <summary>
/// Mihomo 只读 API 响应解析器。
/// </summary>
public static class MihomoJsonParser
{
    public static MihomoVersionResponse? ParseVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("version", out var versionProp) ||
                versionProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var version = versionProp.GetString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var isMeta = root.TryGetProperty("meta", out var metaProp) &&
                         metaProp.ValueKind == JsonValueKind.True;

            // Mihomo 返回 meta=true 或版本字符串有效
            if (!isMeta && !version.Contains("mihomo", StringComparison.OrdinalIgnoreCase) &&
                !version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new MihomoVersionResponse(version.Trim(), isMeta);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ProxyCatalog ParseProxies(string json, long fetchedAtUtcMs)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("proxies", out var proxiesElem) ||
            proxiesElem.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Mihomo /proxies 响应缺少有效的 proxies 对象。");
        }

        // 按 Mihomo API 语义区分大小写精确匹配名称
        var members = new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal);
        foreach (var prop in proxiesElem.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = prop.Value.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString() ?? string.Empty
                : string.Empty;

            var hasDialerProxy = prop.Value.TryGetProperty("dialer-proxy", out var dialerProp);
            string? dialerValue = null;
            if (hasDialerProxy)
            {
                dialerValue = dialerProp.ValueKind == JsonValueKind.String
                    ? dialerProp.GetString()
                    : dialerProp.ValueKind == JsonValueKind.Null
                        ? null
                        : dialerProp.GetRawText();
            }

            members[prop.Name] = new ProxyMemberInfo(prop.Name, type, hasDialerProxy, dialerValue);
        }

        return new ProxyCatalog(fetchedAtUtcMs, members);
    }

    public static MihomoConnectionsSnapshot ParseConnections(
        string json,
        long requestStartUtcMs,
        long receiveUtcMs,
        long receiveMonoTicks,
        PipeServerIdentity? serverIdentity)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Mihomo /connections 响应不是有效 JSON 对象。");
        }

        if (!root.TryGetProperty("uploadTotal", out var upTotalElem) || !upTotalElem.TryGetInt64(out var uploadTotal) ||
            !root.TryGetProperty("downloadTotal", out var downTotalElem) || !downTotalElem.TryGetInt64(out var downloadTotal))
        {
            throw new FormatException("Mihomo /connections 响应缺少有效的全局累计字节字段。");
        }

        var list = new List<MihomoConnectionRecord>();
        if (root.TryGetProperty("connections", out var connsElem))
        {
            if (connsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in connsElem.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = GetStringOrNull(item, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    var upload = item.TryGetProperty("upload", out var uProp) && uProp.TryGetInt64(out var uVal) ? uVal : 0L;
                    var download = item.TryGetProperty("download", out var dProp) && dProp.TryGetInt64(out var dVal) ? dVal : 0L;
                    var startRaw = GetStringOrNull(item, "start");

                    var chains = ReadStringArray(item, "chains");
                    var providerChains = ReadStringArray(item, "providerChains");

                    string? host = null;
                    string? sniffHost = null;
                    string? processPath = null;
                    string? process = null;
                    if (item.TryGetProperty("metadata", out var metaElem) && metaElem.ValueKind == JsonValueKind.Object)
                    {
                        host = GetStringOrNull(metaElem, "host");
                        sniffHost = GetStringOrNull(metaElem, "sniffHost");
                        processPath = GetStringOrNull(metaElem, "processPath");
                        process = GetStringOrNull(metaElem, "process");
                        // 严禁读取或存储 destinationIP
                    }

                    list.Add(new MihomoConnectionRecord(
                        id,
                        upload,
                        download,
                        startRaw,
                        chains,
                        host,
                        sniffHost,
                        processPath,
                        process,
                        providerChains));
                }
            }
            else if (connsElem.ValueKind != JsonValueKind.Null)
            {
                throw new FormatException("Mihomo /connections 的 connections 字段既非数组也非 null。");
            }
        }
        else
        {
            throw new FormatException("Mihomo /connections 响应缺少 connections 字段。");
        }

        return new MihomoConnectionsSnapshot(
            uploadTotal,
            downloadTotal,
            list,
            requestStartUtcMs,
            receiveUtcMs,
            receiveMonoTicks,
            serverIdentity);
    }

    private static string? GetStringOrNull(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static List<string> ReadStringArray(JsonElement obj, string propName)
    {
        var result = new List<string>();
        if (obj.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in prop.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.String)
                {
                    var val = elem.GetString();
                    if (val is not null)
                    {
                        result.Add(val);
                    }
                }
            }
        }
        return result;
    }
}
