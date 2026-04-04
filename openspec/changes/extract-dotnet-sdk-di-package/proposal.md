## Why

当前 Unity 适配分支中的 `DevHub.Sdk` 主包同时承载了运行时发现、HTTP/WebSocket 客户端与 `Microsoft.Extensions.DependencyInjection` 集成入口。即使消费方完全不使用 `AddDevHubSdk()` 或工厂接口，主包仍会因为 `Microsoft.Extensions.Options` -> `Microsoft.Extensions.Primitives` 的依赖链在类库发布产物里带出额外 DLL，这与“主包尽量只保留 Unity 消费所需依赖”的目标不一致。

当前已经确认这些 `Microsoft.Extensions.*` 依赖仅由 DI 扩展入口使用，因此需要把 DI 能力从主包拆分为可选包，让默认消费路径回到更小、更稳定的依赖闭包，同时保留 ASP.NET Core / Generic Host 场景的接入方式。

## What Changes

- 将 `DevHub.Sdk` 主包中的 `AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory` 及其默认实现迁移到独立的 `DevHub.Sdk.DependencyInjection` 包。 **BREAKING**
- 让 `DevHub.Sdk` 主包只保留不依赖 `Microsoft.Extensions.Options` / `Microsoft.Extensions.DependencyInjection.Abstractions` 的核心客户端、扩展 seam 与模型类型。
- 为新的 `DevHub.Sdk.DependencyInjection` 包建立对 `DevHub.Sdk` 与 `Microsoft.Extensions.*` 的最小直接依赖，并保持现有 DI 使用方式尽可能平滑迁移。
- 更新 `.NET SDK` 工作区的打包说明、依赖边界说明与测试覆盖，使主包和可选 DI 包的职责清晰可追溯。

## Capabilities

### New Capabilities
- `dotnet-sdk-di-extension-package`: 定义 `DevHub.Sdk.DependencyInjection` 作为可选 companion package 的公开入口、依赖边界与迁移约束。

### Modified Capabilities
- `dotnet-sdk-unity-compatibility`: 调整 Unity 分支下 `DevHub.Sdk` 主包的依赖收敛要求，使 DI 相关依赖不再属于主包发布依赖图。

## Impact

- 受影响代码位于 `sdks/dotnet/src/DevHub.Sdk/`、`sdks/dotnet/src/DevHub.Sdk.DependencyInjection/`、`sdks/dotnet/Directory.Packages.props`、`sdks/dotnet/DevHub.DotNetSdk.slnx` 以及相关测试项目。
- 受影响公开 API 包括 `AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory` 的命名空间归属与引用方式；已使用 DI 的消费方需要额外引用新包。
- 受影响发布结果包括 `DevHub.Sdk` 主包的直接依赖图、类库 `publish` 产物、README 安装说明与主/副包的验收基线。
