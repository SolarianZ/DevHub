# DevHub .NET SDK

当前目录用于维护独立于主工程的 DevHub .NET SDK 解决方案。

- 解决方案：`DevHub.DotNetSdk.slnx`
- SDK 项目：`src/DevHub.Sdk/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`

当前阶段已完成 `.NET SDK` 的 M5 子范围实现：

- Runtime discovery：读取并校验 `hub.json` / `token.txt`
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*`
- WebSocket Events：`hub.ws.authenticate`、`hub.events.subscribe/unsubscribe`、`hub.event` 读取
- 统一错误模型：`DevHubRpcException`
- SDK 单元测试 + SDK↔Hub 黑盒集成测试

当前不包含：TS SDK、`tests/conformance`、跨语言 CI 门禁。

## 快速开始

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});

var ping = await client.PingAsync(new { hello = "world" });
var definitions = await client.ListDefinitionsAsync();

await client.DisposeAsync();
```

读取事件流：

```csharp
using DevHub.Sdk;

var eventsClient = await DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});

await eventsClient.AuthenticateAsync();
var subscriptionId = await eventsClient.SubscribeAsync(new[] { "app.instance.registered" });

await foreach (var evt in eventsClient.ReadEventsAsync())
{
    Console.WriteLine($"{evt.Type}: {evt.Payload}");
}
```

## 公开 API

- `DevHubClientOptions`
- `DevHubClient`
- `DevHubEventsClient`
- `DevHubRpcException`
- `DevHub.Sdk.Models.*`

常用命令：

- `dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
