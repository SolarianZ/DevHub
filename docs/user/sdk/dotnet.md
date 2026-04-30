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
<ProjectReference Include="..\..\..\sdks\dotnet\src\DevHub.Sdk.DependencyInjection\DevHub.Sdk.DependencyInjection.csproj" /> <!-- 仅在使用 AddDevHubSdk / 工厂时需要 -->
```

### 2.2 使用本地打包产物

如需更贴近“发布包消费”的形式，可先执行：

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack
```

然后在消费项目中引用输出目录里的 `.nupkg`。正式发布后的核心包 `PackageId` 为 `DevHub.Sdk.DotNet`，依赖注入 companion package 为 `DevHub.Sdk.DotNet.DependencyInjection`。

## 3. 能力概览

- Runtime discovery：读取并校验 `hub.json` / `token.txt`。
- HTTP JSON-RPC：覆盖 `hub.ping`、`hub.apps.*` 与 `hub.invoke.*`。
- WebSocket Events：覆盖 `hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe` 与 `hub.event`。
- loopback runtime discovery：接受 `localhost`、IPv4 loopback 与 IPv6 loopback 形式的合法 `hub.json` 端点。
- 单读取器事件契约：每个 `DevHubEventsClient` 同一时刻只允许一个活动中的 `ReadEventsAsync` 读取器。
- 分离的实例注册结果：`RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`，将 `AppInstance` 快照与 `InstanceSessionToken` 分离。
- 事件恢复约束：订阅结果未知或本地事件缓冲溢出时，当前 WebSocket 会话终止，后续需要重新认证并重新订阅。
- 已放弃请求本地维护：`DevHubEventsClient` 提供 `GetAbandonedRequestCount(...)` 与 `ClearAbandonedRequests(...)`，可按过滤器统计或清理本地已放弃请求记录。
- 公开扩展点：`runtime resolver`、按客户端粒度提供 `HttpClient` 的窄 seam、可选 `ILoggerFactory`；`AddDevHubSdk` 与客户端工厂位于 companion package。
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`。
- 统一错误模型：`DevHubRpcException`；协议 `error.data` 通过 `ErrorData` 暴露，非对象响应会被视为非法 JSON-RPC 包。
- 请求标识边界：SDK 默认生成 string 形式的 JSON-RPC `id`；若观察或桥接原始协议载荷，numeric `id` 仅以 Host 可无损处理的 `Int64` 整数为合法范围，小数或越界数值会被 SDK 视为非法响应。

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

依赖注入扩展由 companion package `DevHub.Sdk.DotNet.DependencyInjection` 提供，公开类型仍位于 `DevHub.Sdk` 命名空间。

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

var definition = await client.GetDefinitionAsync("sample.app", scope: "");
var instances = await client.ListInstancesAsync(new ListInstancesRequest
{
    AppId = "sample.app",
    Scope = null,
    IncludeOffline = true
});
var instance = await client.GetInstanceAsync("sample-inst-1");
```

列表查询按当前协议必须显式提供 `scope`；如需跨全部作用域枚举，请对 `ListDefinitionsAsync(...)` / `ListInstancesAsync(...)` 显式传入 `null`，不要省略该字段。
`GetInstanceAsync(...)` 按精确 `instanceId` 读取单个 `AppInstance` 快照；实例离线但仍保留在注册表时，读取结果仍返回该快照，未命中则透传 `instance_not_found`。

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
    Scope = "",
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

所有会序列化 `scope` 的出站模型都必须显式赋值 `Scope`。Global 作用域使用 `string.Empty`，未赋值状态会在本地直接失败。

### 6.3 注册实例与实例所有权凭据

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "instance-owner"
});

var registered = await client.RegisterInstanceAsync(
    new AppInstanceRegistration
    {
        InstanceId = "sample-inst-1",
        AppId = "sample.app",
        Scope = string.Empty,
        Pid = Environment.ProcessId,
        Invoke = new InvokeCapability
        {
            Poll = true,
            Respond = true
        }
    },
    password: "instance-password");

