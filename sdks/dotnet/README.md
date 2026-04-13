# DevHub .NET SDK

本目录用于维护独立于主工程的 DevHub .NET SDK 工作区。

## 接入导航

- [`../../docs/guides/sdk/dotnet.md`](../../docs/guides/sdk/dotnet.md)：面向外部调用方的 `.NET SDK` 接入指南。
- [`../../docs/guides/getting-started/host-quickstart.md`](../../docs/guides/getting-started/host-quickstart.md)：启动 Host、读取 `hub.json` 和 `tokenFile` 的入口。
- [`../../docs/guides/无SDK接入指南.md`](../../docs/guides/无SDK接入指南.md)：不依赖官方 SDK 的原始协议路径。

- 解决方案：`DevHub.DotNetSdk.slnx`
- SDK 项目：`src/DevHub.Sdk/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`

## 能力范围

`.NET SDK` 对外提供以下协议能力：

- Runtime discovery：读取并校验 `hub.json` / `token.txt`
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`（含 `validateDefinition` / `upsertDefinition` / `deleteDefinition` 与带顶层 `password` 的实例注册 / 注销）、`hub.invoke.*`
- WebSocket Events：`hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe`、`hub.event`
- 公开扩展点：`runtime resolver`、按客户端粒度提供 `HttpClient` 的窄 seam、默认命名 `HttpClient` 管道
- 闭集事件类型模型：`DevHubEventType` / `DevHubEventTypes`（含 `AppDefinitionUpserted` / `AppDefinitionDeleted`）
- 统一错误模型：`DevHubRpcException`（协议要求 `error.data` 为对象；非对象响应会被视为非法 JSON-RPC 包）
- 协议辅助常量与结构化错误：`DevHubRpcException.CalleeError`
- 依赖注入工厂：`AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory`
- SDK 单元测试 + SDK↔Hub 黑盒集成测试

## 文档边界

本文档只覆盖 `.NET SDK` 本身的公开能力、用法与验证命令。

仓库级 `host/tests/conformance` 与跨语言 CI 门禁属于仓库整体测试与工程规划，不属于 `.NET SDK` 的公开 API 范围。

## 仓库内使用方式

本节命令仅用于仓库内开发或本地打包验证，不代表正式发布后的安装入口。

### 方式一：项目引用

```xml
<ProjectReference Include="..\..\sdks\dotnet\src\DevHub.Sdk\DevHub.Sdk.csproj" />
```

### 方式二：本地打包后引用

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

然后在消费项目中引用生成的本地包。若用于本地打包验证，请以实际生成的 `.nupkg` 文件名为准，不要从文档复制固定版本号：

```xml
<!-- TODO(devhub-release): 正式发布资产可用后，用该资产中的 SDK 版本替换 TODO-FIRST-RELEASE-VERSION。当前不要填写未生成的版本号。 -->
<PackageReference Include="DevHub.Sdk.DotNet" Version="TODO-FIRST-RELEASE-VERSION" />
```

## 发布资产占位

当 `.NET SDK` 的正式安装资产尚未生成时，公开安装说明统一使用以下占位写法：

```text
TODO(devhub-release): 正式发布资产可用后，在此补充 DevHub .NET SDK 的发布资产名称、版本号与安装命令；当前不要填写未生成的版本号、下载链接或仓库外安装命令。
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
services.AddHttpClient(DevHubServiceCollectionExtensions.DefaultHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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
        HttpClientProvider = httpClientProvider
    });

var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
    new DevHubClientOptions
    {
        ClientId = "EventsClient",
        DataDir = dataDir
    },
    new DevHubEventsClientDependencies
    {
        RuntimeResolver = runtimeResolver
    });
```

`HttpClientProvider` 只负责为当前 `DevHubClient` 提供底层 `HttpClient`，JSON-RPC 请求封装、错误映射与响应校验继续由 SDK 内部负责。低层 `JsonRpcHttpTransport` / `JsonRpcWebSocketSession` 已收敛为内部实现，不再作为稳定公开契约。

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

### 校验与写入定义

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "DefinitionAdmin"
});

var definition = new AppDefinition
{
    AppId = "sample.app",
    DisplayName = "Sample App",
    Launch = new LaunchConfiguration
    {
        ExePath = "python3",
        ArgsTemplate = "app.py"
    }
};

var validation = await client.ValidateDefinitionAsync(definition);
if (!validation.Valid)
{
    foreach (var issue in validation.Errors)
    {
        Console.WriteLine($"{issue.Path} {issue.Code}: {issue.Message}");
    }
}
else
{
    var upserted = await client.UpsertDefinitionAsync(definition);
    await client.DeleteDefinitionAsync(upserted.AppId);
}
```

### 注册实例并维持心跳

实例密码作为独立参数传入，不属于 `AppInstanceRegistration`，也不会出现在 `AppInstance` 或事件载荷中。

```csharp
using DevHub.Sdk;
using DevHub.Sdk.Models;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "WorkerClient"
});

const string instancePassword = "sample-instance-secret";

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
}, instancePassword);

var lastSeenUtc = await client.HeartbeatAsync(instance.InstanceId);

await client.UnregisterInstanceAsync(instance.InstanceId, instancePassword);
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
- `IDevHubHttpClientProvider`
- `IDevHubClientFactory` / `IDevHubEventsClientFactory`
- `DevHubServiceCollectionExtensions.DefaultHttpClientName`
- `DevHubRpcException`
- `DevHubRpcErrorCode`
- `DevHubEventType` / `DevHubEventTypes`
- `DevHub.Sdk.Models.*`

## 集成测试隔离模式

`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 运行的 `.NET SDK` 集成测试会先准备独立临时 Host 运行副本，再自行启动该临时 Host，并为每个测试用例分配独立临时 `DEVHUB_DATA_DIR`。

这些测试不会复用开发机默认数据目录下的常驻 Hub；测试结束后会关闭自己启动的 Host、回收 Host 进程树，并清理对应临时目录。

未提供预构建 Host 程序时，测试夹具会把 Host 构建到自己的隔离输出目录，再从该隔离产物启动临时 Host；默认行为不把源码树下 `host/src/DevHub.Host/bin/...` 视为运行时输入。

如果需要关闭这一步默认构建，或希望与 `JavaScript / Python SDK` 集成测试统一复用同一份 Host 产物，请优先设置共享环境变量 `DEVHUB_SDK_HOST_ASSEMBLY` 指向固定的 `DevHub.Host.dll`。如需只覆盖 `.NET SDK`，也可以改用 `DEVHUB_DOTNET_SDK_HOST_ASSEMBLY`；当两者同时存在时，`.NET` 专用变量优先。

仓库级 smoke、手工联调或示例运行可以连接本机已启动的 Hub，但那属于另一种运行方式，不等同于 SDK 集成测试模式。

## 常用命令

```powershell
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```
