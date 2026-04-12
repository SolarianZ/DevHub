# DevHub .NET SDK 接入指南

本文面向准备通过官方 `.NET SDK` 连接 DevHub Host 的调用方，覆盖环境准备、连接 Host、最小示例和验证方式。

## 1. 前置条件

- 已按 [`../getting-started/host-quickstart.md`](../getting-started/host-quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备 `.NET SDK 8` 或 `.NET SDK 10`，以便构建和运行消费端程序。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../operations/publishing/README.md`](../../operations/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

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

再在消费项目中引用输出目录里的 `.nupkg`。正式发布后的主包 `PackageId` 为 `DevHub.Sdk.DotNet`，生成的资产文件名会与 [`../../operations/publishing/release-asset-layout.md`](../../operations/publishing/release-asset-layout.md) 保持一致。

## 3. 连接 Host

`.NET SDK` 会按以下优先级解析数据根目录：

1. `DevHubClientOptions.DataDir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据根目录

SDK 固定从 `<dataDir>/runtime/hub.json` 读取发现文件，并继续通过 `hub.json.tokenFile` 读取令牌。

最小示例：

```csharp
using DevHub.Sdk;

await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
{
    ClientId = "quickstart-dotnet"
});

var ping = await client.PingAsync(new { echo = "world" });
Console.WriteLine($"ok={ping.Ok}, serverTimeUtc={ping.ServerTimeUtc:O}");
```

## 4. 最小验证方式

- 直接运行上面的 `PingAsync()` 示例，确认能收到 `ok=true` 的响应。
- 若要验证 SDK 工作区自身的测试基线，可执行：

```powershell
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
```

- 若要验证本地发布包是否可生成，可执行：

```powershell
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

## 5. 后续路径

- 需要完整 API、扩展点和错误模型时，请阅读 [`../../../sdks/dotnet/README.md`](../../../sdks/dotnet/README.md)。
- 需要与其他语言 SDK 对照时，请回到 [`README.md`](./README.md)。
- 如果你希望直接基于协议接入，请切换到 [`../无SDK接入指南.md`](../无SDK接入指南.md)。
