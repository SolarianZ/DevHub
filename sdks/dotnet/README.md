# DevHub .NET SDK

此目录包含 DevHub .NET SDK 的源代码、工作区配置和测试。详细文档请查看 [docs](../../docs/README.md) 。

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

## 事件客户端恢复语义

- 同一 `DevHubEventsClient` 实例会串行执行 `AuthenticateAsync(...)`，避免并发发送多条 `hub.ws.authenticate`。
- `SubscribeAsync(...)` 或 `UnsubscribeAsync(...)` 在请求发出后若因超时或取消进入结果未知状态，SDK 会主动废弃当前 WebSocket 会话。
- 会话被废弃后，后续读取、订阅或只读 WS RPC 都需要先重新执行 `AuthenticateAsync(...)`，再重新执行 `SubscribeAsync(...)`。
- 本地事件缓冲采用有界 fail-fast 队列；消费者落后导致缓冲溢出时，当前事件流会终止，并要求重新认证与重新订阅。

## 运行时诊断

核心 SDK 通过 `DevHubClientDependencies.LoggerFactory` 与 `DevHubEventsClientDependencies.LoggerFactory` 接入可选日志；使用 `AddDevHubSdk(...)` / 工厂时，companion package 会自动桥接容器中的 `ILoggerFactory`。

启用日志后，SDK 会输出以下结构化诊断信息：

- runtime discovery 开始、成功、失败；
- HTTP RPC 请求发送、成功、超时/取消与协议失败；
- WebSocket 连接建立、主动断开、远端关闭与终止错误；
- `AuthenticateAsync(...)`、`SubscribeAsync(...)`、`UnsubscribeAsync(...)` 的生命周期；
- 订阅结果未知导致的强制断连；
- 本地事件缓冲溢出与终止路径。

## 内容结构

```text
sdks/dotnet/
├── DevHub.DotNetSdk.slnx                          # .NET SDK 工作区解决方案文件
├── Directory.Build.props                          # 工作区公共构建配置
├── Directory.Packages.props                       # 统一依赖版本管理
├── src/                                           # SDK 源代码
│   ├── DevHub.Sdk/                                # SDK 核心库
│   └── DevHub.Sdk.DependencyInjection/            # 依赖注入扩展
├── tests/                                         # SDK 测试工程
│   ├── DevHub.Sdk.UnitTests/                      # SDK 单元测试
│   ├── DevHub.Sdk.DependencyInjection.UnitTests/  # 依赖注入扩展单元测试
│   ├── DevHub.Sdk.IntegrationTests/               # SDK 与 Host 的集成测试
│   └── DevHub.Sdk.ConformanceAdapter/             # 协议符合性适配器
└── tools/                                         # SDK 辅助工具
    └── DevHub.Sdk.UnityPublish/                   # Unity 发布支持工具（仅限 dotnet_sdk_for_unity 分支）
```
