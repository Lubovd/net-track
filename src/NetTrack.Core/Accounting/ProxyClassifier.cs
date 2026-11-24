using NetTrack.Core.Mihomo;

namespace NetTrack.Core.Accounting;

public enum ConnectionPathKind
{
    Proxy,
    Direct,
    Reject,
    Unconfirmed
}

public sealed record ConnectionClassification(ConnectionPathKind Kind, string? NodeName);

/// <summary>
/// 根据 Mihomo /proxies 精确类型与 dialer-proxy 字段判定单条连接的实际走向。
/// </summary>
public static class ProxyClassifier
{
    public static readonly HashSet<string> ProxyOutboundTypes = new(StringComparer.Ordinal)
    {
        "Shadowsocks",
        "ShadowsocksR",
        "Vmess",
        "Vless",
        "Trojan",
        "Hysteria",
        "Hysteria2",
        "Tuic",
        "WireGuard",
        "Socks5",
        "Http",
        "Snell",
        "AnyTLS",
        "Ssh",
        "Mieru"
    };

    public static readonly HashSet<string> DirectTypes = new(StringComparer.Ordinal)
    {
        "Direct",
        "Compatible"
    };

    public static readonly HashSet<string> RejectTypes = new(StringComparer.Ordinal)
    {
        "Reject",
        "RejectDrop"
    };

    public static readonly HashSet<string> PolicyGroupTypes = new(StringComparer.Ordinal)
    {
        "Selector",
        "URLTest",
        "Fallback",
        "LoadBalance"
    };

    public static readonly HashSet<string> ExplicitUnconfirmedTypes = new(StringComparer.Ordinal)
    {
        "Pass",
        "PassRule",
        "Relay"
    };

    public static ConnectionClassification Classify(IReadOnlyList<string>? chains, ProxyCatalog catalog)
    {
        if (chains is null || chains.Count == 0)
        {
            return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
        }

        // 所有链路成员必须能在 /proxies 中精确查到，且不能包含 Pass / PassRule / Relay
        var resolved = new ProxyMemberInfo[chains.Count];
        for (var i = 0; i < chains.Count; i++)
        {
            if (!catalog.TryGetMember(chains[i], out var member))
            {
                return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
            }

            if (ExplicitUnconfirmedTypes.Contains(member.Type))
            {
                return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
            }

            resolved[i] = member;
        }

        // chains[1..] 必须全部为已知普通策略组（排除双出站或其他未知类型）
        for (var i = 1; i < resolved.Length; i++)
        {
            if (!PolicyGroupTypes.Contains(resolved[i].Type))
            {
                return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
            }
        }

        // chains[0] 为实际出站，且 dialer-proxy 字段必须显式存在并为空字符串
        var outbound = resolved[0];
        if (!outbound.HasDialerProxyProperty || outbound.DialerProxyValue != string.Empty)
        {
            return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
        }

        if (ProxyOutboundTypes.Contains(outbound.Type))
        {
            return new ConnectionClassification(ConnectionPathKind.Proxy, outbound.Name);
        }

        if (DirectTypes.Contains(outbound.Type))
        {
            return new ConnectionClassification(ConnectionPathKind.Direct, null);
        }

        if (RejectTypes.Contains(outbound.Type))
        {
            return new ConnectionClassification(ConnectionPathKind.Reject, null);
        }

        return new ConnectionClassification(ConnectionPathKind.Unconfirmed, null);
    }
}
