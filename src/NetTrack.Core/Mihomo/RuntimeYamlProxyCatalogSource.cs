using NetTrack.Core.Abstractions;

namespace NetTrack.Core.Mihomo;

/// <summary>
/// 无选点副作用的节点与策略组定义来源（不调用 Mihomo GET /proxies，避免触发 Fallback 选点）。
/// </summary>
public interface IProxyCatalogSource
{
    Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 从本机 Mihomo 运行时 YAML 配置及本地 proxy-providers 缓存文件只读提取 name、type、dialer-proxy。
/// 其余字段（订阅链接、密码、服务器地址、控制密钥等）在解析时即丢弃，绝不驻留或记录日志。
/// </summary>
public sealed class RuntimeYamlProxyCatalogSource : IProxyCatalogSource
{
    private static readonly Dictionary<string, string> YamlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ss"] = "Shadowsocks",
        ["shadowsocks"] = "Shadowsocks",
        ["ssr"] = "ShadowsocksR",
        ["shadowsocksr"] = "ShadowsocksR",
        ["vmess"] = "Vmess",
        ["vless"] = "Vless",
        ["trojan"] = "Trojan",
        ["hysteria"] = "Hysteria",
        ["hysteria2"] = "Hysteria2",
        ["tuic"] = "Tuic",
        ["wireguard"] = "WireGuard",
        ["socks5"] = "Socks5",
        ["http"] = "Http",
        ["snell"] = "Snell",
        ["anytls"] = "AnyTLS",
        ["ssh"] = "Ssh",
        ["mieru"] = "Mieru",
        ["direct"] = "Direct",
        ["compatible"] = "Compatible",
        ["reject"] = "Reject",
        ["reject-drop"] = "RejectDrop",
        ["rejectdrop"] = "RejectDrop",
        ["select"] = "Selector",
        ["selector"] = "Selector",
        ["url-test"] = "URLTest",
        ["urltest"] = "URLTest",
        ["fallback"] = "Fallback",
        ["load-balance"] = "LoadBalance",
        ["loadbalance"] = "LoadBalance",
        ["relay"] = "Relay",
        ["pass"] = "Pass",
        ["pass-rule"] = "PassRule",
        ["passrule"] = "PassRule"
    };

    private readonly string _configFilePath;
    private readonly IClock _clock;
    private readonly bool _isKernelVersionVerified;

    public RuntimeYamlProxyCatalogSource(
        string? configDirectory = null,
        string configFileName = "clash-verge.yaml",
        IClock? clock = null,
        bool isKernelVersionVerified = false)
    {
        var dir = string.IsNullOrWhiteSpace(configDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev")
            : configDirectory;

        _configFilePath = Path.Combine(dir, configFileName);
        _clock = clock ?? SystemClock.Instance;
        _isKernelVersionVerified = isKernelVersionVerified;
    }

    public Task<ProxyCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = LoadCatalog();
        return Task.FromResult(catalog);
    }

    public ProxyCatalog LoadCatalog()
    {
        if (!File.Exists(_configFilePath))
        {
            throw new FileNotFoundException($"找不到本机 Mihomo 运行时配置文件：{_configFilePath}");
        }

        var members = new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal);
        AddBuiltInMembers(members);

        var maxLastWriteTicks = File.GetLastWriteTimeUtc(_configFilePath).Ticks;
        var configDir = Path.GetDirectoryName(_configFilePath) ?? string.Empty;
        var providers = new List<ProviderConfigEntry>();

        var declaredPipeName = ParseMainConfigFile(_configFilePath, members, providers);

        // 读取本地 proxy-providers 缓存文件（仅提取 name / type / dialer-proxy，并应用 override.dialer-proxy）
        foreach (var provider in providers)
        {
            var resolved = Path.IsPathRooted(provider.Path)
                ? provider.Path
                : Path.GetFullPath(Path.Combine(configDir, provider.Path));

            if (File.Exists(resolved))
            {
                var providerWriteTicks = File.GetLastWriteTimeUtc(resolved).Ticks;
                if (providerWriteTicks > maxLastWriteTicks)
                {
                    maxLastWriteTicks = providerWriteTicks;
                }
                ParseProviderFile(resolved, provider.OverrideDialerProxy, members);
            }
        }

