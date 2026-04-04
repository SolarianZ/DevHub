## Context

当前 `DevHub.Sdk` 主包在 [DevHubServiceCollectionExtensions.cs](/Users/qiuyu/projects/DevHub/sdks/dotnet/src/DevHub.Sdk/DevHubServiceCollectionExtensions.cs) 中直接承载 `AddDevHubSdk()`、`IDevHubClientFactory`、`IDevHubEventsClientFactory` 及默认工厂实现，因此 [DevHub.Sdk.csproj](/Users/qiuyu/projects/DevHub/sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj) 必须显式引用 `Microsoft.Extensions.DependencyInjection.Abstractions` 和 `Microsoft.Extensions.Options`。在当前 Unity 适配分支里，这会让主包即便被非 DI 消费场景引用，也持续携带 `Microsoft.Extensions.*` 的依赖链，与“主包只保留核心 SDK 能力所需依赖”的目标冲突。

仓库中已经存在 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/` 目录及历史构建残留，但当前 solution 仅包含 `DevHub.Sdk` 主项目，没有正式的 companion package 源码与打包入口。这说明“拆分 DI 包”与当前工作区结构兼容，但需要重新建立为受支持的正式项目。

本 change 仍受当前分支 `dotnet_sdk_for_unity` 的约束：范围限定在 `sdks/dotnet`，不能通过修改 `docs/spec/Spec.md` 或 Host 行为来规避问题；验收重点是 NuGet 包依赖边界、README 准确性和现有 DI 行为不回退。

## Goals / Non-Goals

**Goals:**
- 让 `DevHub.Sdk` 主包移除对 `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options` 的直接依赖。
- 将 DI 公开入口迁移到正式的 `DevHub.Sdk.DependencyInjection` companion package，并保持现有 DI 注册行为等价。
- 尽可能降低已使用 DI 的消费方迁移成本，优先保留现有类型名、方法名与命名空间习惯。
- 更新 solution、测试和文档，使主包与 companion package 的职责边界、安装方式和验收基线清晰一致。

**Non-Goals:**
- 不修改 Host、协议规范、运行时行为或其他语言 SDK。
- 不在本 change 中继续拆分事件流相关依赖或 `Microsoft.Bcl.AsyncInterfaces` / `System.Threading.Channels`。
- 不追求让“仍只引用主包但继续调用 DI API”的旧代码保持二进制兼容；本 change 接受这部分消费方需要增加新包引用。
- 不因为拆包而重新设计 SDK 的工厂语义、运行时发现机制或配置模型。

## Decisions

### 1. 建立正式的 `DevHub.Sdk.DependencyInjection` 包承载 DI API

新增正式项目 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj`，用于承载：
- `AddDevHubSdk()` 扩展方法；
- `IDevHubClientFactory` / `IDevHubEventsClientFactory`；
- 默认工厂实现。

该项目直接依赖 `DevHub.Sdk`、`Microsoft.Extensions.DependencyInjection.Abstractions` 和 `Microsoft.Extensions.Options`，并与主包共享版本、README、仓库元数据和 `netstandard2.0` 发布目标。

之所以使用独立 companion package，而不是条件编译或 `PrivateAssets`，是因为当前问题的根因在于“主包公开 API 形态”本身带来了依赖。只调整引用可见性无法让主包真正退出这条依赖链。

备选方案是保留单包结构，只在类库 `publish` 时人工裁掉 `System.Memory.dll` 等文件。这个方案被拒绝，因为 `.deps.json` 与包依赖图仍会声明 DI 依赖，属于不完整产物。

### 2. DI 类型继续使用 `DevHub.Sdk` 命名空间，迁移以“增包”为主

迁移后的 DI 类型仍保留在 `DevHub.Sdk` 命名空间下，以降低现有消费方的源代码改动量。对已使用 DI 的调用方，理想迁移路径应是“新增 `DevHub.Sdk.DependencyInjection` 包引用”，而不是同时修改 `using`、类型名和注册调用。

这意味着实现阶段应把当前 DI 代码从主包物理迁出到新项目，但保持命名空间不变。少量仅能依赖主包内部 `internal` 工具的逻辑，例如空值守卫，需要改写为独立可访问的实现，而不是通过 `InternalsVisibleTo` 重新制造跨程序集耦合。

备选方案是把 DI API 放入 `DevHub.Sdk.DependencyInjection` 命名空间。这个方案被拒绝，因为会让所有已存在的 DI 调用代码同时承受“加包 + 改命名空间”两层迁移成本。

