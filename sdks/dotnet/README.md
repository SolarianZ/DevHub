# DevHub .NET SDK

**当前分支专为Unity项目调整了 .NET SDK 。若要使用通用 .NET SDK ，请查看 [main分支](https://github.com/SolarianZ/DevHub/tree/main) 。**

当前目录用于维护独立于主工程的 DevHub .NET SDK 工作区。

- 解决方案：`DevHub.DotNetSdk.slnx`
- 核心 SDK 项目：`src/DevHub.Sdk/`
- 可选 DI companion package：`src/DevHub.Sdk.DependencyInjection/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`
- conformance 适配器项目：`tests/DevHub.Sdk.ConformanceAdapter/`

## 当前能力范围

当前 `.NET SDK` 已覆盖 `docs/spec/Spec.md` 中当前已实现的公开协议能力：

- Runtime discovery：读取并校验 `hub.json` / `token.txt`
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*`
- WebSocket Events：`hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe`、`hub.event`
- 公开扩展点：`runtime resolver`、`HTTP transport`、`WS session`
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`
- 统一错误模型：`DevHubRpcException`（协议要求 `error.data` 为对象；非对象响应会被视为非法 JSON-RPC 包）
- 协议辅助常量与结构化错误：`DevHubRpcException.CalleeError`
- 可选依赖注入 companion package：`AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory`
- SDK 单元测试 + SDK↔Hub 黑盒集成测试 + conformance 适配器

## 文档边界

本文档只覆盖 `.NET SDK` 本身的公开能力、用法与验证命令。

仓库级 `host/tests/conformance` 与跨语言 CI 门禁属于仓库整体测试与工程规划，不属于 `.NET SDK` 的公开 API 范围。
工作区中的 `tests/DevHub.Sdk.ConformanceAdapter/` 仅用于对接仓库级 conformance runner，不构成面向消费方的公开 API。

## 安装方式

按消费方式选择引用边界：

### 核心 SDK：直接创建客户端

```xml
<ProjectReference Include="..\..\sdks\dotnet\src\DevHub.Sdk\DevHub.Sdk.csproj" />
```

```xml
<PackageReference Include="DevHub.Sdk" Version="1.0.0" />
```

### 可选 DI companion package：`IServiceCollection` 集成

若消费方需要 `AddDevHubSdk()`、`IDevHubClientFactory` 或 `IDevHubEventsClientFactory`，请引用 `DevHub.Sdk.DependencyInjection`。

项目引用方式：

```xml
<ProjectReference Include="..\..\sdks\dotnet\src\DevHub.Sdk.DependencyInjection\DevHub.Sdk.DependencyInjection.csproj" />
```

NuGet 引用方式：

```xml
<PackageReference Include="DevHub.Sdk.DependencyInjection" Version="1.0.0" />
```

### 本地打包

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack
```

生成本地包后，按上面的核心 SDK 或 DI companion package 边界引用对应包即可。`DevHub.Sdk.DependencyInjection` 会直接依赖 `DevHub.Sdk`。

## Unity 2019.4 适配说明

当前分支发布的 `DevHub.Sdk` 与 `DevHub.Sdk.DependencyInjection` 均仅包含 `netstandard2.0` 目标资产，用于匹配 Unity 2019.4 可稳定消费的程序集基线。

SDK 的公开 JSON 类型面已经切换到 `Newtonsoft.Json 9.0.1`：

- `PingResult.Echo`、`RequestResult.Value`、`Invocation.Args`、`DevHubEvent.Payload`、`DevHubRpcException.ErrorData` 等公开载荷现在使用 `JToken` / `JObject`
- 旧版基于 `System.Text.Json` 的 `JsonElement`、`JsonDocument`、`GetRawText()` 与对应特性不再属于当前 Unity 分支的公开契约
- 若消费端需要读取载荷字段，推荐使用 `JObject` / `JToken` 的属性访问与 `Value<T>()` 系列 API

## SDK 包依赖边界

`DevHub.Sdk` 主包仅保留与核心 SDK 能力直接对应的外部依赖：

- `Newtonsoft.Json 9.0.1`：用于 runtime discovery、HTTP JSON-RPC、WebSocket 会话、公开模型标注与 `JToken` / `JObject` 载荷访问
- `Microsoft.Bcl.AsyncInterfaces`：为 `DevHubEventsClient.ReadEventsAsync()` 等 `IAsyncEnumerable<T>` / `IAsyncDisposable` 能力提供 `netstandard2.0` 兼容支持
- `System.Threading.Channels`：支撑事件客户端内部的异步事件缓冲与消费队列

`DevHub.Sdk.DependencyInjection` 可选包承载容器集成入口，并直接依赖：

- `DevHub.Sdk 1.0.0`
- `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.2`
- `Microsoft.Extensions.Options 10.0.2`

当前 `DevHub.Sdk` 主包发布产物不包含以下依赖：

- `System.Text.Json`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Options`

## Runtime Discovery

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

SDK 固定从 `<dataDir>/runtime/hub.json` 读取发现文件，并继续通过 `hub.json.tokenFile` 读取令牌。
若误传 `runtime` 子目录，SDK 会直接拒绝该路径并要求传入数据根目录。
SDK 始终以 `hub.json` 为权威端点来源，不会硬编码端口、HTTP 地址或 WebSocket URL。

## 快速开始

### 1. 创建 HTTP 客户端

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});

