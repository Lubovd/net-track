using NetTrack.Core.Accounting;
using NetTrack.Core.Mihomo;
using Xunit;

namespace NetTrack.Core.Tests;

public sealed class PipeDiscoveryAndYamlTests
{
    [Fact]
    public async Task AutoDiscovery_SkipsDeadConfigPipeAndServicePipe_ConnectsToProductionPipe()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var locator = new FakeConfigLocator("verge-mihomo-sidecar-release-a1b2");
        var enumerator = new FakePipeEnumerator(
            "clash-verge-service",
            "unrelated-pipe",
            "verge-mihomo-sidecar-release-a1b2",
            "verge-mihomo-production-deadbeef");

        var probed = new List<string>();
        var factory = new FakeMihomoClientFactory((pipe, _) =>
        {
            probed.Add(pipe);
            if (pipe == "verge-mihomo-production-deadbeef")
            {
                return new FakeMihomoClient(
                    pipe,
                    _ => Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true)),
                    _ => Task.FromResult(new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs, clock.UtcNowMs, clock.GetTimestamp(), null)));
            }

            return new FakeMihomoClient(
                pipe,
                _ => throw new TimeoutException("pipe timeout"),
                _ => throw new TimeoutException("pipe timeout"));
        });

        var svc = new PipeDiscoveryService(locator, enumerator, factory, TimeSpan.FromSeconds(1));
        var res = await svc.DiscoverAsync(null, CancellationToken.None);

        Assert.Equal(PipeDiscoveryStatus.Connected, res.Status);
        Assert.NotNull(res.Client);
        Assert.Equal("verge-mihomo-production-deadbeef", res.Client!.PipeName);
        Assert.DoesNotContain("clash-verge-service", probed);
        Assert.DoesNotContain("unrelated-pipe", probed);
        res.Client.Dispose();
    }

    [Fact]
    public async Task AutoDiscovery_MultipleLivePipes_ReturnsAmbiguous_ExplicitSelectsTarget()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var locator = new FakeConfigLocator(null);
        var enumerator = new FakePipeEnumerator("verge-mihomo", "verge-mihomo-production-1234abcd");

        var factory = new FakeMihomoClientFactory((pipe, _) => new FakeMihomoClient(
            pipe,
            _ => Task.FromResult<MihomoVersionResponse?>(new MihomoVersionResponse("v1.19.33", true)),
            _ => Task.FromResult(new MihomoConnectionsSnapshot(0, 0, [], clock.UtcNowMs, clock.UtcNowMs, clock.GetTimestamp(), null))));

        var svc = new PipeDiscoveryService(locator, enumerator, factory, TimeSpan.FromSeconds(1));

        var amb = await svc.DiscoverAsync(null, CancellationToken.None);
        Assert.Equal(PipeDiscoveryStatus.Ambiguous, amb.Status);
        Assert.Equal(2, amb.AvailablePipes.Count);

        var explicitRes = await svc.DiscoverAsync(@"\\.\pipe\verge-mihomo-production-1234abcd", CancellationToken.None);
        Assert.Equal(PipeDiscoveryStatus.Connected, explicitRes.Status);
        Assert.Equal("verge-mihomo-production-1234abcd", explicitRes.Client!.PipeName);
        explicitRes.Client.Dispose();
    }

    [Fact]
    public void PipeNameValidator_RejectsRemoteAndIllegalPaths()
    {
        Assert.True(PipeNameValidator.TryNormalize(@"\\.\pipe\verge-mihomo", out var norm, out _));
        Assert.Equal("verge-mihomo", norm);

        Assert.False(PipeNameValidator.TryNormalize(@"\\other-host\pipe\verge-mihomo", out _, out _));
        Assert.False(PipeNameValidator.TryNormalize(@"..\verge-mihomo", out _, out _));
    }

    [Fact]
    public void RuntimeYamlProxyCatalogSource_ExtractsNodesGroupsDialerAndLocalProviders()
    {
        using var dir = new TempTestDir("yaml-catalog");
        var providerDir = Path.Combine(dir.Path, "proxy_providers");
        Directory.CreateDirectory(providerDir);

        File.WriteAllText(Path.Combine(providerDir, "sub.yaml"), """
            proxies:
              - { name: "JP-Provider-01", type: vless, server: secret.example.com, uuid: do-not-store }
              - name: "Relay-Leaf"
                type: trojan
                dialer-proxy: "HK-01"
            """);

        File.WriteAllText(Path.Combine(dir.Path, "clash-verge.yaml"), """
            external-controller-pipe: \\.\pipe\verge-mihomo-sidecar-release-abcd
            secret: super-secret-key
            proxies:
              - name: "HK-01"
                type: ss
                server: 1.2.3.4
                password: hidden-password
              - { name: "US-H2", type: hysteria2, server: 5.6.7.8 }
            proxy-groups:
              - name: "代理"
                type: select
                proxies:
                  - "HK-01"
                  - DIRECT
              - name: "故障转移"
                type: fallback
              - name: "链式组"
                type: relay
            proxy-providers:
              main-sub:
                type: http
                url: https://secret-subscription.example.com/sub?token=xyz
                path: ./proxy_providers/sub.yaml
            """);

        var locator = new ClashVergeConfigLocator(dir.Path);
        Assert.Equal("verge-mihomo-sidecar-release-abcd", locator.TryReadConfiguredPipeName());

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero));
        var source = new RuntimeYamlProxyCatalogSource(dir.Path, clock: clock);
        var catalog = source.LoadCatalog();

        Assert.Equal(ConnectionPathKind.Proxy, ProxyClassifier.Classify(["HK-01", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Proxy, ProxyClassifier.Classify(["JP-Provider-01", "故障转移"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Direct, ProxyClassifier.Classify(["DIRECT", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["Relay-Leaf", "代理"], catalog).Kind);
        Assert.Equal(ConnectionPathKind.Unconfirmed, ProxyClassifier.Classify(["HK-01", "链式组"], catalog).Kind);
    }
}
