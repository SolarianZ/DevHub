# DevHub .NET SDK

**注意**：当前分支 .NET SDK 为适配 Unity 2019 而作了大量修改，若要使用通用版本 .NET SDK ，请查看 main 分支。当前分支中所有与 .NET SDK 相关的信息以此文档为准，其他文档是基于 main 分支通用版 .NET SDK 编写的，可能不适用于当前分支版本，仅供参考。

此目录包含 DevHub .NET SDK 的源代码、工作区配置和测试。详细用户接入说明请查看 [docs/user/sdk/dotnet.md](../../docs/user/sdk/dotnet.md) 。

## 当前分支要点

- `DevHubClient` 与 `DevHubEventsClient` 都提供 `GetHostVersionAsync(...)` 与 `CheckVersionCompatibilityAsync(...)`，用于直接查询 Host 版本并执行语义化兼容性判断。
- `RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`，分离 `AppInstance` 快照与 `InstanceSessionToken`；Host 启动场景可通过顶层 `launchId` 回传 `DEVHUB_LAUNCH_ID`。
- `DevHubEventsClient` 保持单活动读取器、订阅结果未知即废弃当前会话、已放弃请求本地维护与 bounded fail-fast 事件缓冲等恢复语义。
- runtime discovery 仅接受 loopback `httpBaseUrl` 与固定 `/ws` 的 `wsUrl`，并要求 `runtimeTuning.launchRegisterTimeoutSeconds` 等字段齐全。
- 当前公开扩展点保持 Unity 兼容基线：`RuntimeResolver`、`TransportFactory`、`SessionFactory`；依赖注入能力由 companion package 承载。
- `DevHubRpcException` 通过 `Data` / `ErrorData` 暴露对象或 JSON `null` 形态的 `error.data`；numeric JSON-RPC `id` 仅接受 `Int64` 范围内整数。

## 版本查询与兼容性检查

`DevHubClient` 与 `DevHubEventsClient` 都提供以下入口：

- `GetHostVersionAsync(...)`：调用 `hub.getVersion` 并返回 Host 的直接版本字符串。
- `CheckVersionCompatibilityAsync(...)`：优先调用 `hub.getVersion`，当 Host 返回 `method_not_found` 时回退到 `Runtime.HubVersion`，并返回 `VersionCompatibilityResult`。

`VersionCompatibilityResult.Status` 的判定规则如下：

- `Incompatible`：`Major` 不同。
- `UpdateRecommended`：`Major` 相同但 `Minor` 不同。
- `Compatible`：`Major` 与 `Minor` 相同；`Patch`、预发布标签和构建元数据差异不单独提示。
- `Unknown`：版本缺失，或无法解析为兼容检查所需的语义化版本格式。

事件客户端上的版本查询与兼容性检查继续复用已鉴权 WebSocket 只读通道，因此调用前需要先执行 `AuthenticateAsync(...)`。

## 注册结果与实例凭据

`DevHubClient.RegisterInstanceAsync(...)` 返回 `RegisterInstanceResult`：

- `Instance`：注册后的 `AppInstance` 快照。
- `InstanceSessionToken`：实例所有权凭据，用于后续 `HeartbeatAsync(...)`、`UnregisterInstanceAsync(...)`、`PollAsync(...)` 与 `RespondAsync(...)`。

`AppInstance`、`ListInstancesAsync(...)`、`GetInstanceAsync(...)` 与事件载荷都不包含 `instanceSessionToken`。实例快照可直接序列化或缓存，不会混入所有权凭据。

在同一个 `DevHubClient` 实例内，`HeartbeatAsync(instanceId)`、`PollAsync(...)` 与 `RespondAsync(...)` 可省略 `InstanceSessionToken`，复用先前注册时缓存的会话令牌；`RespondAsync(...)` 还可复用先前 `PollAsync(...)` 缓存的 `LeaseToken`。

`instanceId` 是同一 Hub 注册表内的全局实例身份，值必须满足公开标识符规范且长度不超过 256。复用同一 `instanceId` 执行注册时，`appId` 与 `scope` 必须保持一致；同一 `DevHubClient` 实例能够识别的身份漂移会在本地请求发送前失败。

## 事件客户端恢复语义