        // 保留声明的 external-controller-pipe 与最大文件修改时间，由 ProxyCatalog.EvaluateKernelVerification 绑定内核进程身份进行生产核验
        return new ProxyCatalog(
            _clock.UtcNowMs,
            members,
            sourceMaxLastWriteUtcTicks: maxLastWriteTicks,
            isKernelVersionVerified: _isKernelVersionVerified,
            declaredPipeName: declaredPipeName);
    }

    public static ProxyCatalog ParseYamlContent(string yamlContent, long fetchedAtUtcMs)
    {
        var members = new Dictionary<string, ProxyMemberInfo>(StringComparer.Ordinal);
        AddBuiltInMembers(members);
        var providers = new List<ProviderConfigEntry>();

        using var reader = new StringReader(yamlContent);
        var declaredPipeName = ParseMainConfigLines(ReadAllLines(reader), members, providers);

        return new ProxyCatalog(
            fetchedAtUtcMs,
            members,
            sourceMaxLastWriteUtcTicks: null,
            isKernelVersionVerified: true,
            declaredPipeName: declaredPipeName);
    }

    private static void AddBuiltInMembers(Dictionary<string, ProxyMemberInfo> members)
    {
        members["DIRECT"] = new ProxyMemberInfo("DIRECT", "Direct", true, string.Empty);
        members["REJECT"] = new ProxyMemberInfo("REJECT", "Reject", true, string.Empty);
        members["REJECT-DROP"] = new ProxyMemberInfo("REJECT-DROP", "RejectDrop", true, string.Empty);
        members["COMPATIBLE"] = new ProxyMemberInfo("COMPATIBLE", "Compatible", true, string.Empty);
        members["PASS"] = new ProxyMemberInfo("PASS", "Pass", true, string.Empty);
        members["GLOBAL"] = new ProxyMemberInfo("GLOBAL", "Selector", true, string.Empty);
    }

    private static string? ParseMainConfigFile(
        string filePath,
        Dictionary<string, ProxyMemberInfo> members,
        List<ProviderConfigEntry> providers)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return ParseMainConfigLines(ReadAllLines(reader), members, providers);
    }

    private static void ParseProviderFile(
        string filePath,
        string? overrideDialerProxy,
        Dictionary<string, ProxyMemberInfo> members)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = ReadAllLines(reader);

        var inProxies = false;
        var hasTopLevelProxiesKey = lines.Any(l =>
            l.Length > 0 && !char.IsWhiteSpace(l[0]) && l.StartsWith("proxies:", StringComparison.Ordinal));

        if (!hasTopLevelProxiesKey)
        {
            // 部分 provider 缓存文件顶层直接是列表（- name: ...）
            inProxies = true;
        }

        string? curName = null;
        string? curType = null;
        string? curDialer = null;

        void FlushCurrent()
        {
            if (!string.IsNullOrEmpty(curName) && !string.IsNullOrEmpty(curType))
            {
                // 应用 Mihomo proxy-providers 的 override.dialer-proxy 或 provider 级 dialer-proxy 覆盖
                var effectiveDialer = !string.IsNullOrEmpty(overrideDialerProxy)
                    ? overrideDialerProxy
                    : (curDialer ?? string.Empty);

                AddOrMarkConflict(members, new ProxyMemberInfo(curName, NormalizeYamlType(curType), true, effectiveDialer));
            }
            curName = null;
            curType = null;
            curDialer = null;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var indent = CountLeadingSpaces(rawLine);
            var isSequenceItem = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-";

            // 仅当 indent == 0 且不是列表项（-）时才切换顶层键，兼容零缩进列表写法
            if (hasTopLevelProxiesKey && indent == 0 && !isSequenceItem)
            {
                FlushCurrent();
                inProxies = trimmed.StartsWith("proxies:", StringComparison.Ordinal);
                continue;
            }

            if (!inProxies)
            {
                continue;
            }

            if (isSequenceItem)
            {
                FlushCurrent();
                var afterDash = trimmed.Length > 1 ? trimmed.Substring(1).Trim() : string.Empty;
                if (afterDash.StartsWith('{'))
                {
                    ParseInlineFlowMapping(afterDash, out curName, out curType, out curDialer);
                    FlushCurrent();
                }
                else
                {
                    TryApplyScalarField(afterDash, ref curName, ref curType, ref curDialer);
                }
            }
            else
            {
                TryApplyScalarField(trimmed, ref curName, ref curType, ref curDialer);
            }
        }

        FlushCurrent();
    }

    private static string? ParseMainConfigLines(
        IEnumerable<string> lines,
        Dictionary<string, ProxyMemberInfo> members,
        List<ProviderConfigEntry> providers)
    {
        var section = TopSection.None;
        string? declaredPipeName = null;
        string? curName = null;
        string? curType = null;
        string? curDialer = null;
        var itemIndent = -1;

        // proxy-providers 块状态
        var providerItemIndent = -1;
        var providerOverrideIndent = -1;
        string? curProviderPath = null;
        string? curProviderDialer = null;
        string? curProviderOverrideDialer = null;

        void FlushCurrentProxy()
        {
            if (section is TopSection.Proxies or TopSection.ProxyGroups &&
                !string.IsNullOrEmpty(curName) &&
                !string.IsNullOrEmpty(curType))
            {
                AddOrMarkConflict(members, new ProxyMemberInfo(curName, NormalizeYamlType(curType), true, curDialer ?? string.Empty));
            }
            curName = null;
            curType = null;
            curDialer = null;
        }

        void FlushCurrentProvider()
        {
            if (section == TopSection.ProxyProviders && !string.IsNullOrWhiteSpace(curProviderPath))
            {
                var effectiveProviderDialer = !string.IsNullOrWhiteSpace(curProviderOverrideDialer)
                    ? curProviderOverrideDialer
                    : curProviderDialer;
                providers.Add(new ProviderConfigEntry(curProviderPath, effectiveProviderDialer));
            }
            curProviderPath = null;
            curProviderDialer = null;
            curProviderOverrideDialer = null;
            providerOverrideIndent = -1;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var indent = CountLeadingSpaces(rawLine);
            var isSequenceItem = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-";

            // 兼容 YAML 1.2.2 合法的零缩进块序列：indent == 0 且为列表项时不退出 proxies / proxy-groups 段
            if (indent == 0 && !(isSequenceItem && section is TopSection.Proxies or TopSection.ProxyGroups))
            {
                FlushCurrentProxy();
                FlushCurrentProvider();
                itemIndent = -1;
                providerItemIndent = -1;

                if (trimmed.StartsWith("proxies:", StringComparison.Ordinal))
                {
                    section = TopSection.Proxies;
                }
                else if (trimmed.StartsWith("proxy-groups:", StringComparison.Ordinal))
                {
                    section = TopSection.ProxyGroups;
                }
                else if (trimmed.StartsWith("proxy-providers:", StringComparison.Ordinal))
                {
                    section = TopSection.ProxyProviders;
                }
                else if (trimmed.StartsWith("external-controller-pipe:", StringComparison.Ordinal))
                {
                    section = TopSection.None;
                    if (TryExtractKeyValue(trimmed, out _, out var rawPipe) &&
                        PipeNameValidator.TryNormalize(rawPipe, out var normalizedPipe, out _))
                    {
                        declaredPipeName = normalizedPipe;
                    }
                }
                else
                {
                    section = TopSection.None;
                }
                continue;
            }

            if (section == TopSection.None)
            {
                continue;
            }

            if (section == TopSection.ProxyProviders)
            {
                // 识别每个 provider 的名称键（proxy-providers 下的第一层缩进）
                if (providerItemIndent < 0 || indent <= providerItemIndent)
                {
                    FlushCurrentProvider();
                    providerItemIndent = indent;
                    continue;
                }

                // 若当前离开 override 子块，重置 override 缩进
                if (providerOverrideIndent >= 0 && indent <= providerOverrideIndent)
                {
                    providerOverrideIndent = -1;
                }

                if (TryExtractKeyValue(trimmed, out var pk, out var pv))
                {
                    if (providerOverrideIndent >= 0)
                    {
                        if (string.Equals(pk, "dialer-proxy", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pv))
                        {
                            curProviderOverrideDialer = pv;
                        }
                    }
                    else if (string.Equals(pk, "path", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pv))
                    {
                        curProviderPath = pv;
                    }
                    else if (string.Equals(pk, "dialer-proxy", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pv))
                    {
                        // Provider 级 dialer-proxy 同样会作用于该 Provider 下全部节点
                        curProviderDialer = pv;
                    }
                    else if (string.Equals(pk, "override", StringComparison.Ordinal))
                    {
                        if (pv.StartsWith('{'))
                        {
                            ParseInlineFlowMapping(pv, out _, out _, out var inlineDialer);
                            if (!string.IsNullOrWhiteSpace(inlineDialer))
                            {
                                curProviderOverrideDialer = inlineDialer;
                            }
                        }
                        else
                        {
                            providerOverrideIndent = indent;
                        }
                    }
                }
                continue;
            }

            // proxies / proxy-groups 列表项（支持缩进列表与零缩进列表）
            if (isSequenceItem && (itemIndent < 0 || indent <= itemIndent))
            {
                FlushCurrentProxy();
                itemIndent = indent;

                var afterDash = trimmed.Length > 1 ? trimmed.Substring(1).Trim() : string.Empty;
                if (afterDash.StartsWith('{'))
                {
                    ParseInlineFlowMapping(afterDash, out curName, out curType, out curDialer);
                    FlushCurrentProxy();
                }
                else
                {
                    TryApplyScalarField(afterDash, ref curName, ref curType, ref curDialer);
                }
            }
            else if (itemIndent >= 0 && indent > itemIndent)
            {
                TryApplyScalarField(trimmed, ref curName, ref curType, ref curDialer);
            }
        }

        FlushCurrentProxy();
        FlushCurrentProvider();
        return declaredPipeName;
    }

    private sealed record ProviderConfigEntry(string Path, string? OverrideDialerProxy);

    /// <summary>
    /// 写入节点定义；若同名节点在不同 Provider 或主配置间存在冲突定义，标记为冲突未知类型，防止后者覆盖前者导致直连误计。
    /// </summary>
    private static void AddOrMarkConflict(Dictionary<string, ProxyMemberInfo> members, ProxyMemberInfo candidate)
    {
        if (!members.TryGetValue(candidate.Name, out var existing))
        {
            members[candidate.Name] = candidate;
            return;
        }

        // 内置 GLOBAL 允许被配置中的 proxy-groups 覆盖（常规 Mihomo 语义）
        if (string.Equals(candidate.Name, "GLOBAL", StringComparison.Ordinal) &&
            string.Equals(existing.Type, "Selector", StringComparison.Ordinal))
        {
            members[candidate.Name] = candidate;
            return;
        }

        if (string.Equals(existing.Type, "AmbiguousDuplicate", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(existing.Type, candidate.Type, StringComparison.Ordinal) &&
            existing.HasDialerProxyProperty == candidate.HasDialerProxyProperty &&
            string.Equals(existing.DialerProxyValue, candidate.DialerProxyValue, StringComparison.Ordinal))
        {
            return;
        }

        members[candidate.Name] = new ProxyMemberInfo(candidate.Name, "AmbiguousDuplicate", HasDialerProxyProperty: false, DialerProxyValue: null);
    }

    private static void ParseInlineFlowMapping(string flow, out string? name, out string? type, out string? dialerProxy)
    {
        name = null;
        type = null;
        dialerProxy = null;

        var start = flow.IndexOf('{');
        var end = flow.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return;
        }

        var inner = flow.Substring(start + 1, end - start - 1);
        foreach (var part in SplitFlowPairs(inner))
        {
            TryApplyScalarField(part.Trim(), ref name, ref type, ref dialerProxy);
        }
    }

    private static IEnumerable<string> SplitFlowPairs(string inner)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var segStart = 0;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle && (i == 0 || inner[i - 1] != '\\'))
            {
                inDouble = !inDouble;
            }
            else if (!inSingle && !inDouble)
            {
                if (c is '{' or '[')
                {
                    depth++;
                }
                else if (c is '}' or ']')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (c == ',' && depth == 0)
                {
                    yield return inner.Substring(segStart, i - segStart);
                    segStart = i + 1;
                }
            }
        }

        if (segStart < inner.Length)
        {
            yield return inner.Substring(segStart);
        }
    }

    private static void TryApplyScalarField(string line, ref string? name, ref string? type, ref string? dialerProxy)
    {
        if (!TryExtractKeyValue(line, out var key, out var value))
        {
            return;
        }

        if (string.Equals(key, "name", StringComparison.Ordinal) && name is null)
        {
            name = value;
        }
        else if (string.Equals(key, "type", StringComparison.Ordinal) && type is null)
        {
            type = value;
        }
        else if (string.Equals(key, "dialer-proxy", StringComparison.Ordinal) && dialerProxy is null)
        {
            dialerProxy = value;
        }
    }

    private static bool TryExtractKeyValue(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        key = line.Substring(0, colon).Trim();
        var rawVal = line.Substring(colon + 1).Trim();
        rawVal = StripInlineComment(rawVal);

        if (rawVal.Length >= 2 &&
            ((rawVal[0] == '"' && rawVal[^1] == '"') ||
             (rawVal[0] == '\'' && rawVal[^1] == '\'')))
        {
            rawVal = UnescapeQuoted(rawVal);
        }

        if (string.Equals(rawVal, "null", StringComparison.OrdinalIgnoreCase) || rawVal == "~")
        {
            rawVal = string.Empty;
        }

        value = rawVal;
        return true;
    }

    private static string UnescapeQuoted(string quoted)
    {
        var inner = quoted.Substring(1, quoted.Length - 2);
        return quoted[0] == '"'
            ? inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : inner.Replace("''", "'", StringComparison.Ordinal);
    }

    private static string StripInlineComment(string value)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle && (i == 0 || value[i - 1] != '\\'))
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value.Substring(0, i).Trim();
            }
        }
        return value.Trim();
    }

    public static string NormalizeYamlType(string yamlType) =>
        YamlTypeMap.TryGetValue(yamlType.Trim(), out var canonical) ? canonical : yamlType.Trim();

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }
        return count;
    }

    private static List<string> ReadAllLines(TextReader reader)
    {
        var list = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            list.Add(line);
        }
        return list;
    }

    private enum TopSection
    {
        None,
        Proxies,
        ProxyGroups,
        ProxyProviders
    }
}
