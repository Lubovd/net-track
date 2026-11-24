using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using NetTrack.Core.Abstractions;
using NetTrack.Core.Accounting;
using NetTrack.Core.Collection;
using NetTrack.Core.Configuration;
using NetTrack.Core.Locking;
using NetTrack.Core.Mihomo;
using NetTrack.Core.Reporting;
using NetTrack.Core.Storage;
using Xunit;

namespace NetTrack.IntegrationTests;

/// <summary>
/// 隔离命名管道端到端测试：使用本地临时模拟管道服务端与临时 YAML 配置，不触碰真实 Clash 或用户账本。
/// </summary>
public sealed class IsolatedNamedPipeIntegrationTests
{
    [Fact]
    public async Task EndToEnd_RealNamedPipeHttpOverPipe_CollectsProxyTrafficWithoutCallingProxiesEndpoint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-int-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, "clash-config");
        Directory.CreateDirectory(configDir);

        var pipeName = $"verge-mihomo-production-{Guid.NewGuid():N}"[..36];
        var yamlPath = Path.Combine(configDir, "clash-verge.yaml");
        File.WriteAllText(yamlPath, $$"""
            external-controller-pipe: {{pipeName}}
            proxies:
              - name: "HK-01"
                type: ss
            proxy-groups:
              - name: "代理"
                type: select
            """);
        // 将模拟配置修改时间设为早于服务端进程启动时间，模拟内核已加载该版本配置
        File.SetLastWriteTimeUtc(yamlPath, Process.GetCurrentProcess().StartTime.ToUniversalTime().AddSeconds(-10));

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var proxiesEndpointCalled = false;
        var frameIndex = 0;
        var baseStartTime = DateTimeOffset.UtcNow.AddSeconds(1);

        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var reader = new StreamReader(server, Encoding.ASCII, false, 4096, leaveOpen: true);
                var reqLine = await reader.ReadLineAsync(serverCts.Token);
                if (reqLine is null)
                {
                    continue;
                }

                // 读完请求头
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(serverCts.Token))) { }

                string body;
                if (reqLine.StartsWith("GET /version ", StringComparison.Ordinal))
                {
                    body = """{"meta":true,"version":"v1.19.33"}""";
                }
                else if (reqLine.StartsWith("GET /proxies", StringComparison.Ordinal))
                {
                    proxiesEndpointCalled = true;
                    body = """{"proxies":{}}""";
                }
                else if (reqLine.StartsWith("GET /connections", StringComparison.Ordinal))
                {
                    var idx = Interlocked.Increment(ref frameIndex);
                    // idx 1 是发现探测；idx 2 建立基线；idx 3+ 产生新增字节
                    var connStart = baseStartTime.ToString("O");
                    body = idx <= 2
                        ? """{"uploadTotal":0,"downloadTotal":0,"connections":[]}"""
                        : $$$"""{"uploadTotal":12000,"downloadTotal":34000,"connections":[{"id":"c1","upload":10000,"download":30000,"start":"{{{connStart}}}","chains":["HK-01","代理"],"metadata":{"host":"Files.Example.com.","processPath":"C:\\Apps\\TestApp.EXE"}},{"id":"c2","upload":2000,"download":4000,"start":"{{{connStart}}}","chains":["DIRECT","代理"],"metadata":{"host":"music.qq.com","processPath":"C:\\Apps\\QQMusic.exe"}}]}""";
                }
                else
                {
                    body = "{}";
                }

                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                await server.WriteAsync(headerBytes, serverCts.Token);
                await server.WriteAsync(bodyBytes, serverCts.Token);
                await server.FlushAsync(serverCts.Token);
            }
        });

        try
        {
            var paths = new NetTrackPaths(tempDir);
            var lockRes = CollectorFileLock.TryAcquire(paths.LockFilePath, "integration-test");
            Assert.Equal(CollectorLockStatus.Acquired, lockRes.Status);

            var options = new CollectorOptions
            {
                ExplicitPipeName = pipeName,
                PollIntervalSeconds = 1,
                CommitIntervalSeconds = 1,
                GapThresholdSeconds = 5
            };

            var discovery = new PipeDiscoveryService(
                new ClashVergeConfigLocator(configDir),
                new DefaultWindowsPipeEnumerator(),
                new MihomoPipeClientFactory(),
                TimeSpan.FromSeconds(2));

            using var ledger = new SqliteLedgerStore(paths.DatabasePath);
            using var runner = new CollectorRunner(
                options,
                discovery,
                new RuntimeYamlProxyCatalogSource(configDir, isKernelVersionVerified: true),
                ledger,
                lockRes.LockHandle!);

            runner.Start();

            // 等待至少 3 帧被采样与提交
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref frameIndex) < 4)
            {
                await Task.Delay(150);
            }

            var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
            Assert.True(stopRes.FlushSucceeded);

            // 确认全程从未调用 GET /proxies（零选点副作用）
            Assert.False(proxiesEndpointCalled);

            // 查询报表：直连 QQMusic 被排除，仅代理 TestApp 入账
            var query = new ReportQueryService(paths);
            var todayBj = BeijingTime.GetBeijingDate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var report = query.QueryDailyReport(new ReportQueryRequest(todayBj, ReportGroupBy.App), options);

            Assert.Equal(10000, report.Summary.UploadBytes);
            Assert.Equal(30000, report.Summary.DownloadBytes);
            Assert.Single(report.Items);
            Assert.Equal("testapp.exe", report.Items[0].Key);
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 对应 TRIAL_REVIEW.md 验收项 1：
    /// 使用生产默认工厂（RuntimeYamlProxyCatalogSource 默认 isKernelVersionVerified: false），
    /// 通过真实命名管道验证：当 clash-verge.yaml 声明的管道匹配当前连接管道且修改时间不晚于内核启动时间时，
    /// 严格代理账本正常入账且自动发现实际出站节点（排除 DIRECT）；且全程不调用 GET /proxies。
    /// </summary>
    [Fact]
    public async Task ProductionDefaultFactory_RuntimeYamlMatchesPipeAndBeforeProcessStart_ConfirmsStrictProxyTraffic_AndDiscoversNodes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nettrack-prod-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configDir = Path.Combine(tempDir, "verge-cfg");
        Directory.CreateDirectory(configDir);

        var pipeName = $"verge-mihomo-prod-default-{Guid.NewGuid():N}";
        var yamlPath = Path.Combine(configDir, "clash-verge.yaml");
        File.WriteAllText(yamlPath, $"""
            external-controller-pipe: \\.\pipe\{pipeName}
            proxies:
              - name: "HK-Prod-01"
                type: vless
            proxy-groups:
              - name: "Proxy"
                type: select
            """);
        // 将配置文件修改时间设为早于当前测试进程（充当管道服务端）启动时间，模拟 Clash Verge 正常启动顺序
        var serverStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        File.SetLastWriteTimeUtc(yamlPath, serverStartUtc.AddSeconds(-10));

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var frameIndex = 0;
        var proxiesEndpointCalled = false;

        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var reader = new StreamReader(server, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(serverCts.Token);
                if (string.IsNullOrEmpty(requestLine)) continue;

                while (true)
                {
                    var header = await reader.ReadLineAsync(serverCts.Token);
                    if (string.IsNullOrEmpty(header)) break;
                }

                string body;
                if (requestLine.StartsWith("GET /version ", StringComparison.Ordinal))
                {
                    body = """{"meta":true,"version":"v1.19.33"}""";
                }
                else if (requestLine.StartsWith("GET /proxies", StringComparison.Ordinal))
                {
                    proxiesEndpointCalled = true;
                    body = """{"proxies":{}}""";
                }
                else if (requestLine.StartsWith("GET /connections ", StringComparison.Ordinal))
                {
                    var idx = Interlocked.Increment(ref frameIndex);
                    // idx 1 是发现探测；idx 2 建立基线；idx 3+ 产生新增字节
                    var connStart = DateTimeOffset.UtcNow.AddMilliseconds(-20).ToString("O");
                    if (idx <= 2)
                    {
                        body = """{"downloadTotal":0,"uploadTotal":0,"connections":[]}""";
                    }
                    else
                    {
                        var up = (idx - 2) * 4000;
                        var down = (idx - 2) * 8000;
                        body = $$"""
                        {
                          "downloadTotal": {{down + 500}},
                          "uploadTotal": {{up + 500}},
                          "connections": [
                            {
                              "id": "prod-proxy-1",
                              "upload": {{up}},
                              "download": {{down}},
                              "start": "{{connStart}}",
                              "chains": ["HK-Prod-01", "Proxy"],
                              "providerChains": ["paid-airport"],
                              "metadata": { "host": "api.github.com", "processPath": "C:\\Apps\\Code.exe" }
                            },
                            {
                              "id": "prod-direct-1",
                              "upload": 500,
                              "download": 500,
                              "start": "{{connStart}}",
                              "chains": ["DIRECT"],
                              "metadata": { "host": "local.test", "processPath": "C:\\Apps\\Local.exe" }
                            }
                          ]
                        }
                        """;
                    }
                }
                else
                {
                    body = "{}";
                }

                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(response);
                await server.WriteAsync(headerBytes, serverCts.Token);
                await server.WriteAsync(bodyBytes, serverCts.Token);
                await server.FlushAsync(serverCts.Token);
            }
        });

        try
        {
            var paths = new NetTrackPaths(tempDir);
            var lockRes = CollectorFileLock.TryAcquire(paths.LockFilePath, "integration-prod");
            Assert.Equal(CollectorLockStatus.Acquired, lockRes.Status);

            var options = new CollectorOptions
            {
                ExplicitPipeName = pipeName,
                PollIntervalSeconds = 1,
                CommitIntervalSeconds = 1,
                GapThresholdSeconds = 5
            };

            var discovery = new PipeDiscoveryService(
                new ClashVergeConfigLocator(configDir),
                new DefaultWindowsPipeEnumerator(),
                new MihomoPipeClientFactory(),
                TimeSpan.FromSeconds(2));

            using var ledger = new SqliteLedgerStore(paths.DatabasePath);
            // 故意使用生产默认构造（不传 isKernelVersionVerified: true）
            using var runner = new CollectorRunner(
                options,
                discovery,
                new RuntimeYamlProxyCatalogSource(configDir),
                ledger,
                lockRes.LockHandle!);

            runner.Start();

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref frameIndex) < 4)
            {
                await Task.Delay(150);
            }

            var stopRes = await runner.StopAsync(releaseLockOnFailure: true);
            Assert.True(stopRes.FlushSucceeded);
            Assert.False(proxiesEndpointCalled);

            var query = new ReportQueryService(paths);
            var todayBj = BeijingTime.GetBeijingDate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var report = query.QueryDailyReport(new ReportQueryRequest(todayBj, ReportGroupBy.Node), options);

            // 1. 严格代理账本：仅凭磁盘 YAML 无内核级核验证据时，保守归入未确认（严禁误计直连）
            Assert.False(report.AttributionVerified);
            Assert.Equal("unverified_disk_yaml", report.AttributionReasonCode);
            Assert.Equal(0, report.Summary.TotalBytes);
            Assert.True(report.Summary.UnconfirmedDownloadBytes >= 8000);

            // 2. 方案 B 自动节点发现与观察：未手填 selectedNodes、不重启采集，直接自动发现并记录观察量，排除 DIRECT
            var discovered = query.QueryDiscoveredNodes(todayBj);
            var singleNode = Assert.Single(discovered);
            Assert.Equal("HK-Prod-01", singleNode.Node);
            Assert.Equal("paid-airport", singleNode.Provider);

            var autoReport = query.QueryDailyReport(
                new ReportQueryRequest(todayBj, ReportGroupBy.Domain, Filters: new ReportFilters(Node: singleNode.ObservedFilterKey), Scope: ReportScope.AutoNodeObserved),
                options);
            Assert.Equal("auto_node_observed", autoReport.Scope);
            Assert.True(autoReport.Summary.UploadBytes >= 4000);
            Assert.True(autoReport.Summary.DownloadBytes >= 8000);
            var domainItem = Assert.Single(autoReport.Items);
            Assert.Equal("api.github.com", domainItem.Key);
            Assert.DoesNotContain(autoReport.Items, i => i.Key == "local.test");
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