### 3. 主包与 companion package 的职责边界以“核心能力 vs. 容器集成”划分

拆分后的职责边界如下：
- `DevHub.Sdk`：运行时发现、HTTP/WebSocket 客户端、公开模型、错误模型、可替换 seam（resolver / transport / session factory）。
- `DevHub.Sdk.DependencyInjection`：把上述核心类型接入 `IServiceCollection` / Options 模式所需的注册扩展与工厂包装。

主包不再直接提供 `AddDevHubSdk()` 或工厂接口，README 需要把“直接创建客户端”和“DI 集成”拆成两条安装路径。相应地，`dotnet pack` 的验收也要拆成两套：主包关注依赖收敛，companion package 关注 API 暴露和依赖声明完整性。

备选方案是把所有工厂接口继续留在主包，只迁走扩展方法。这个方案被拒绝，因为 `IDevHubClientFactory` / `IDevHubEventsClientFactory` 本身就是 DI 能力的一部分，继续留在主包会让依赖边界模糊。

### 4. 保持现有 DI 行为等价，测试以现有单测语义为基准迁移

当前 [ServiceCollectionExtensionsTests.cs](/Users/qiuyu/projects/DevHub/sdks/dotnet/tests/DevHub.Sdk.UnitTests/DependencyInjection/ServiceCollectionExtensionsTests.cs) 已覆盖两条关键行为：
- `AddDevHubSdk(options => ...)` 后可创建 HTTP / Events 客户端，且配置值生效；
- `AddDevHubSdk()` 配合外部 `Configure<DevHubClientOptions>(...)` 时，工厂继续读取最终 options。

实现阶段应保留这两条行为语义不变。测试层面优先采用“保留现有单测项目，新增对 companion package 的项目引用并迁移/更新 DI 相关测试”的最小方案，而不是再新建一套独立测试工程，以避免本次 change 把成本扩散到更多工作区结构调整。

备选方案是新建 `DevHub.Sdk.DependencyInjection.UnitTests`。这个方案不是不可行，但当前收益不足以覆盖额外的 solution 与 CI 维护成本。

## Risks / Trade-offs

- [已使用 DI 的消费方发生源级破坏] -> 通过保持 `DevHub.Sdk` 命名空间与现有 API 名称不变，把迁移压缩为“新增 companion package 引用并移除对主包内 DI API 的假设”。
- [新包与主包版本漂移] -> 通过共享 `VersionPrefix`、README、仓库元数据与 solution 内统一打包验证，降低双包不同步风险。
- [迁移过程中误用主包内部实现细节] -> 明确禁止让新项目依赖主包 `internal` 类型，必要守卫逻辑在新项目内独立实现。
- [文档误导导致非 DI 消费方继续安装 companion package] -> README 中显式区分“基础安装”和“ASP.NET Core / Generic Host DI 集成”两条入口，并把 companion package 标为可选。
- [旧残留目录/产物混淆正式源码结构] -> 实施时以新的 `.csproj` 和源码树为准，清理与正式项目不一致的残留构建产物。

## Migration Plan

1. 在 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/` 下建立正式项目文件和源码文件，将 DI 相关公开类型从主包迁移过去。
2. 从 `DevHub.Sdk.csproj` 中移除 `Microsoft.Extensions.DependencyInjection.Abstractions` 与 `Microsoft.Extensions.Options`，并从主包源码中删除 DI 入口定义。
3. 将 `DevHub.DotNetSdk.slnx` 与相关测试项目更新为同时包含主包与 companion package，迁移 DI 相关单测到新的引用边界下。
4. 更新 `sdks/dotnet/README.md`、打包说明和依赖边界说明，分别描述主包与 companion package 的安装/使用方式。
5. 以 `dotnet pack` 为核心验证主包和 companion package：确认主包不再声明 `Microsoft.Extensions.*` DI 依赖，companion package 正确声明对 `DevHub.Sdk` 与 `Microsoft.Extensions.*` 的依赖，并补跑最小相关 `dotnet test`。

回滚策略是整组回退：如果拆包后发现 API 迁移风险或测试回退不可接受，则同时回退主包移除、companion package 引入和文档改动，恢复现有单包布局。

## Open Questions

- companion package 是否需要单独的 `README` 展示内容，还是继续复用 `sdks/dotnet/README.md` 并在其中分节说明即可？
- 是否需要在实现阶段同步清理 `sdks/dotnet/src/DevHub.Sdk.DependencyInjection/` 下已有的历史 `bin/`、`obj/` 残留，以免影响对正式项目结构的判断？