- 同一 `DevHubEventsClient` 实例会串行执行 `AuthenticateAsync(...)`，避免并发发送多条 `hub.ws.authenticate`。
- 同一时刻只允许一个活动中的 `ReadEventsAsync(...)` 读取器。
- `SubscribeAsync(...)` 或 `UnsubscribeAsync(...)` 在请求发出后若因超时或取消进入结果未知状态，SDK 会主动废弃当前 WebSocket 会话。
- 会话被废弃后，后续读取、订阅或只读 WS RPC 都需要先重新执行 `AuthenticateAsync(...)`，再重新执行 `SubscribeAsync(...)`。
- 本地事件缓冲采用有界 fail-fast 队列；消费者落后导致缓冲溢出时，当前事件流会终止，并要求重新认证与重新订阅。
- `GetAbandonedRequestCount(...)` 与 `ClearAbandonedRequests(...)` 只维护当前 WebSocket 会话上的本地 tombstone 记录，不发送额外 JSON-RPC 请求。

## 运行时发现边界

- 数据目录固定采用 `<dataDir>/runtime/hub.json` 与 `tokenFile` 布局，误把 `runtime` 子目录作为 `DataDir` 传入时会直接失败。
- `httpBaseUrl` 仅接受 loopback 主机、根路径 `/`，且不能包含尾随斜杠、userinfo、query 或 fragment。
- `wsUrl` 仅接受 loopback 主机、固定路径 `/ws`，且不能包含尾随斜杠、userinfo、query 或 fragment。
- `runtimeTuning` 必须同时包含 `leaseSeconds`、`onlineThresholdSeconds`、`launchDedupeWindowSeconds` 与 `launchRegisterTimeoutSeconds`。

## 内容结构

```text
sdks/dotnet/
├── DevHub.DotNetSdk.slnx                    # .NET SDK 工作区解决方案文件
├── Directory.Build.props                    # 工作区公共构建配置
├── Directory.Packages.props                 # 工作区统一依赖版本管理
├── src/                                     # SDK 源码
│   ├── DevHub.Sdk/                          # 核心 SDK 项目
│   └── DevHub.Sdk.DependencyInjection/      # 可选 DI companion package
├── tests/                                   # SDK 测试与测试配套项目
│   ├── DevHub.Sdk.UnitTests/                # 核心 SDK 单元测试
│   ├── DevHub.Sdk.DependencyInjection.UnitTests/ # DI companion package 单元测试
│   ├── DevHub.Sdk.IntegrationTests/         # 集成测试
│   └── DevHub.Sdk.ConformanceAdapter/       # conformance 适配器项目
└── tools/                                   # SDK 配套工具
    └── DevHub.Sdk.UnityPublish/             # Unity publish 工具项目
```

## 使用方式

```powershell
python3 scripts/sdk/publish_unity_dotnet_sdk.py
```

Unity 版本只能通过 `python3 scripts/sdk/publish_unity_dotnet_sdk.py` 生成引用目录，并在 Unity 工程中引用该脚本输出目录内的 DLL；不要直接引用 SDK `.csproj`，也不要把 `dotnet pack` 生成的 `.nupkg` 作为 Unity 接入入口。

该脚本会执行本地 `dotnet publish`，输出目录可直接作为 Unity 工程引用的 DLL 集合。
脚本每次执行前都会重建输出目录，避免残留上一次 publish 的陈旧 DLL。
默认输出目录为 `artifacts/sdk/dotnet-for-unity`。

可选参数：

```powershell
python3 scripts/sdk/publish_unity_dotnet_sdk.py --output artifacts/sdk/dotnet-for-unity --configuration Release
```

`dotnet pack` 仍用于生成 NuGet 主包；Unity 引用的 DLL 目录只通过上述本地 publish 脚本生成。

## Unity 2019.4 适配说明

当前交付的 `DevHub.Sdk.DotNet` 与 `DevHub.Sdk.DotNet.DependencyInjection` 均仅包含 `netstandard2.0` 目标资产，用于匹配 Unity 2019.4 可稳定消费的程序集基线。

Unity 接入边界固定为 `publish_unity_dotnet_sdk.py` 产出的 DLL 目录；`DevHub.Sdk.DotNet` 与 `DevHub.Sdk.DotNet.DependencyInjection` 的包资产用于常规 .NET 消费，不作为 Unity 工程的直接引用入口。

SDK 的公开 JSON 类型面使用 `Newtonsoft.Json` 类型，并由 `Json.Net.Unity3D 9.0.1` 提供程序集：

