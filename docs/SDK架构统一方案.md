# SDK 架构统一方案

## 统一基线

本方案只以 `docs/Spec.md` 为准，不通过修改 Spec 迁就现状实现。

- 数据根目录解析统一遵循 `Spec.md §4.1.1`：显式 `dataDir` 参数 > `DEVHUB_DATA_DIR` > 平台默认数据根目录。
- 三套 SDK 都应完全移除 `DEVHUB_RUNTIME_DIR`、`DEVHUB_APPDEFS_DIR`、`DEVHUB_APPINST_DIR`、`DEVHUB_LOG_DIR` 的处理逻辑；不保留兼容分支、迁移提示、注释、README 说明和对应测试。
- 运行时发现统一遵循 `Spec.md §4.1.1`、`§4.1.2`：固定读取 `<dataDir>/runtime/hub.json`，并继续通过 `hub.json.tokenFile` 读取令牌。
- 三套 SDK 的集成测试统一遵循 `Spec.md §4.1.1` 的单实例粒度约束：每套 SDK 在运行集成测试时都必须自行启动绑定独立临时 `dataDir` 的临时 Host，禁止复用开发机默认数据根目录下的常驻 Host，也禁止不同 SDK 共享同一个测试 Host。
- 三套 SDK 的集成测试在完成后都必须关闭自己启动的临时 Host，并清理对应临时目录；若测试覆盖 `launch` 场景，还必须确保 Host 派生出的子进程一并回收，避免并行测试互相干扰。
- 三套 SDK 的 README 都必须明确说明上述集成测试隔离约束，并区分“SDK 集成测试自启临时 Host”与“仓库级 smoke / 手工联调可连接本地 Hub”这两类运行方式。
- WebSocket 统一遵循 `Spec.md §3.3`、`§4.3`、`§6.3.16`：鉴权后，客户端只接受两类入站消息。
  - 与挂起请求匹配的 JSON-RPC 响应。
  - `hub.event` 通知。
- 事件类型统一收敛到当前 Spec 对外公开的 6 个事件值；公开类型、订阅入参、运行时解析校验保持一致。
- 订阅生命周期统一遵循 `Spec.md §6.3.14` 到 `§6.3.16`：订阅绑定到连接，断线后订阅自动失效；若复用同一客户端对象，则必须重新认证并重新订阅。
- 三套 SDK 都应提供同构的高级扩展点：`runtime resolver`、`HTTP transport`、`WS session`。语言生态附加能力可以保留，但不能替代这三类扩展点。

## .NET SDK 处理方案

### 1. 补齐公开扩展点，和 Python / JS 保持同构

- 当前不一致：
  - `.NET SDK` 的高阶扩展 seam 仍以 internal 为主，调用方公开可见的主要入口只有 `DevHubClient.FromRuntimeAsync()`、`DevHubEventsClient.FromRuntimeAsync()` 与 `AddDevHubSdk()`。
  - Python / JS 已经把 runtime resolver、HTTP transport、WS session 作为公开扩展点提供给调用方注入。
- 位置：
  - `sdks/dotnet/src/DevHub.Sdk/DevHubClient.cs`，`FromRuntimeAsync()`，关键词：`HttpMessageHandler handler`
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`FromRuntimeAsync()`，关键词：`IWebSocketConnectionFactory connectionFactory`
  - `sdks/dotnet/src/DevHub.Sdk/Internal/JsonRpcHttpTransport.cs`，`Create()`，关键词：`internal sealed class JsonRpcHttpTransport`
  - `sdks/dotnet/src/DevHub.Sdk/Internal/WebSocketTransport.cs`，关键词：`IWebSocketConnectionFactory`、`ClientWebSocketConnectionFactory`
- 处理方案：
  - 将 runtime discovery、HTTP transport、WS session 三类 seam 提升为公开抽象。
  - 为 `DevHubClient` 和 `DevHubEventsClient` 提供公开构造入口或公开工厂重载，使调用方可注入上述抽象。
  - `AddDevHubSdk()` 保留为 .NET 生态增强层，但底层仍走同一组公开 seam。
- Spec 对齐说明：
  - 该调整只改变 SDK 构造与扩展方式，不改变协议消息、字段、错误语义与运行时发现规则，符合 Spec。

