# NetTrack

NetTrack 是一个 Windows 上的 Clash/Mihomo 流量观察工具。它通过本机命名管道读取连接快照，按北京时间统计上传和下载，帮助定位用量集中在哪个小时、应用、域名或出站节点。

- 打开桌面应用即可开始采集，收起到托盘后继续运行。
- 默认的「自动节点观察」会发现实际出站节点，排除已识别的直连和拒绝流量，并标明走向未确认的用量。
- 严格代理账本只纳入有运行内核证据支持的远端代理流量。服务商的实际扣量以服务商账单为准。
- 小时报表区分已采样的零流量、无采集数据和采样空档。CSV 导出采用当前日期和筛选条件，流量保留为整数字节。

## 构建和运行

需要 Windows 和 .NET 10 SDK。启动 Clash Verge Rev 或启用了本机命名管道的 Mihomo 后，可以运行桌面入口。

```powershell
dotnet build NetTrack.slnx --configuration Release
dotnet run --project src/NetTrack.Desktop --configuration Release --no-build
```

CLI 入口支持采集、状态查询、报表和 CSV 导出。

```powershell
dotnet run --project src/NetTrack.Cli --configuration Release --no-build -- status
```

桌面和 CLI 共享本地 SQLite 账本，同一个账本只允许一个采集写入实例。

## 测试

```powershell
dotnet test NetTrack.slnx --configuration Release
```

测试使用临时目录、模拟连接快照或隔离测试管道。
