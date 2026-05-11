# DevHub .NET SDK 接入指南

本文面向准备通过官方 `.NET SDK` 连接 DevHub Host 的调用方，覆盖环境准备、运行时发现、常见调用方式、扩展点与最小验证方式。

## 1. 前置条件

- 已按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备可运行消费端程序的 `.NET SDK`；如需在仓库内联调或运行 SDK 工作区测试，使用 `.NET SDK 10`。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

### 2.1 项目引用

仓库内联调时，可直接引用 SDK 项目：

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

然后在消费项目中引用输出目录里的 `.nupkg`。正式发布后的主包 `PackageId` 为 `DevHub.Sdk.DotNet`；若需要 `AddDevHubSdk()`、`IDevHubClientFactory` 或 `IDevHubEventsClientFactory`，再额外引用 `DevHub.Sdk.DotNet.DependencyInjection`。生成的资产文件名会与 [`../../developer/publishing/release-asset-layout.md`](../../developer/publishing/release-asset-layout.md) 保持一致。

如需生成与仓库发布流程同结构的本地 SDK 资产目录，可执行：

```powershell
python scripts/release/package_dotnet_sdk.py --release-id dotnet-local-check
```

该命令会把主包与 DI companion package 输出到 `artifacts/sdk/dotnet/<release-id>/sdk/dotnet/`，并生成同级验证摘要。Unity DLL 输出路径仍由 `python3 scripts/sdk/publish_unity_dotnet_sdk.py` 单独负责。

### 2.3 Unity 场景

Unity 工程统一通过 `python3 scripts/sdk/publish_unity_dotnet_sdk.py` 生成 DLL，并引用脚本输出目录中的 DLL；不要直接引用 SDK `.csproj`，也不要把 `dotnet pack` 生成的 `.nupkg` 作为 Unity 接入入口。脚本参数与输出说明见 [`../../../sdks/dotnet/README.md`](../../../sdks/dotnet/README.md)。

## 3. 能力概览

- Runtime discovery：读取并校验 `hub.json` / `token.txt`，接受 `localhost`、IPv4 loopback 与 IPv6 loopback 端点。
- HTTP JSON-RPC：覆盖 `hub.ping`、`hub.apps.*` 与 `hub.invoke.*`。
- WebSocket Events：覆盖 `hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe` 与 `hub.event`。
- 版本查询与兼容性检查：`GetHostVersionAsync(...)` 与 `CheckVersionCompatibilityAsync(...)`。
- 注册实例结果：`RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`，分离 `AppInstance` 快照与 `InstanceSessionToken`。
- 事件恢复与本地维护：单活动读取器、已放弃请求计数/清理，以及订阅结果未知时的会话重建约束。
- 公开扩展点：`runtime resolver`、`HTTP transport factory`、`WebSocket session factory`、依赖注入工厂。
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`。
- 统一错误模型：`DevHubRpcException` 通过 `Data` / `ErrorData` 暴露 `error.data`；协议允许 `error.data` 为对象或 JSON `null`，其他 JSON 类型会被视为非法 JSON-RPC 包。
- 请求标识边界：SDK 默认生成 string 形式的 JSON-RPC `id`；若收到 numeric `id`，仅 `Int64` 范围内整数会被视为合法响应标识。

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
`hub.json.httpBaseUrl` 仅接受 loopback 主机、根路径 `/`，且不能包含尾随斜杠、userinfo、query 或 fragment；`hub.json.wsUrl` 仅接受 loopback 主机、固定路径 `/ws`，且同样不能包含上述附加成分。
`hub.json.runtimeTuning` 公开视图固定包含 `leaseSeconds`、`onlineThresholdSeconds`、`launchDedupeWindowSeconds` 与 `launchRegisterTimeoutSeconds`；缺失任一字段都会导致运行时发现失败。

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

var definitions = await client.ListDefinitionsAsync();
var globalDefinition = await client.GetDefinitionAsync("sample.app");
var scopedDefinitions = await client.ListDefinitionsAsync(new ListDefinitionsRequest
{
    AppId = "sample.app",
    Scope = "team-a"
});
var allInstances = await client.ListInstancesAsync(new ListInstancesRequest
{
    IncludeOffline = true
});
```