### 2. 完全移除旧环境变量处理逻辑

- 当前不一致：
  - `.NET SDK` 仍显式扫描并拒绝 `DEVHUB_RUNTIME_DIR`、`DEVHUB_APPDEFS_DIR`、`DEVHUB_APPINST_DIR`、`DEVHUB_LOG_DIR`。
  - 这属于超出 Spec 的旧版迁移逻辑，不应继续保留。
- 位置：
  - `sdks/dotnet/src/DevHub.Sdk/Internal/RuntimeDiscovery.cs`，`ResolveDataDirectory()` / `ThrowIfLegacyEnvironmentVariablesPresent()`，关键词：`DEVHUB_RUNTIME_DIR`、`DEVHUB_APPDEFS_DIR`
  - `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Discovery/RuntimeDiscoveryTests.cs`，`RuntimeDiscovery_WhenLegacyEnvironmentVariableProvided_ShouldThrowMigrationException`，关键词：`DEVHUB_APPINST_DIR`、`DEVHUB_LOG_DIR`
  - `sdks/dotnet/README.md`，`Runtime Discovery`，关键词：`DEVHUB_RUNTIME_DIR`
- 处理方案：
  - 删除旧环境变量列表、扫描逻辑、迁移异常文本。
  - 删除与旧环境变量相关的单元测试与 README 说明。
  - 保留且只保留 Spec 要求的三段解析顺序：显式 `DataDir`、`DEVHUB_DATA_DIR`、平台默认目录。
- Spec 对齐说明：
  - `Spec.md §4.1.1` 只定义了显式参数、`DEVHUB_DATA_DIR` 与平台默认目录；移除旧变量处理后反而更严格对齐 Spec。

### 3. 收紧事件类型公开模型

- 当前不一致：
  - `.NET SDK` 目前只有 `DevHubEventTypes` 字符串常量；`SubscribeAsync()` 仍接收 `IEnumerable<string>`，`DevHubEvent.Type` 仍是 `string`，运行时只检查非空字符串。
  - JS 已收敛到闭集事件类型模型；Python 也至少在解析层按闭集校验。
- 位置：
  - `sdks/dotnet/src/DevHub.Sdk/Models/DevHubEventTypes.cs`，关键词：`InvocationFailed`
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`SubscribeAsync()`，关键词：`IEnumerable<string>? types`
  - `sdks/dotnet/src/DevHub.Sdk/Models/InvocationModels.cs`，`DevHubEvent`，关键词：`public string Type`
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`ValidateEvent()`，关键词：`string.IsNullOrWhiteSpace(evt.Type)`
- 处理方案：
  - 新增公开的闭集事件类型模型，例如 string-backed `DevHubEventType`。
  - 将 `DevHubEvent.Type` 与 `SubscribeAsync(types)` 的公开签名都收敛到该模型。
  - 运行时解析阶段继续按 Spec 字符串值进行校验，遇到非 6 个已知值立即失败。
- Spec 对齐说明：
  - 事件在协议层仍按 Spec 的字符串值序列化，不改变 WS 契约；只是把 SDK 公开模型从“裸字符串”收紧为“受限字符串集合”。

### 4. 事件客户端补齐“断线后重新认证 + 重新订阅”的复用语义

- 当前不一致：
  - `.NET SDK` 已经对非法 WS 消息采取严格失败策略，这点与目标一致。
  - 但当前连接终止后会直接完成内部事件通道，缺少像 JS 那样的“同一客户端对象重新认证并恢复读流”的能力。
- 位置：
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`AuthenticateAsync()`，关键词：`_eventStreamAvailable = true`
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`RunReceiveLoopAsync()`，关键词：`_eventChannel.Writer.TryComplete(terminalException)`
  - `sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs`，`ReadEventsAsync()`，关键词：`ReadEventsCore`
- 处理方案：
  - 保留当前“非法 WS 入站消息即 fail fast”的严格策略，不做放宽。
  - 将“连接终止”与“客户端已 Dispose”拆开建模。
  - 当连接异常终止但客户端未 `DisposeAsync()` 时，允许再次执行 `AuthenticateAsync()` 创建新的连接状态与新的事件通道。
  - 明确要求调用方在重新认证后重新订阅，旧订阅不复用。