var snapshot = registered.Instance;
var token = registered.InstanceSessionToken;
```

- `RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`，其中 `Instance` 为 `AppInstance` 快照，`InstanceSessionToken` 为实例所有权凭据。
- `password` 只作为 `RegisterInstanceAsync(..., password)` 的独立参数出现；`AppInstanceRegistration`、`AppInstance` 与 `app.instance.*` 事件载荷都不包含 `password` 或 `instanceSessionToken`。
- `AppInstance`、`GetInstanceAsync(...)`、`ListInstancesAsync(...)` 与 `app.instance.*` 事件载荷都不包含 `instanceSessionToken`。
- `HeartbeatAsync(...)`、`UnregisterInstanceAsync(...)`、`PollAsync(...)` 与 `RespondAsync(...)` 需要显式使用 `RegisterInstanceResult.InstanceSessionToken`。

### 6.4 事件订阅

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

`DevHubEventsClient` 的运行时约束如下：

- 同一时刻只允许一个活动中的 `ReadEventsAsync` 读取器。
- 同一实例上的 `AuthenticateAsync(...)` 会串行执行，避免并发认证竞态。
- 若底层 WebSocket 终止，当前活动读取器只会排空已缓冲事件并结束；后续读取前需要重新执行 `AuthenticateAsync()`，并重新执行 `SubscribeAsync()` 恢复订阅。
- `SubscribeAsync(...)` 或 `UnsubscribeAsync(...)` 在请求发出后若因超时或取消进入结果未知状态，SDK 会主动废弃当前 WebSocket 会话；后续必须重新认证并重新订阅。
- 本地事件缓冲采用有界 fail-fast 队列；消费者处理速度落后导致缓冲溢出时，当前事件流会终止，并要求重新认证与重新订阅。

### 6.5 已放弃请求维护

`DevHubEventsClient` 暴露两组纯本地维护接口：

```csharp
using DevHub.Sdk.Models;

var total = eventsClient.GetAbandonedRequestCount();
var appScoped = eventsClient.GetAbandonedRequestCount(new AbandonedRequestFilter
{
    AppId = "sample.app",
    Method = "hub.apps.getDefinition"
});

var removed = eventsClient.ClearAbandonedRequests(new AbandonedRequestFilter
{
    OlderThan = TimeSpan.FromMinutes(2)
});
```

- `GetAbandonedRequestCount(...)` 返回当前匹配过滤条件的已放弃请求数量。
- `ClearAbandonedRequests(...)` 只移除匹配条件的本地记录，并返回本次实际移除数量。
- `AbandonedRequestFilter` 支持 `OlderThan`、`AppId`、`Method` 三个可选条件；同时提供多个条件时按逻辑与匹配。
- 两个接口都只读取或修改当前 `DevHubEventsClient` 关联 WebSocket 会话中的本地 tombstone 记录，不发送 JSON-RPC 请求，不隐式重连，也不改变当前认证或订阅状态。
- `AppId` 匹配采用最佳努力规则：只有请求进入已放弃状态时能稳定识别 `appId` 的记录才会命中 `AppId` 过滤条件。
- 某条记录被手动清理后，如果服务端随后返回同一 `requestId` 的迟到响应，该响应会回到既有 unknown `response id` 故障语义，而不是继续被忽略。

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
        HttpClientProvider = httpClientProvider,
        LoggerFactory = loggerFactory
    });

var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
    new DevHubClientOptions
    {
        ClientId = "example-events-client",
        DataDir = dataDir
    },
    new DevHubEventsClientDependencies
    {
        RuntimeResolver = runtimeResolver,
        LoggerFactory = loggerFactory
    });
```

`HttpClientProvider` 只负责为当前 `DevHubClient` 提供底层 `HttpClient`，JSON-RPC 请求封装、错误映射与响应校验仍由 SDK 内部负责。低层 `JsonRpcHttpTransport` / `JsonRpcWebSocketSession` 不属于稳定公开契约。

`LoggerFactory` 为可选扩展点。配置后，SDK 会输出 runtime discovery、HTTP/WS 连接生命周期、认证、订阅迁移、缓冲溢出与终止协议错误的结构化日志；日志不会写出 bearer token、`instanceSessionToken` 或 token 文件内容。

## 8. 最小验证方式

- 直接运行上面的 `PingAsync()` 示例，确认能收到 `ok=true` 的响应。
- 若要验证 `.NET SDK` 工作区自身的测试基线，可执行：

```powershell
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
```

- 若要验证本地发布包是否可生成，可执行：

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack
```

- 若要查看工作区构建、集成测试隔离或仓库级联调要求，请阅读 [`../../developer/guides/development.md`](../../developer/guides/development.md)。

## 9. 相关文档

- [`./README.md`](./README.md)
- [`../../../sdks/dotnet/README.md`](../../../sdks/dotnet/README.md)
- [`../host/quickstart.md`](../host/quickstart.md)
- [`../protocol/README.md`](../protocol/README.md)
- [`../../developer/guides/development.md`](../../developer/guides/development.md)
- [`../../developer/publishing/README.md`](../../developer/publishing/README.md)
