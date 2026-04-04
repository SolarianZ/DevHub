# dotnet-sdk-unity-compatibility Specification

## Purpose
TBD - created by archiving change adapt-dotnet-sdk-for-unity. Update Purpose after archive.
## Requirements
### Requirement: Unity branch package target
在该 Unity 适配分支中发布的 `DevHub.Sdk` 包 SHALL 仅提供 `netstandard2.0` 目标资产，不得再发布 `net8.0` 或 `net10.0` 目标资产。

#### Scenario: Pack output exposes only netstandard2.0 assets
- **WHEN** 维护者对 `sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj` 执行发布或打包
- **THEN** 生成的 `DevHub.Sdk` 包只包含 `lib/netstandard2.0/` 目标资产
- **THEN** 包元数据中不再声明 `lib/net8.0/` 或 `lib/net10.0/` 目标资产

### Requirement: Newtonsoft-based JSON contract
`DevHub.Sdk` SHALL 使用 `Newtonsoft.Json 9.0.1` 作为 JSON 序列化与反序列化实现，并且公开 JSON 相关 API 不得再要求消费方引用 `System.Text.Json`。

#### Scenario: Published package no longer requires System.Text.Json
- **WHEN** 消费方引用该 Unity 分支发布的 `DevHub.Sdk` 包
- **THEN** 包依赖图中包含 `Newtonsoft.Json` 版本 `9.0.1`
- **THEN** 包依赖图中不包含 `System.Text.Json`
- **THEN** SDK 的公开 JSON 载荷类型与序列化特性均基于 `Newtonsoft.Json` 体系

### Requirement: Package dependency pruning
`DevHub.Sdk` 发布产物 SHALL 只声明对核心 SDK 能力有直接支撑作用的外部依赖；依赖注入相关依赖不得继续出现在主包依赖图中，而应由独立的可选 companion package 承载。

#### Scenario: DI-only dependencies are removed from the core package
- **WHEN** 维护者审计 `DevHub.Sdk` 项目引用并重新打包
- **THEN** 没有源代码使用的外部依赖不会继续保留在 `DevHub.Sdk.csproj`
- **THEN** `Microsoft.Extensions.Http` 与 `Microsoft.Extensions.Logging.Abstractions` 不再出现在 `DevHub.Sdk` 最终包依赖图中
- **THEN** `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options` 也不再出现在 `DevHub.Sdk` 最终包依赖图中
- **THEN** 若消费方需要 `AddDevHubSdk()`、`IDevHubClientFactory` 或 `IDevHubEventsClientFactory`，则通过独立的 `DevHub.Sdk.DependencyInjection` 包获得这些依赖
- **THEN** `DevHub.Sdk` 保留下来的每个外部依赖都能直接追溯到当前仍保留的核心 SDK 公开能力