var ping = await client.PingAsync(new { hello = "world" });
Console.WriteLine($"Ping ok={ping.Ok}, serverTimeUtc={ping.ServerTimeUtc:O}");

await client.DisposeAsync();
```

### 2. 使用环境变量覆盖数据根目录

```csharp
Environment.SetEnvironmentVariable("DEVHUB_DATA_DIR", dataDir);

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});
```

### 3. 使用依赖注入工厂

先引用 `DevHub.Sdk.DependencyInjection`，类型命名空间仍保持为 `DevHub.Sdk`：

```csharp
using DevHub.Sdk;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddDevHubSdk(options =>
{
    options.ClientId = "ExampleClient";
    options.DataDir = dataDir;
});

using var serviceProvider = services.BuildServiceProvider();
var clientFactory = serviceProvider.GetRequiredService<IDevHubClientFactory>();
var eventsClientFactory = serviceProvider.GetRequiredService<IDevHubEventsClientFactory>();

await using var client = await clientFactory.CreateAsync();
await using var eventsClient = await eventsClientFactory.CreateAsync();
```

### 4. 使用公开扩展点

```csharp
using DevHub.Sdk;

var client = await DevHubClient.FromRuntimeAsync(
    new DevHubClientOptions
    {
        ClientId = "ExampleClient",
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
        ClientId = "EventsClient",
        DataDir = dataDir
    },
    new DevHubEventsClientDependencies
    {
        RuntimeResolver = runtimeResolver,
        SessionFactory = sessionFactory
    });
```

## HTTP 用法示例

### 查询定义与实例

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});

var definitions = await client.ListDefinitionsAsync();
var definition = await client.GetDefinitionAsync("sample.app");

var instances = await client.ListInstancesAsync(new ListInstancesRequest
{
    AppId = "sample.app",
    IncludeOffline = true
});
```

### 注册实例并维持心跳

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "WorkerClient"
});

var instance = await client.RegisterInstanceAsync(new AppInstanceRegistration
{
    InstanceId = "sample-inst-1",
    AppId = "sample.app",
    Pid = Environment.ProcessId,
    Invoke = new InvokeCapability
    {
        Poll = true,
        Respond = true
    },
    Meta = new { role = "worker" }
});

var lastSeenUtc = await client.HeartbeatAsync(instance.InstanceId);

await client.UnregisterInstanceAsync(instance.InstanceId);
```

### 发起通知与请求

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "CallerClient"
});

var notifyResult = await client.NotifyAsync(new InvokeRequest
{
    AppId = "sample.app",
    Method = "sample.notify",
    Args = new { value = 1 }
});

var requestResult = await client.RequestAsync(new InvokeRequest
{
    AppId = "sample.app",
    Method = "sample.request",
    Args = new { name = "DevHub" },
    Options = new InvocationOptions
    {
        TtlMs = 30_000,
        WaitTimeoutMs = 5_000
    }
});

Console.WriteLine(requestResult.Value?.ToString(Newtonsoft.Json.Formatting.None));
```

### 认领调用并响应

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "CalleeClient"
});

var poll = await client.PollAsync(new PollRequest
{
    InstanceId = "sample-inst-1",
    MaxCount = 10,
    WaitMs = 5_000
});

foreach (var item in poll.Items)
{
    await client.RespondAsync(new RespondRequest
    {
        InstanceId = "sample-inst-1",
        InvocationId = item.InvocationId,
        Value = new { ok = true }
    });
}
```

## WebSocket 事件流示例

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "EventsClient"
});

await eventsClient.AuthenticateAsync();
var subscriptionId = await eventsClient.SubscribeAsync(new[]
{
    DevHubEventTypes.AppInstanceRegistered,
    DevHubEventTypes.InvocationCompleted
});

await foreach (var evt in eventsClient.ReadEventsAsync())
{
    Console.WriteLine($"{evt.TimeUtc:O} {evt.Type}: {evt.Payload?.ToString(Newtonsoft.Json.Formatting.None)}");
}

await eventsClient.UnsubscribeAsync(subscriptionId);
```

## 错误处理

协议错误统一映射为 `DevHubRpcException`：