- Spec 对齐说明：
  - 订阅绑定连接、断线自动清理属于 Spec 明确要求；重新认证后重新订阅完全符合 Spec。

### 5. 将当前集成测试隔离模式显式固化到 README 与回归测试

- 当前不一致：
  - `.NET SDK` 的集成测试代码已经通过 `DevHubHostFixture` 为每个测试用例启动独立临时 Host，并在释放时关闭 Host、删除临时目录。
  - 但 `README` 还没有把“集成测试必须使用独立临时 Host，禁止复用本机常驻 Host”写成显式约束，使用者仅从命令示例无法得出这一结论。
- 位置：
  - `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/TestHost/DevHubHostFixture.cs`，关键词：`StartAsync()`、`DisposeAsync()`、`Kill(entireProcessTree: true)`
  - `sdks/dotnet/README.md`，关键词：`常用命令`、`dotnet test`
- 处理方案：
  - 在 `.NET SDK README` 中新增“集成测试隔离模式”章节，明确说明 `dotnet test` 运行的 SDK 集成测试会自启独立临时 Host 与临时 `DEVHUB_DATA_DIR`，不得连接本机常驻 Hub。
  - 在 README 中补充并行开发场景说明：同一台机器可同时运行 `.NET / Python / JS` SDK 集成测试，因为每套测试都必须拥有自己的临时 Host 与独立数据根目录。
  - 补一条 Host 夹具回归测试，验证 `DisposeAsync()` 后临时目录被清理、Host 主进程退出，作为文档约束的自动化兜底。
- Spec 对齐说明：
  - 该调整不改变协议行为，只把 `Spec.md §4.1.1` 已要求的“单实例粒度 = 当前用户 + dataDir”落实到 SDK 集成测试规范与说明文档。

## Python SDK 处理方案

### 1. 完全移除旧环境变量处理逻辑

- 当前不一致：
  - `Python SDK` 仍显式检测并拒绝 `DEVHUB_RUNTIME_DIR`。
  - 根据统一基线，这类旧变量处理应被整体删除，而不是继续保留单个变量的兼容分支。
- 位置：
  - `sdks/python/src/devhub_sdk/runtime.py`，`resolve_data_directory()` / `_ensure_legacy_runtime_dir_env_unused()`，关键词：`DEVHUB_RUNTIME_DIR`
  - `sdks/python/tests/unit/test_runtime.py`，`test_resolve_data_directory_when_legacy_runtime_env_present_should_raise`，关键词：`DEVHUB_RUNTIME_DIR`
  - `sdks/python/README.md`，`高级扩展`，关键词：`DEVHUB_RUNTIME_DIR`
- 处理方案：
  - 删除旧环境变量常量、检测逻辑、异常文本。
  - 删除对应测试与 README 表述。
  - `resolve_data_directory()` 只保留 Spec 规定的三段解析顺序。
- Spec 对齐说明：
  - 删除旧变量逻辑后，运行时发现路径规则与 `Spec.md §4.1.1`、`§4.1.2` 完全一致。

### 2. 将未知服务端通知从“忽略”改为“协议错误并终止事件流”

- 当前不一致：
  - `Python SDK` 遇到 `method != "hub.event"` 且不带 `id/result/error` 的服务端通知时直接忽略并继续读流。
  - `.NET` 与 JS 都会把这种消息视为协议错误并终止事件流。
- 位置：
  - `sdks/python/src/devhub_sdk/_ws_session.py`，`_handle_message()`，关键词：`if method != "hub.event"`、`return`
  - `sdks/python/tests/unit/test_events_client.py`，`test_events_client_when_unknown_notification_received_should_ignore_and_continue`，关键词：`hub.future.notification`
- 处理方案：
  - 调整 `_handle_message()`：鉴权后仅允许“挂起请求响应”与 `hub.event` 两类入站消息。
  - 任何其他服务端通知都抛出协议错误，并终止当前事件流。
  - 删除“忽略未知通知并继续”这类测试，改为验证事件流失败。
- Spec 对齐说明：
  - `Spec.md §6.3.16` 只定义了 `hub.event` 作为服务端事件交付方式；SDK 严格限制入站通知类型更符合当前公开契约。