- `GetDefinitionAsync("sample.app")` 与 `GetDefinitionAsync("sample.app", string.Empty)` 都读取 Global Definition。
- `ListDefinitionsRequest.Scope` 与 `ListInstancesRequest.Scope` 中，`null` 表示不按作用域过滤，`string.Empty` 表示只匹配 Global 作用域。
- `AppDefinition.Scope`、`AppInstanceRegistration.Scope`、`LaunchRequest.Scope` 与 `InvocationTarget.Scope` 使用 `string.Empty` 表示 Global；未显式赋值时会归一化为 `string.Empty`。

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
    Scope = string.Empty,
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

### 6.3 注册实例与实例凭据复用

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

var lastSeenUtc = await client.HeartbeatAsync(registered.InstanceId);
var polled = await client.PollAsync(new PollRequest
{
    InstanceId = registered.InstanceId
});
```

- `RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`，其中 `Instance` 为可序列化的 `AppInstance` 快照，`InstanceSessionToken` 为实例所有权凭据。
- Host 启动的 App 可从环境变量 `DEVHUB_LAUNCH_ID` 读取启动请求标识，并调用 `RegisterInstanceAsync(instance, password, launchId)` 将该值作为顶层 `launchId` 传回 Host；自主注册可省略该参数。
- `password` 只作为 `RegisterInstanceAsync(..., password)` 的独立参数出现；`AppInstanceRegistration`、`AppInstance`、`GetInstanceAsync(...)`、`ListInstancesAsync(...)` 与 `app.instance.*` 事件载荷都不包含 `password` 或 `instanceSessionToken`。
- 在同一个 `DevHubClient` 实例内，`HeartbeatAsync(instanceId)`、`PollAsync(...)` 与 `RespondAsync(...)` 可省略 `InstanceSessionToken`，复用该客户端先前注册时缓存的会话令牌；`RespondAsync(...)` 还可复用先前 `PollAsync(...)` 缓存的 `LeaseToken`。
- `UnregisterInstanceAsync(instanceId, credential)` 接受 `InstanceSessionToken`；若使用同一 `DevHubClient` 注册实例，也可传入当时使用的 `password`，SDK 会在本地换算为对应的 `InstanceSessionToken`。

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
- `DevHubEventsClient.DisposeAsync()` 与底层 WebSocket 断开流程采用 best-effort 清理；关闭握手最多等待 1 秒，超时后直接继续释放本地资源。
- `DevHubClientOptions.RequestTimeout` 只用于请求-响应等待阶段；关闭或释放客户端时的清理上限由 SDK 内部固定控制，不形成可无限阻塞的关闭契约。

### 6.5 已放弃请求维护

`DevHubEventsClient` 暴露两组纯本地维护接口：

```csharp
using System;
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

await eventsClient.UnsubscribeAsync(subscriptionId);
```

- `GetAbandonedRequestCount(...)` 返回当前匹配过滤条件的已放弃请求数量。
- `ClearAbandonedRequests(...)` 只移除匹配条件的本地记录，并返回本次实际移除数量。
- `AbandonedRequestFilter` 支持 `OlderThan`、`AppId`、`Method` 三个可选条件；同时提供多个条件时按逻辑与匹配。
- 两个接口都只读取或修改当前 `DevHubEventsClient` 关联 WebSocket 会话中的本地 tombstone 记录，不发送 JSON-RPC 请求，也不会隐式重连。

## 7. 高级扩展

默认情况下，推荐使用 `DevHubClient.FromRuntimeAsync(...)` 与 `DevHubEventsClient.FromRuntimeAsync(...)`。

如果需要接入自定义运行时发现、HTTP transport 或 WebSocket session，可以通过公开扩展点注入：

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
        TransportFactory = transportFactory
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
        SessionFactory = sessionFactory
    });
```

`IDevHubHttpTransportFactory` 与 `IDevHubWebSocketSessionFactory` 负责承接底层通信；JSON-RPC 请求封装、错误映射与响应校验仍由 SDK 内部负责。低层具体实现类型不构成稳定公开契约。
如需按客户端粒度提供底层 `HttpClient`，可组合 `JsonRpcHttpTransportFactory(IDevHubHttpClientProvider)`，或在依赖注入场景替换 `IDevHubHttpClientProvider` 的实现。

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
