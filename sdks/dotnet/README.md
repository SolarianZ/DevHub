# DevHub .NET SDK

当前目录用于维护独立于主工程的 DevHub .NET SDK 工作区。

- 解决方案：`DevHub.DotNetSdk.slnx`
- SDK 项目：`src/DevHub.Sdk/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`

## 当前能力范围

当前 `.NET SDK` 已覆盖 `docs/Spec.md` 中属于 M5 `.NET SDK` 子范围的公开协议能力：

- Runtime discovery：读取并校验 `hub.json` / `token.txt`
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*`
- WebSocket Events：`hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe`、`hub.event`
- 统一错误模型：`DevHubRpcException`
- SDK 单元测试 + SDK↔Hub 黑盒集成测试

当前不包含：

- TS SDK
- `tests/conformance`
- 跨语言 CI 门禁

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

SDK 会按以下优先级解析运行时目录：

1. `DevHubClientOptions.RuntimeDir`
2. 环境变量 `DEVHUB_RUNTIME_DIR`
3. 平台默认目录

平台默认目录：

- Windows：`%LOCALAPPDATA%/DevHub/runtime/`
- macOS：`~/Library/Application Support/DevHub/runtime/`
- Linux：`$XDG_DATA_HOME/DevHub/runtime/`，若未设置则回退到 `~/.local/share/DevHub/runtime/`

SDK 始终以 `hub.json` 为权威端点来源，不会硬编码端口、HTTP 地址或 WebSocket URL。

## 快速开始

### 1. 创建 HTTP 客户端

```csharp
using DevHub.Sdk;

var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
});

var ping = await client.PingAsync(new { hello = "world" });
Console.WriteLine($"Ping ok={ping.Ok}, serverTimeUtc={ping.ServerTimeUtc:O}");

await client.DisposeAsync();
```

### 2. 使用环境变量覆盖运行时目录

```csharp
Environment.SetEnvironmentVariable("DEVHUB_RUNTIME_DIR", runtimeDir);

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "ExampleClient"
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

await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "EventsClient"
});

await eventsClient.AuthenticateAsync();
var subscriptionId = await eventsClient.SubscribeAsync(new[]
{
    "app.instance.registered",
    "invocation.completed"
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
    Console.WriteLine($"code={ex.Code}, message={ex.Message}, requestId={ex.RequestId}");
    Console.WriteLine(ex.Data?.GetRawText());
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
- `DevHubRpcException`
- `DevHub.Sdk.Models.*`

## 常用命令

```powershell
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```