### 3. 将事件类型从裸字符串收敛为闭集公开模型

- 当前不一致：
  - `Python SDK` 虽然在解析层按 `ALL_EVENT_TYPES` 校验，但公开类型仍是 `Iterable[str]` 与 `str`。
  - 这会让 Python 在 API 层比 JS 松散，也让调用方把非法事件类型带到网络边界。
- 位置：
  - `sdks/python/src/devhub_sdk/events.py`，`subscribe()`，关键词：`Iterable[str] | None`
  - `sdks/python/src/devhub_sdk/models.py`，`DevHubEvent`，关键词：`type: str`
  - `sdks/python/src/devhub_sdk/constants.py`，关键词：`ALL_EVENT_TYPES`
  - `sdks/python/src/devhub_sdk/_parsing.py`，`parse_event()`，关键词：`if event_type not in ALL_EVENT_TYPES`
- 处理方案：
  - 新增公开的 `DevHubEventType` 类型别名或 `StrEnum`，并公开稳定的受支持事件列表。
  - 将 `DevHubEvent.type` 与 `subscribe(types)` 的签名都收紧到该类型。
  - 在入参校验阶段就拒绝未知事件类型，而不是继续把它们发送给 Hub。
- Spec 对齐说明：
  - 事件在协议层仍使用 Spec 定义的字符串值；SDK 只是在公开类型系统上收紧，不改变协议契约。

### 4. 补齐事件客户端复用语义

- 当前不一致：
  - `Python SDK` 当前把 `_stream_completed` 作为永久终止状态；连接一旦异常结束，后续请求直接报“事件流已终止”。
  - JS 已支持“断线后重新认证 + 重新订阅”；`.NET SDK` 也建议向这一语义收敛。
- 位置：
  - `sdks/python/src/devhub_sdk/_ws_session.py`，`_ensure_stream_available()`，关键词：`_stream_completed`
  - `sdks/python/src/devhub_sdk/_ws_session.py`，`_complete_event_stream()`，关键词：`_stream_completed = True`
  - `sdks/python/tests/unit/test_events_client.py`，`test_events_client_when_connection_terminated_should_raise_runtime_error_on_followup_request`，关键词：`事件流已终止`
- 处理方案：
  - 将“客户端已关闭”和“当前连接已终止”拆成两个状态。
  - 当连接终止但客户端未 `close()` 时，允许再次 `authenticate()` 建立新连接，并重置挂起请求表与事件队列。
  - 旧事件流在终止后结束；新的 `read_events()` 从新连接对应的队列开始读取。
  - 重新认证后必须重新订阅，不复用旧订阅。
- Spec 对齐说明：
  - 该语义直接遵循 Spec 中“订阅绑定连接、断线自动清理”的要求。

### 5. 将集成测试 Host 清理从“关闭主进程”提升为“回收整个进程树”，并补齐 README 说明

- 当前不一致：
  - `Python SDK` 的集成测试已通过 `DevHubHostFixture.start()` 为每个测试用例创建独立临时 Host 和独立 `dataDir`，这点方向正确。
  - 但 `DevHubHostFixture.close()` 目前只关闭 Host 主进程；在 `launch` 相关测试中，若 Host 已派生子进程，主进程退出后仍可能留下短生命周期或异常残留的子进程。
  - `README` 也未明确写出“SDK 集成测试自启临时 Host、禁止复用本地常驻 Hub”，容易和仓库级 smoke 的手工前置步骤混淆。
- 位置：
  - `sdks/python/tests/integration/_host.py`，关键词：`DevHubHostFixture.start()`、`close()`、`self._process.kill()`
  - `sdks/python/tests/integration/test_http_flow.py`，关键词：`test_two_hosts_with_different_data_dirs_should_isolate_http_state`
  - `sdks/python/README.md`，关键词：`验证命令`、`本地 Smoke 前置说明`
