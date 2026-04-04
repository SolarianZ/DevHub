## 1. Companion Package 建立

- [x] 1.1 在 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/` 下创建正式的 `DevHub.Sdk.DependencyInjection.csproj`，配置 `netstandard2.0`、包元数据、README 打包项以及对 `DevHub.Sdk`、`Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Options` 的引用。
- [x] 1.2 将 `sdks/dotnet/DevHub.DotNetSdk.slnx` 更新为包含 `DevHub.Sdk.DependencyInjection` 项目，并清理会干扰正式项目结构判断的历史残留构建产物。

## 2. 主包与 DI 源码拆分

- [x] 2.1 将 `AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory` 及默认工厂实现从 `DevHub.Sdk` 主包迁移到新项目，并保持 `DevHub.Sdk` 命名空间与现有 API 名称不变。
- [x] 2.2 从 `sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj` 移除 `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options`，同时删除主包中已不再保留的 DI 源码入口。
- [x] 2.3 校正迁移后对核心 SDK 公开类型的引用方式，避免新项目依赖主包 `internal` 实现细节，并确认 `AddDevHubSdk()` 的注册行为与当前一致。

## 3. 测试与文档同步

- [x] 3.1 更新 `sdks/dotnet/tests/DevHub.Sdk.UnitTests/` 对项目的引用边界，使现有 DI 相关单测在引用 companion package 后继续验证 `AddDevHubSdk(options => ...)` 与外部 `Configure<DevHubClientOptions>(...)` 两条行为。
- [x] 3.2 更新 `sdks/dotnet/README.md` 的安装、依赖边界与 DI 使用示例，明确区分 `DevHub.Sdk` 主包与 `DevHub.Sdk.DependencyInjection` 可选包的职责与引用方式。
- [x] 3.3 审计 `sdks/dotnet` 范围内其他与包边界相关的说明或打包文档，确保不再把 DI 入口描述为 `DevHub.Sdk` 主包内置能力。

## 4. 发布与验收

- [x] 4.1 执行 `dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack`，确认主包依赖图不再包含 `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options`。
- [x] 4.2 执行 `dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack`，确认 companion package 正确声明对 `DevHub.Sdk` 与 `Microsoft.Extensions.*` 的依赖，并暴露 DI 入口程序集。
- [x] 4.3 按当前 CI 最小相关范围执行 `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`，并在需要时补充对主/副包打包结果的依赖审计记录。
