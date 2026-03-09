# DevHub .NET SDK

当前目录用于维护独立于主工程的 DevHub .NET SDK 解决方案。

- 解决方案：`DevHub.DotNetSdk.slnx`
- SDK 项目：`src/DevHub.Sdk/`
- 单元测试项目：`tests/DevHub.Sdk.UnitTests/`
- 集成测试项目：`tests/DevHub.Sdk.IntegrationTests/`

当前阶段仅完成解决方案、项目骨架与基础依赖初始化，尚未实现具体 SDK 功能。

常用命令：

- `dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
