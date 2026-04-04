# dotnet-sdk-di-extension-package Specification

## Purpose
Define the optional dependency injection companion package that lets DevHub SDK users keep the main `DevHub.Sdk` package free of DI-only dependencies while preserving the existing `AddDevHubSdk()` surface.

## Requirements

### Requirement: Optional dependency injection companion package
系统 SHALL 将 `AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory` 及其默认实现发布在独立的 `DevHub.Sdk.DependencyInjection` 包中，而不是继续放在 `DevHub.Sdk` 主包内。

#### Scenario: Companion package publishes DI entry points
- **WHEN** 维护者打包 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj`
- **THEN** 生成的包包含承载 `AddDevHubSdk()`、`IDevHubClientFactory` 与 `IDevHubEventsClientFactory` 的程序集
- **THEN** 该包直接依赖 `DevHub.Sdk`
- **THEN** 该包直接依赖 `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options`

### Requirement: Dependency injection behavior remains equivalent after the split
消费方在引用 `DevHub.Sdk.DependencyInjection` 后 SHALL 继续获得与拆分前等价的服务注册与工厂创建行为。

#### Scenario: Configure delegate still produces configured clients
- **WHEN** 消费方引用 `DevHub.Sdk.DependencyInjection` 并调用 `AddDevHubSdk(options => ...)`
- **THEN** 服务容器可以解析 `IDevHubClientFactory` 与 `IDevHubEventsClientFactory`
- **THEN** 通过工厂创建的客户端使用传入的 `DevHubClientOptions` 配置值

#### Scenario: External options configuration is still honored
- **WHEN** 消费方调用 `AddDevHubSdk()`，并通过 `Configure<DevHubClientOptions>(...)` 在容器外部配置选项
- **THEN** 通过工厂创建的客户端继续使用外部配置后的 `DevHubClientOptions`
