# DevHub .NET SDK

当前目录用于维护独立于主工程的 DevHub .NET SDK 工作区。

- 解决方案：`DevHub.DotNetSdk.slnx`
- SDK 项目：`src/DevHub.Sdk/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`

## 当前能力范围

当前 `.NET SDK` 已覆盖 `docs/Spec.md` 中当前已实现的公开协议能力：

- Runtime discovery：读取并校验 `hub.json` / `token.txt`
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*`
- WebSocket Events：`hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe`、`hub.event`
- 公开扩展点：`runtime resolver`、`HTTP transport`、`WS session`
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`
- 统一错误模型：`DevHubRpcException`
- 协议辅助常量与结构化错误：`DevHubRpcException.CalleeError`
- 依赖注入工厂：`AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory`
- SDK 单元测试 + SDK↔Hub 黑盒集成测试

## 文档边界

本文档只覆盖 `.NET SDK` 本身的公开能力、用法与验证命令。

仓库级 `host/tests/conformance` 与跨语言 CI 门禁属于仓库整体测试与工程规划，不属于 `.NET SDK` 的公开 API 范围。

## 安装方式

当前仓库内建议通过以下两种方式消费：

### 方式一：项目引用

```xml
<ProjectReference Include="..\..\sdks\dotnet\src\DevHub.Sdk\DevHub.Sdk.csproj" />
```

### 方式二：本地打包后引用

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

然后在消费项目中引用生成的本地包：

```xml
<PackageReference Include="DevHub.Sdk" Version="1.0.0" />
```

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

Console.WriteLine(requestResult.Value?.GetRawText());
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
    Console.WriteLine($"{evt.TimeUtc:O} {evt.Type}: {evt.Payload?.GetRawText()}");
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
    Console.WriteLine(ex.ErrorData?.GetRawText());

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

- `DevHubClientOptions`
- `DevHubClient`
- `DevHubEventsClient`
- `DevHubClientDependencies` / `DevHubEventsClientDependencies`
- `IDevHubRuntimeResolver`
- `IDevHubHttpTransport` / `IDevHubHttpTransportFactory`
- `IDevHubWebSocketSession` / `IDevHubWebSocketSessionFactory`
- `IDevHubClientFactory` / `IDevHubEventsClientFactory`
- `DevHubRpcException`
- `DevHubRpcErrorCode`
- `DevHubEventType` / `DevHubEventTypes`
- `DevHub.Sdk.Models.*`

## 集成测试隔离模式

`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 运行的 `.NET SDK` 集成测试会自行启动独立临时 Host，并为每个测试用例分配独立临时 `DEVHUB_DATA_DIR`。

这些测试不会复用开发机默认数据目录下的常驻 Hub；测试结束后会关闭自己启动的 Host、回收 Host 进程树，并清理对应临时目录。

因此，同一台机器可以并行运行 `.NET / Python / JS` SDK 集成测试，因为每套测试都必须拥有自己的临时 Host 与独立数据根目录。

仓库级 smoke、手工联调或示例运行可以连接本机已启动的 Hub，但那属于另一种运行方式，不等同于 SDK 集成测试模式。

## 常用命令

```powershell
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```
