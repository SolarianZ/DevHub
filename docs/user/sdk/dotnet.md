# DevHub .NET SDK 接入指南

本文面向准备通过官方 `.NET SDK` 连接 DevHub Host 的调用方，覆盖环境准备、运行时发现、常见调用方式、扩展点与最小验证方式。

## 1. 前置条件

- 已按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备 `.NET SDK 8` 或 `.NET SDK 10`，以便构建和运行消费端程序。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

### 2.1 项目引用

仓库内联调时，可以直接引用 SDK 项目：

```xml
<ProjectReference Include="..\..\..\sdks\dotnet\src\DevHub.Sdk\DevHub.Sdk.csproj" />
```

### 2.2 使用本地打包产物

如果你希望更贴近“发布包消费”的形式，可先执行：

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

然后在消费项目中引用输出目录里的 `.nupkg`。正式发布后的主包 `PackageId` 为 `DevHub.Sdk.DotNet`。

## 3. 能力概览

- Runtime discovery：读取并校验 `hub.json` / `token.txt`。
- HTTP JSON-RPC：覆盖 `hub.ping`、`hub.apps.*` 与 `hub.invoke.*`。
- WebSocket Events：覆盖 `hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe` 与 `hub.event`。
- 公开扩展点：`runtime resolver`、按客户端粒度提供 `HttpClient` 的窄 seam、依赖注入工厂。
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`。
- 统一错误模型：`DevHubRpcException`；协议要求 `error.data` 为对象，非对象响应会被视为非法 JSON-RPC 包。

## 4. 运行时发现

SDK 会按以下优先级解析数据根目录：

1. `DevHubClientOptions.DataDir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据根目录

标准目录布局固定如下：

```text
<dataDir>/
├── runtime/
│   ├── hub.json
│   └── token.txt
├── apps/
│   ├── definitions/
│   └── instances/
└── logs/
```

平台默认数据根目录：

- Windows：`%LOCALAPPDATA%/DevHub/`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`$XDG_DATA_HOME/DevHub/`，若未设置则回退到 `~/.local/share/DevHub/`

SDK 固定从 `<dataDir>/runtime/hub.json` 读取发现文件，再通过 `hub.json.tokenFile` 读取令牌。若误传 `runtime` 子目录，SDK 会直接拒绝该路径并要求传入数据根目录。

## 5. 快速开始

### 5.1 创建 HTTP 客户端

```csharp
using DevHub.Sdk;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "quickstart-dotnet"
});

var ping = await client.PingAsync(new { echo = "world" });
Console.WriteLine($"ok={ping.Ok}, serverTimeUtc={ping.ServerTimeUtc:O}");
```

### 5.2 使用环境变量或显式数据目录

```csharp
Environment.SetEnvironmentVariable("DEVHUB_DATA_DIR", dataDir);

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "quickstart-dotnet"
});
```

### 5.3 使用依赖注入工厂

```csharp
using DevHub.Sdk;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddDevHubSdk(options =>
{
    options.ClientId = "quickstart-dotnet";
    options.DataDir = dataDir;
});

using var serviceProvider = services.BuildServiceProvider();
var clientFactory = serviceProvider.GetRequiredService<IDevHubClientFactory>();
var eventsClientFactory = serviceProvider.GetRequiredService<IDevHubEventsClientFactory>();

await using var client = await clientFactory.CreateAsync();
await using var eventsClient = await eventsClientFactory.CreateAsync();
```

## 6. 常见交互场景

### 6.1 查询定义与实例

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "example-client"
});

var definitions = await client.ListDefinitionsAsync();
var definition = await client.GetDefinitionAsync("sample.app", scope: null);
var instances = await client.ListInstancesAsync(new ListInstancesRequest
{
    AppId = "sample.app",
    IncludeOffline = true
});
```

### 6.2 校验与写入定义

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "definition-admin"
});

var definition = new AppDefinition
{
    AppId = "sample.app",
    Scope = null,
    DisplayName = "Sample App",
    Launch = new LaunchConfiguration
    {
        ExePath = "python3",
        ArgsTemplate = "app.py"
    }
};

var validation = await client.ValidateDefinitionAsync(definition);
if (validation.Valid)
{
    await client.UpsertDefinitionAsync(definition);
}
```

### 6.3 事件订阅

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "events-client"
});

await eventsClient.AuthenticateAsync();
var subscriptionId = await eventsClient.SubscribeAsync(new[]
{
    DevHubEventTypes.AppDefinitionUpserted,
    DevHubEventTypes.AppDefinitionDeleted
});

await foreach (var evt in eventsClient.ReadEventsAsync())
{
    Console.WriteLine($"{evt.TimeUtc:O} {evt.Type}");
}

await eventsClient.UnsubscribeAsync(subscriptionId);
```

## 7. 高级扩展

默认情况下，推荐使用 `DevHubClient.FromRuntimeAsync(...)` 与 `DevHubEventsClient.FromRuntimeAsync(...)`。

如果需要接入自定义运行时发现或按客户端粒度提供底层 `HttpClient`，可以通过公开扩展点注入：

```csharp
using DevHub.Sdk;

var client = await DevHubClient.FromRuntimeAsync(
    new DevHubClientOptions
    {
        ClientId = "example-client",
        DataDir = dataDir
    },
    new DevHubClientDependencies
    {
        RuntimeResolver = runtimeResolver,
        HttpClientProvider = httpClientProvider
    });

var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
    new DevHubClientOptions
    {
        ClientId = "example-events-client",
        DataDir = dataDir
    },
    new DevHubEventsClientDependencies
    {
        RuntimeResolver = runtimeResolver
    });
```

`HttpClientProvider` 只负责为当前 `DevHubClient` 提供底层 `HttpClient`，JSON-RPC 请求封装、错误映射与响应校验仍由 SDK 内部负责。低层 `JsonRpcHttpTransport` / `JsonRpcWebSocketSession` 不属于稳定公开契约。

## 8. 最小验证方式

- 直接运行上面的 `PingAsync()` 示例，确认能收到 `ok=true` 的响应。
- 若要验证 `.NET SDK` 工作区自身的测试基线，可执行：

```powershell
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
```

- 若要验证本地发布包是否可生成，可执行：

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

- 若要查看工作区构建、集成测试隔离或仓库级联调要求，请阅读 [`../../developer/guides/development.md`](../../developer/guides/development.md)。

## 9. 相关文档

- [`./README.md`](./README.md)
- [`../../../sdks/dotnet/README.md`](../../../sdks/dotnet/README.md)
- [`../host/quickstart.md`](../host/quickstart.md)
- [`../protocol/README.md`](../protocol/README.md)
- [`../../developer/guides/development.md`](../../developer/guides/development.md)
- [`../../developer/publishing/README.md`](../../developer/publishing/README.md)