- 处理方案：
  - 改造 `DevHubHostFixture` 的进程启动与关闭方式：启动时创建独立 process group / session，关闭时按平台回收整棵进程树，而不是只 kill Host 主进程。
  - 为 `launch` 场景补充回归测试，验证测试结束后不会留下 Host 派生进程，避免与其他 SDK 的并行测试互相污染。
  - 在 `Python SDK README` 中新增“集成测试隔离模式”章节，明确 `pytest tests/integration` 会自启独立临时 Host，不依赖本机默认 `DEVHUB_DATA_DIR`。
  - 在 README 中把“仓库级 smoke 需要开发者手工启动本地 Hub”与“SDK 集成测试自动启动临时 Host”拆成两个独立说明，避免误导。
- Spec 对齐说明：
  - 独立 `dataDir` 运行多实例属于 `Spec.md §4.1.1` 明确允许的场景；加强进程树清理只是保证测试环境可重复、可并行，不改变协议契约。

## JavaScript SDK 处理方案

### 1. 完全移除旧环境变量处理逻辑

- 当前不一致：
  - `JS SDK` 仍显式检测并拒绝 `DEVHUB_RUNTIME_DIR`。
  - 根据统一基线，这类逻辑应被彻底删除，不再保留任何迁移兼容痕迹。
- 位置：
  - `sdks/javascript/src/runtime.ts`，`resolveDataDirectory()` / `assertLegacyRuntimeDirEnvUnset()`，关键词：`DEVHUB_RUNTIME_DIR`
  - `sdks/javascript/tests/unit/runtime.test.ts`，关键词：`检测到旧环境变量时应抛出迁移错误`
  - `sdks/javascript/README.md`，`当前状态`，关键词：`DEVHUB_RUNTIME_DIR`
- 处理方案：
  - 删除旧环境变量常量、检测逻辑、测试和 README 说明。
  - `resolveDataDirectory()` 仅保留 Spec 规定的解析顺序与 `<dataDir>/runtime/hub.json` 布局。
- Spec 对齐说明：
  - 该调整直接向 `Spec.md §4.1.1`、`§4.1.2` 收敛。

### 2. 统一 runtime resolver 的输入模型

- 当前不一致：
  - `JS SDK` 的 `RuntimeResolver.resolve()` 只接收 `dataDirOverride?: string`。
  - Python 当前接收完整 `DevHubClientOptions`，目标中的 `.NET SDK` 也应公开同类 seam；因此 JS 的 resolver 输入过窄，是三套 SDK 中的异类。
- 位置：
  - `sdks/javascript/src/runtime.ts`，`RuntimeResolver.resolve()`，关键词：`dataDirOverride?: string`
  - `sdks/javascript/src/client.ts`，`fromRuntime()`，关键词：`resolve(normalized.dataDir)`
  - `sdks/javascript/src/events-client.ts`，`fromRuntime()`，关键词：`resolve(normalized.dataDir)`
- 处理方案：
  - 将 `RuntimeResolver.resolve()` 统一为接收完整的归一化客户端选项，而不是只接收字符串路径。
  - `DevHubClient.fromRuntime()` 与 `DevHubEventsClient.fromRuntime()` 都把完整选项传给 resolver。
  - 这样三套 SDK 的 runtime seam 都能承载未来的发现策略扩展，而不是把 resolver 限制成“单纯路径映射器”。
- Spec 对齐说明：
  - 该调整只影响 SDK 内部架构与扩展接口，不改变 Spec 规定的数据根目录优先级和发现文件布局。

### 3. 将当前 WS 严格校验与重连语义固化为跨语言基线

- 当前不一致：
  - `JS SDK` 在这两点上已经比另两套 SDK 更接近目标状态。
  - 当前需要的不是放宽 JS，而是把 JS 的做法固化为统一基线，供 .NET / Python 对齐。
- 位置：
  - `sdks/javascript/src/ws-session.ts`，`handleMessage()`，关键词：`supported response or hub.event notification`
  - `sdks/javascript/src/events-client.ts`，`authenticate()`，关键词：`this.eventQueue = new AsyncQueue<DevHubEvent>()`
  - `sdks/javascript/tests/unit/events-client.test.ts`，关键词：`断线后重新认证应重建事件流并要求重新订阅`
- 处理方案：
  - 保持当前“仅接受响应或 `hub.event`”的严格校验，不做协议放宽。
  - 保持当前“断线后可重新认证，但必须重新订阅”的对象复用语义。
  - 补充共享架构说明与跨语言一致性测试，把该行为从“JS 当前实现”升级为“统一 SDK 约束”。
