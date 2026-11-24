using System.Text.RegularExpressions;

namespace NetTrack.Core.Mihomo;

public interface IPipeEnumerator
{
    IEnumerable<string> EnumerateLocalPipeNames();
}

public interface IClashConfigLocator
{
    string? TryReadConfiguredPipeName();
}

/// <summary>
/// 管道名称规范化与安全校验（仅允许本机管道）。
/// </summary>
public static partial class PipeNameValidator
{
    private const string LocalPipePrefix = @"\\.\pipe\";

    public static bool TryNormalize(string raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "管道名称不能为空。";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith(LocalPipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(LocalPipePrefix.Length);
        }
        else if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "只允许连接本机管道（名称或 \\\\.\\pipe\\ 前缀）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains('\\') ||
            trimmed.Contains('/') ||
            trimmed.Contains("..", StringComparison.Ordinal))
        {
            error = "管道名称包含非法路径字符。";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static bool IsAllowedAutoDiscoveryPipe(string pipeName)
    {
        if (string.Equals(pipeName, "clash-verge-service", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(pipeName, "verge-mihomo", StringComparison.OrdinalIgnoreCase) ||
               VergeMihomoPipeRegex().IsMatch(pipeName);
    }

    [GeneratedRegex(@"^verge-mihomo-(production|sidecar-release)-[0-9a-fA-F]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VergeMihomoPipeRegex();
}

/// <summary>
/// 默认 Windows 本机命名管道枚举器。
/// </summary>
public sealed class DefaultWindowsPipeEnumerator : IPipeEnumerator
{
    public IEnumerable<string> EnumerateLocalPipeNames()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(@"\\.\pipe\");
        }
        catch
        {
            yield break;
        }

        foreach (var fullPath in entries)
        {
            var name = Path.GetFileName(fullPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }
}

/// <summary>
/// 轻量读取 Clash Verge 配置文件顶层的 external-controller-pipe（不反序列化整份配置，避免接触敏感字段）。
/// </summary>
public sealed class ClashVergeConfigLocator : IClashConfigLocator
{
    private readonly string _configDirectory;

    public ClashVergeConfigLocator(string? configDirectory = null)
    {
        _configDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev")
            : configDirectory;
    }

    public string? TryReadConfiguredPipeName()
    {
        var yamlPath = Path.Combine(_configDirectory, "clash-verge.yaml");
        if (!File.Exists(yamlPath))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(yamlPath))
            {
                // 只看顶层非缩进键
                if (string.IsNullOrEmpty(rawLine) || char.IsWhiteSpace(rawLine[0]) || rawLine[0] == '#')
                {
                    continue;
                }

                const string key = "external-controller-pipe:";
                if (!rawLine.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                var valuePart = rawLine.Substring(key.Length).Trim();
                valuePart = StripYamlInlineComment(valuePart);
                if (valuePart.Length >= 2 &&
                    ((valuePart[0] == '"' && valuePart[^1] == '"') ||
                     (valuePart[0] == '\'' && valuePart[^1] == '\'')))
                {
                    valuePart = valuePart.Substring(1, valuePart.Length - 2).Trim();
                }

                if (string.IsNullOrWhiteSpace(valuePart) ||
                    string.Equals(valuePart, "null", StringComparison.OrdinalIgnoreCase) ||
                    valuePart == "~")
                {
                    return null;
                }

                return PipeNameValidator.TryNormalize(valuePart, out var normalized, out _)
                    ? normalized
                    : null;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string StripYamlInlineComment(string value)
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
            else if (c == '"' && !inSingle)
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
}

public enum PipeDiscoveryStatus
{
    Connected,
    WaitingForClash,
    Ambiguous,
    ExplicitPipeFailed
}

public sealed record PipeDiscoveryResult(
    PipeDiscoveryStatus Status,
    IMihomoClient? Client,
    MihomoVersionResponse? Version,
    IReadOnlyList<string> AvailablePipes,
    string? Message);

/// <summary>
/// 本机 Mihomo 管道发现与健康探测服务。
/// </summary>
public sealed class PipeDiscoveryService
{
    private readonly IClashConfigLocator _configLocator;
    private readonly IPipeEnumerator _pipeEnumerator;
    private readonly IMihomoClientFactory _clientFactory;
    private readonly TimeSpan _probeTimeout;

    public PipeDiscoveryService(
        IClashConfigLocator configLocator,
        IPipeEnumerator pipeEnumerator,
        IMihomoClientFactory clientFactory,
        TimeSpan probeTimeout)
    {
        _configLocator = configLocator;
        _pipeEnumerator = pipeEnumerator;
        _clientFactory = clientFactory;
        _probeTimeout = probeTimeout;
    }

    public async Task<PipeDiscoveryResult> DiscoverAsync(string? explicitPipeName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitPipeName))
        {
            if (!PipeNameValidator.TryNormalize(explicitPipeName, out var normalized, out var err))
            {
                return new PipeDiscoveryResult(
                    PipeDiscoveryStatus.ExplicitPipeFailed,
                    null,
                    null,
                    Array.Empty<string>(),
                    $"指定的管道名称无效：{err}");
            }

            var probe = await ProbeCandidateAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (probe.Client is not null && probe.Version is not null)
            {
                return new PipeDiscoveryResult(
                    PipeDiscoveryStatus.Connected,
                    probe.Client,
                    probe.Version,
                    new[] { normalized },
                    null);
            }

            return new PipeDiscoveryResult(
                PipeDiscoveryStatus.ExplicitPipeFailed,
                null,
                null,
                Array.Empty<string>(),
                $"无法连接指定的管道 {normalized}：{probe.Error}");
        }

        // 自动发现：先读配置候选，再枚举符合命名规则的运行管道并去重
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configured = _configLocator.TryReadConfiguredPipeName();
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, "clash-verge-service", StringComparison.OrdinalIgnoreCase) &&
            seen.Add(configured))
        {
            candidates.Add(configured);
        }

        var enumerated = _pipeEnumerator
            .EnumerateLocalPipeNames()
            .Where(PipeNameValidator.IsAllowedAutoDiscoveryPipe)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var pipe in enumerated)
        {
            if (seen.Add(pipe))
            {
                candidates.Add(pipe);
            }
        }

        var available = new List<(string Name, IMihomoClient Client, MihomoVersionResponse Version)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProbeCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (probe.Client is not null && probe.Version is not null)
            {
                available.Add((candidate, probe.Client, probe.Version));
            }
        }

        if (available.Count == 0)
        {
            return new PipeDiscoveryResult(
                PipeDiscoveryStatus.WaitingForClash,
                null,
                null,
                Array.Empty<string>(),
                "未发现可用的 Mihomo 管道，等待 Clash 就绪。");
        }

        if (available.Count == 1)
        {
            var chosen = available[0];
            return new PipeDiscoveryResult(
                PipeDiscoveryStatus.Connected,
                chosen.Client,
                chosen.Version,
                new[] { chosen.Name },
                null);
        }

        // 多个可用内核：释放全部试探客户端，提示通过 settings.json 的 pipeName 消除歧义
        var names = available.Select(x => x.Name).ToArray();
        foreach (var item in available)
        {
            item.Client.Dispose();
        }

        return new PipeDiscoveryResult(
            PipeDiscoveryStatus.Ambiguous,
            null,
            null,
            names,
            $"发现多个可用的 Mihomo 管道（{string.Join(", ", names)}），请在 settings.json 中通过 pipeName 指定目标管道。");
    }

    private async Task<(IMihomoClient? Client, MihomoVersionResponse? Version, string? Error)> ProbeCandidateAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        IMihomoClient? client = null;
        try
        {
            client = _clientFactory.Create(pipeName, _probeTimeout);
            var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                client.Dispose();
                return (null, null, "GET /version 未返回有效的 Mihomo 版本信息。");
            }

            // 验证 /connections 快照结构是否合法
            _ = await client.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
            return (client, version, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            return (null, null, ex.Message);
        }
    }
}