- `PingResult.Echo`、`RequestResult.Value`、`Invocation.Args`、`DevHubEvent.Payload`、`DevHubRpcException.ErrorData` 等公开载荷使用 `JToken` / `JObject`
- 若消费端需要读取载荷字段，推荐使用 `JObject` / `JToken` 的属性访问与 `Value<T>()` 系列 API

## SDK 包依赖边界

`DevHub.Sdk.DotNet` 主包仅保留与核心 SDK 能力直接对应的外部依赖：

- `Json.Net.Unity3D 9.0.1`：提供 `Newtonsoft.Json` 程序集，用于 runtime discovery、HTTP JSON-RPC、WebSocket 会话、公开模型标注与 `JToken` / `JObject` 载荷访问
- `Microsoft.Bcl.AsyncInterfaces`：为 `DevHubEventsClient.ReadEventsAsync()` 等 `IAsyncEnumerable<T>` / `IAsyncDisposable` 能力提供 `netstandard2.0` 兼容支持
- `System.Threading.Channels`：支撑事件客户端内部的异步事件缓冲与消费队列

`DevHub.Sdk.DotNet.DependencyInjection` 可选包承载容器集成入口，并直接依赖：

- `DevHub.Sdk.DotNet` 当前仓库版本
- `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.2`
- `Microsoft.Extensions.Http 10.0.2`
- `Microsoft.Extensions.Options 10.0.2`

当前 `DevHub.Sdk.DotNet` 主包发布产物不包含以下依赖：

- `System.Text.Json`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Options`

## 发布产物验收基线

`eng/Version.props` 当前将 `.NET SDK` 版本定义为 `0.7.0`。执行 `dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack` 时，会生成形如 `DevHub.Sdk.DotNet.<version>.nupkg` 与 `DevHub.Sdk.DotNet.<version>.snupkg` 的资产；其中 `<version>` 与 `eng/Version.props` 保持一致。主包 `.nupkg` 包含以下发布资产：

- `lib/netstandard2.0/DevHub.Sdk.dll`
- `lib/netstandard2.0/DevHub.Sdk.xml`
- `README.md`

包 `.nuspec` 声明的直接依赖如下：

- `Json.Net.Unity3D 9.0.1`
- `Microsoft.Bcl.AsyncInterfaces 1.1.0`
- `System.Threading.Channels 4.7.0`

`dotnet pack sdks/dotnet/src/DevHub.Sdk.DependencyInjection/DevHub.Sdk.DependencyInjection.csproj -c Release -o temp/sdk-pack` 会生成形如 `DevHub.Sdk.DotNet.DependencyInjection.<version>.nupkg` 与 `DevHub.Sdk.DotNet.DependencyInjection.<version>.snupkg` 的资产。DI companion package 的 `.nupkg` 包含以下发布资产：

- `lib/netstandard2.0/DevHub.Sdk.DependencyInjection.dll`
- `lib/netstandard2.0/DevHub.Sdk.DependencyInjection.xml`
- `README.md`

其 `.nuspec` 声明的直接依赖如下：

- `DevHub.Sdk.DotNet <同版本>`
- `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.2`
- `Microsoft.Extensions.Http 10.0.2`
- `Microsoft.Extensions.Options 10.0.2`

`DevHub.Sdk.DotNet` 主包依赖图不包含以下项目：

- `System.Text.Json`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Options`

与 Unity 单目标适配相关的保留项与测试差异如下：

- `System.Threading.Channels` 与 `Microsoft.Bcl.AsyncInterfaces` 继续保留，用于事件流 API 的异步缓冲、`IAsyncEnumerable<T>` 与 `IAsyncDisposable`
- `DevHub.Sdk.DotNet.DependencyInjection` 单独承载 `AddDevHubSdk()`、`IDevHubClientFactory` 与 `IDevHubEventsClientFactory`
- SDK 发布包仅面向 `netstandard2.0`，测试工程与 conformance adapter 继续使用 `net10.0` 以复用当前 Host 测试基线；这些测试项目不进入 NuGet 发布产物
- Unity 本地 publish 脚本直接输出可供 Unity 引用的 DLL 目录，不再单独改写程序集引用元数据
- `Json.Net.Unity3D 9.0.1` 提供未签名的 `Newtonsoft.Json` 程序集，验收以包结构、依赖边界与 SDK 行为为准
- 由于 `Json.Net.Unity3D 9.0.1` 仅提供 .NET Framework 资产，当前 restore/build 会出现 `NU1701` 兼容性告警；仓库验收以构建结果、包依赖元数据与 SDK 行为为准