- Spec 对齐说明：
  - 严格校验与重新认证后重新订阅都符合 `Spec.md §3.3`、`§4.3`、`§6.3.14` 到 `§6.3.16`。

### 4. 继续以闭集事件类型模型作为统一参考实现

- 当前不一致：
  - `JS SDK` 已经提供了 `DevHubEventType`、`SUPPORTED_EVENT_TYPES`、`ensureSupportedEventType()`，这比 `.NET` 与 Python 更完整。
  - 当前需要的是将这一做法提升为跨语言统一模型，而不是让 JS 回退到裸字符串。
- 位置：
  - `sdks/javascript/src/event-types.ts`，关键词：`SUPPORTED_EVENT_TYPES`、`ensureSupportedEventType`
  - `sdks/javascript/src/events-client.ts`，`subscribe()`，关键词：`readonly DevHubEventType[]`
  - `sdks/javascript/src/models.ts`，`DevHubEvent`，关键词：`type: DevHubEventType`
- 处理方案：
  - 保持当前闭集事件类型模型不变。
  - 以 JS 的事件类型设计为参考，推动 `.NET` 与 Python 收敛到同一层次的公开约束。
  - 在后续统一文档中明确：事件类型不应再以裸字符串作为公开主模型。
- Spec 对齐说明：
  - JS 当前模型仍按 Spec 字符串值进行序列化与解析，不改变协议，仅提升 SDK API 的约束强度。

### 5. 将当前集成测试隔离模式补齐为“文档显式约束 + 进程树级清理”

- 当前不一致：
  - `JS SDK` 的集成测试已经通过 `DevHubHostFixture.start()` 自行启动临时 Host，并通过独立临时目录隔离运行时数据；测试文件级 `beforeAll/afterAll` 也已经避免了对外部常驻 Host 的依赖。
  - 但 `DevHubHostFixture.close()` 当前只终止 Host 主进程，未显式保证 `launch` 场景下的派生子进程被整树回收。
  - `README` 尚未把“SDK 集成测试必须自启独立临时 Host、禁止复用本地常驻 Host”写成清晰规则。
- 位置：
  - `sdks/javascript/tests/integration/host.ts`，关键词：`DevHubHostFixture.start()`、`close()`、`process.kill("SIGKILL")`
  - `sdks/javascript/tests/integration/runtime-discovery.test.ts`，关键词：`不同 dataDir 下的 Host 应并行隔离 HTTP 与 Events 链路`
  - `sdks/javascript/README.md`，关键词：`开发命令`、`npm test`
- 处理方案：
  - 调整 `JS` 集成测试夹具的进程管理策略：启动时创建可被整组终止的进程组，关闭时做跨平台整棵进程树回收，而不是只杀主进程。
  - 保留当前“每个测试文件一个临时 Host”的粒度，不强行改成“每个 test 一个 Host”；统一要求的关键是“每套 SDK 自管独立 Host 与 dataDir”，不是三套语言必须使用相同测试粒度。
  - 在 `JS SDK README` 中新增“集成测试隔离模式”章节，明确 `npm test` 中的集成测试会自建 / 自启临时 Host，不连接开发机默认数据目录下的常驻 Hub。
  - 在 README 中补充并行开发说明，明确该模式就是为了支持同机同时运行多套 SDK 集成测试而互不干扰。
- Spec 对齐说明：
  - 独立数据根目录并行运行多个 Host 与 `Spec.md §4.1.1` 一致；加强清理与文档说明不改变任何公开协议行为。

## 收尾要求

- 本方案涉及的所有实现调整都不得修改 `docs/Spec.md`。
- 调整完成后，三套 SDK 的 README、单元测试与集成测试口径必须同步更新，确保：
  - 数据根目录只体现 Spec 规定的解析链路。
  - WS 入站消息的允许集合一致。
  - 事件类型公开模型一致。
  - 断线后的认证 / 订阅生命周期一致。
  - SDK 集成测试都以“独立临时 Host + 独立临时 dataDir + 测后可靠回收”为统一模式，并在 README 中明确写出。
