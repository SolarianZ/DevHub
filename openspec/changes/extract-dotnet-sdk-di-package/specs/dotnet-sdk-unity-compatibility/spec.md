## MODIFIED Requirements

### Requirement: Package dependency pruning
`DevHub.Sdk` 发布产物 SHALL 只声明对核心 SDK 能力有直接支撑作用的外部依赖；依赖注入相关依赖不得继续出现在主包依赖图中，而应由独立的可选 companion package 承载。

#### Scenario: DI-only dependencies are removed from the core package
- **WHEN** 维护者审计 `DevHub.Sdk` 项目引用并重新打包
- **THEN** 没有源代码使用的外部依赖不会继续保留在 `DevHub.Sdk.csproj`
- **THEN** `Microsoft.Extensions.Http` 与 `Microsoft.Extensions.Logging.Abstractions` 不再出现在 `DevHub.Sdk` 最终包依赖图中
- **THEN** `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options` 也不再出现在 `DevHub.Sdk` 最终包依赖图中
- **THEN** 若消费方需要 `AddDevHubSdk()`、`IDevHubClientFactory` 或 `IDevHubEventsClientFactory`，则通过独立的 `DevHub.Sdk.DependencyInjection` 包获得这些依赖
- **THEN** `DevHub.Sdk` 保留下来的每个外部依赖都能直接追溯到当前仍保留的核心 SDK 公开能力