```csharp
try
{
    await client.GetDefinitionAsync("missing.app");
}
catch (DevHubRpcException ex)
{
    Console.WriteLine($"code={ex.Code}, knownCode={ex.KnownCode}, reason={ex.Reason}, requestId={ex.RequestId}");
    Console.WriteLine(ex.ErrorData?.ToString(Newtonsoft.Json.Formatting.None));

    if (ex.CalleeError is { } calleeError)
    {
        Console.WriteLine($"invocationId={ex.InvocationId}, calleeCode={calleeError.Code}, calleeMessage={calleeError.Message}");
    }
}
```

非协议层错误会抛出普通 .NET 异常，例如：

- `ArgumentException`：调用参数非法
- `InvalidOperationException`：发现文件、令牌文件或成功载荷非法
- `OperationCanceledException`：请求超时或外部取消

## 公开 API

### `DevHub.Sdk` 主包

- `DevHubClientOptions`
- `DevHubClient`
- `DevHubEventsClient`
- `DevHubClientDependencies` / `DevHubEventsClientDependencies`
- `IDevHubRuntimeResolver`
- `IDevHubHttpTransport` / `IDevHubHttpTransportFactory`
- `IDevHubWebSocketSession` / `IDevHubWebSocketSessionFactory`
- `DevHubRpcException`
- `DevHubRpcErrorCode`
- `DevHubEventType` / `DevHubEventTypes`
- `DevHub.Sdk.Models.*`

### `DevHub.Sdk.DependencyInjection` 可选包

- `DevHubServiceCollectionExtensions.AddDevHubSdk()`
- `IDevHubClientFactory` / `IDevHubEventsClientFactory`

## 集成测试隔离模式

`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 运行的 `.NET SDK` 集成测试会先准备独立临时 Host 运行副本，再自行启动该临时 Host，并为每个测试用例分配独立临时 `DEVHUB_DATA_DIR`。

这些测试不会复用开发机默认数据目录下的常驻 Hub；测试结束后会关闭自己启动的 Host、回收 Host 进程树，并清理对应临时目录。

默认情况下，测试会优先复用已存在的 `host/src/DevHub.Host/bin/...` 构建输出并复制到临时目录；若当前机器尚无可用输出，则仍可能先触发一次对 `host/src/DevHub.Host` 的构建。因此，“临时 Host + 独立数据根目录”只说明运行时状态彼此隔离，并不等同于默认无条件支持 `.NET / Python / JS` 三套 SDK 集成测试并行执行。

如果需要在同一台机器上并行跑多套 SDK 集成测试，请先串行准备好 Host 程序，再通过环境变量 `DEVHUB_DOTNET_SDK_HOST_ASSEMBLY` 指向固定的已构建 `DevHub.Host.dll`，避免多个测试进程同时触发对源码树下 Host 构建产物的竞争。

仓库级 smoke、手工联调或示例运行可以连接本机已启动的 Hub，但那属于另一种运行方式，不等同于 SDK 集成测试模式。

## 常用命令

```powershell
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack
```

## 发布产物验收基线

`dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack` 生成的 `DevHub.Sdk.1.0.0.nupkg` 包含以下发布资产：

- `lib/netstandard2.0/DevHub.Sdk.dll`
- `lib/netstandard2.0/DevHub.Sdk.xml`
- `README.md`

包 `.nuspec` 声明的直接依赖如下：

- `Newtonsoft.Json 9.0.1`
- `Microsoft.Bcl.AsyncInterfaces 1.1.0`
- `System.Threading.Channels 4.7.0`

`dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack` 生成的 `DevHub.Sdk.DependencyInjection.1.0.0.nupkg` 包含以下发布资产：

- `lib/netstandard2.0/DevHub.Sdk.DependencyInjection.dll`
- `lib/netstandard2.0/DevHub.Sdk.DependencyInjection.xml`
- `README.md`

其 `.nuspec` 声明的直接依赖如下：

- `DevHub.Sdk 1.0.0`
- `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.2`
- `Microsoft.Extensions.Options 10.0.2`

`DevHub.Sdk` 主包依赖图不包含以下项目：

- `System.Text.Json`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Options`

与 Unity 单目标适配相关的保留项与测试差异如下：

- `System.Threading.Channels` 与 `Microsoft.Bcl.AsyncInterfaces` 继续保留，用于事件流 API 的异步缓冲、`IAsyncEnumerable<T>` 与 `IAsyncDisposable`
- `DevHub.Sdk.DependencyInjection` 单独承载 `AddDevHubSdk()`、`IDevHubClientFactory` 与 `IDevHubEventsClientFactory`
- SDK 发布包仅面向 `netstandard2.0`，测试工程与 conformance adapter 继续使用 `net10.0` 以复用当前 Host 测试基线；这些测试项目不进入 NuGet 发布产物
- `Newtonsoft.Json 9.0.1` 在 restore/build/pack 期间会产生 `NU1903` 告警；当前分支按 Unity 适配要求固定该版本，验收以包结构、依赖边界与 SDK 行为为准
