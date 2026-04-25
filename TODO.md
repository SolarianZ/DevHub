# 待办事项

## 待处理问题

### Host

- 实例级身份没有真正绑定到调用链：`host/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs` `PollAsync` / `RespondAsync`（搜索 `Heartbeat(instanceId, out _)`）以及 `host/src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs` `HeartbeatAsync` 目前基本只靠 `instanceId` 识别实例，没有把 `ClientId` / `ClientSessionId` 与实例所有权绑定起来；而 `hub.apps.listInstances` 又会暴露可用 `instanceId`。这会导致持有 runtime token 的其他客户端有机会冒充实例去 poll、respond 或 heartbeat。
- 启动去重窗口设计不稳：`host/src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs` `CleanupExpiredLaunchRecords` / `TryGetActiveDedupeRecord`（搜索 `LaunchDedupeWindowSeconds`）会在固定时间窗后清理 dedupe record，即使原进程还活着、只是尚未完成注册。慢启动应用会因此被重复拉起，造成重复实例和竞争注册。
- `hub.apps.registerInstance` 对 `appId` 的防御式校验不完整：`host/src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs` `TryParseInstanceRegistration`（搜索 `appId 为空`）只检查非空，没有复用 `AppDefinitionValidator.IsValidAppId`。这会让非法 `appId` 的实例进入注册表并参与运行时行为，和其他接口的契约约束不一致。
- Definition 快照暴露为可变对象：`host/src/DevHub.Core/Services/DefinitionProvider.cs` `GetAllDefinitions` / `GetDefinition`（搜索 `return _snapshot`）直接返回内部 `AppDefinition` 引用，而 `AppDefinition` / `LaunchConfiguration` / `AppCapabilities` 都是可写模型。调用方可以绕过校验和持久化流程直接改写内存态 definition，容易引入隐式副作用。
- HTTP 通知失败时仍返回空响应：`host/src/DevHub.Host/RpcHttpEndpointHandler.cs` `HandleAsync`（搜索 `suppressJsonRpcResponse`）当前只要请求没有 `id`，后续鉴权失败、参数错误、路由拒绝或内部异常都会被吞成空 `200`。这会让 `hub.invoke.notify` 一类调用把“实际失败”误判成“已成功接收”。
- HTTP 连接取消会错误终止仍在进行中的 invocation：`host/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs` `HandleRequestTimeoutAsync`（搜索 `cancellationToken.IsCancellationRequested`）会把调用方本地超时、主动断开或代理中断直接折叠成 `invocation_timeout` / `invocation_expired`。这样传输层偶发中断会误杀真实业务调用，后续实例返回也会被拒绝。

#### 其他

- `queueIfOffline` 似乎没必要存在？
- 现在做不到在已有 app definition （appid+scope) 的情况下，注册一个相同 appid 但 scope 不同的 app instance

### .NET SDK

- 事件流公开接口与底层语义不一致：`sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs` `ReadEventsAsync` / `ReadEventsCore`（搜索 `_eventChannel`）当前基于共享 `ChannelReader` 夺取事件，同一个 `DevHubEventsClient` 上的多个读取方会被分流而不是广播，容易导致消费者丢事件。需要明确收敛为单消费者契约，或改成真正的多消费者广播模型。
- WebSocket 终止后的事件流状态没有正确复位：`sdks/dotnet/src/DevHub.Sdk/DevHubEventsClient.cs` `HandleTermination` / `AuthenticateAsync`（搜索 `_eventStreamAvailable`）在连接断开或重认证失败后没有同步清理事件流可用状态，`ReadEventsAsync` 仍可能返回一个已终止的旧流，而不是明确要求重新认证并重建订阅。
- RPC 异常模型破坏了 .NET 基类契约：`sdks/dotnet/src/DevHub.Sdk/DevHubRpcException.cs`（搜索 `public new JsonElement? Data`）通过 `new Data` 隐藏了 `Exception.Data`，让通用异常处理代码和专用异常处理代码看到不同语义的 `Data`。应保留显式命名的协议错误数据属性，避免覆盖基类成员语义。
- 核心 SDK 与 DI 集成层边界没有拆开：`sdks/dotnet/src/DevHub.Sdk/DevHubServiceCollectionExtensions.cs` 与 `sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj`（搜索 `AddDevHubSdk` / `Microsoft.Extensions.Http`）把 `AddDevHubSdk`、工厂接口和 `Microsoft.Extensions.*` 依赖直接放进核心包，导致只想使用运行时客户端的调用方也必须承受宿主框架耦合。应将可选的 DI/Extensions 集成下沉到独立程序集。
- `Scope` 默认值把“未设置”静默折叠成 Global：`sdks/dotnet/src/DevHub.Sdk/Models/AppModels.cs` `AppDefinition` / `AppInstanceRegistration`（搜索 `_scope = string.Empty`）以及 `sdks/dotnet/src/DevHub.Sdk/Models/InvocationModels.cs` `LaunchRequest` / `InvocationTarget`（搜索 `_scope = string.Empty`）会让调用方在遗漏 `scope` 时直接把请求发到 Global，而不是尽早暴露参数缺失，容易造成跨作用域误写和误启动。
- 事件解析对 `app.instance.*.payload.scope` 过严：`sdks/dotnet/src/DevHub.Sdk/Internal/ResponsePayloadReader.cs` `ValidateKnownEventPayload`（搜索 `app.instance.registered`）把 `scope` 当成必填字段；但协议只要求至少包含 `appId` 与 `instanceId`。Host 发出规范合法但未带 `scope` 的事件时，SDK 会把它当成协议错误并打断 WS 事件流。

### JS/TS SDK

- WebSocket 请求超时后的迟到响应会误杀整条事件连接：`sdks/javascript/src/ws-session.ts` `createPendingRequest` / `completePending`（搜索 `response id does not match any pending request`）在本地超时后会先移除 pending request；如果服务端稍后回包，当前实现会把这类迟到响应视为协议错误并直接终止 WS session。结果是一次慢 `ping`、`subscribe` 或查询就可能把整个事件流打断。应忽略或记录超时请求的迟到响应，而不是升级为致命连接错误。
- 事件流公开接口与底层语义不一致：`sdks/javascript/src/events-client.ts` `readEvents`（搜索 `return this.#eventQueue`）当前始终返回同一个 `AsyncQueue`。同一个 `DevHubEventsClient` 上如果有多个调用方并行读取事件，底层会把事件分流给不同迭代器，而不是向每个消费者广播，容易造成调用方静默丢事件。需要明确收敛为单消费者契约，或改成真正的多消费者广播模型。
- 公开 TypeScript 模型比真实运行时契约更宽：`sdks/javascript/src/models.ts` 与 `sdks/javascript/src/payloads.ts`（搜索 `target?:`、`exePath?:`、`必须且只能包含 value 或 error 之一`）中有多处类型允许的输入会被 SDK 自己立即拒绝，例如 `InvokeRequest.target` 在类型上可选但运行时必填、`LaunchConfiguration.exePath` 在类型上可选但只要存在 `launch` 就必填、`RespondRequest` 没有表达 `value` 与 `error` 互斥。这样会让 TS 用户编译通过但在构造请求时才失败，需要把公开类型收紧到与实际协议和校验逻辑一致。
- 首次建连完成前会误判为“已连接”：`sdks/javascript/src/ws-session.ts` `ensureConnected` / `sendRequest`（搜索 `if (this.socket) {`）在第一个请求尚未等到 WebSocket `open` 时，就会让并发请求把“已创建 socket”误当成“已完成连接”，随后直接 `send`。这会导致首批并发请求随机失败，甚至打断整条事件会话。
- WebSocket 终止后事件流可用状态没有复位：`sdks/javascript/src/events-client.ts` `handleTermination` / `ensureEventStreamAvailable`（搜索 `#eventStreamAvailable = true`）终止路径只清理了认证状态，没有同步清掉事件流可用标记。这样 `readEvents()` 仍可能返回一个已终止的旧队列，调用方看起来像“没有新事件”而不是“需要重新认证”。
- 实例事件负载把可选 `scope` 错当成必填：`sdks/javascript/src/parsers.ts` `validateEventPayload`（搜索 `APP_INSTANCE_REGISTERED`）会对 `app.instance.registered` / `app.instance.unregistered` 无条件读取 `payload.scope`。这会让 SDK 拒绝 Host 发出的规范合法事件，并中断事件消费。

### Python SDK

- WebSocket 请求超时后的迟到响应会误杀整条事件连接：`sdks/python/src/devhub_sdk/_ws_session.py` `send_request` / `_handle_message`（搜索 `asyncio.wait_for(future`、`响应 id 未匹配任何挂起请求`）在本地超时后会先移除 `_pending`；如果服务端稍后回包，当前实现会把这类迟到响应视为协议错误并直接终止 WS session。结果是一次慢 `ping`、`subscribe` 或查询就可能把整个事件流打断。应忽略或记录超时请求的迟到响应，而不是升级为致命连接错误。
- WebSocket 会话首次建连缺少并发保护：`sdks/python/src/devhub_sdk/_ws_session.py` `_ensure_connected`（搜索 `self._websocket = await self._connect`）当前只靠 `if self._websocket is not None` 判断是否已连接，没有串行化首次连接流程。同一个会话上若有多个协程并发发起首批请求，可能实际建立多条 WebSocket 连接，并让 `_websocket`、`_receiver_task` 与挂起请求落到不同连接代次，造成状态错乱和资源泄漏。应为建连过程增加互斥或单飞保护。
- 事件负载解析比 Spec 更严格，可能拒绝合法事件：`sdks/python/src/devhub_sdk/_parsing.py` `_validate_known_event_payload`（搜索 `app.instance.registered`、`require_scope_string(payload_root, "scope"`）当前把 `app.instance.registered` / `app.instance.unregistered` 的 `payload.scope` 当成必填字段；但规范只要求这两个事件至少包含 `appId` 与 `instanceId`，`scope` 仅在存在时才需要满足规范化要求。这样会导致 SDK 拒绝 Host 发出的规范合法事件。
- WebSocket 终止后事件流可用状态没有复位：`sdks/python/src/devhub_sdk/events.py` `DevHubEventsClient._refresh_session_state` / `_ensure_event_stream_available`（搜索 `_event_stream_available = True`）在会话终止后只清理了 `_authenticated`，没有同步清理 `_event_stream_available`。这样 `read_events()` 仍会被当成可用，调用方拿到的是旧流结束态而不是明确的重连/重认证信号。
- 同一事件客户端的多个读取方会被分流而不是广播：`sdks/python/src/devhub_sdk/_ws_session.py` `WebSocketJsonRpcSession.read_events`（搜索 `stream.queue.get()`）以及 `sdks/python/src/devhub_sdk/events.py` `DevHubEventsClient.read_events` 当前基于单个共享 `asyncio.Queue` 消费事件。多个协程并行读取时会静默丢事件，公开接口语义与真实行为不一致。
- 公开 `runtime` 直接暴露内部可变连接状态：`sdks/python/src/devhub_sdk/client.py` `DevHubClient.runtime`（搜索 `return self._connection_info.runtime`）、`sdks/python/src/devhub_sdk/events.py` `DevHubEventsClient.runtime`（搜索 `return self._connection_info.runtime`）以及 `sdks/python/src/devhub_sdk/models.py` `RuntimeConnectionInfo`（搜索 `rpc_endpoint`）当前把内部 `HubRuntime` 实例直接暴露给外部。调用方修改 `http_base_url` / `ws_url` 后会直接影响后续请求端点，造成状态污染甚至把 token 发往错误地址。

### Monitor

- Bootstrap 状态机对“Host 可用”的判定前后端不一致：`apps/monitor/src-tauri/src/discovery.rs` `DiscoveryCoordinator::run_loop` 只要 `hub.ping` 成功就发布 `host_available` 并停止扫描；但 `apps/monitor/src/lib/monitor-ui.ts` `getUnsupportedRuntimeMessage` 会继续按 `hubVersion >= 0.7.0` 拒绝连接，`apps/monitor/src/components/AppShell.tsx` `HomeDiscoveryWorkspace` 又不会在该状态下提供恢复动作，导致 UI 可能停在“当前 Host 版本不受支持”的死胡同。应把兼容性判定收敛到后端 bootstrap 状态机，避免前端二次否决。
- Bootstrap 初始化存在“先取快照、后订阅事件”的丢状态窗口：`apps/monitor/src/hooks/useBootstrapFlow.ts` `initialize` 先执行 `getBootstrapState/getSettingsSnapshot`，再注册 `BOOTSTRAP_STATE_CHANGED_EVENT/SETTINGS_CHANGED_EVENT`；而 `apps/monitor/src-tauri/src/monitor.rs` `initialize` 会在启动时立即触发 discovery。若状态变化落在两者之间，前端可能长期停留在过期的 bootstrap/settings，直到下一次事件才恢复。需要改成先订阅再补拉最终快照，或用 generation 做补偿。
- Host session 恢复条件过窄：`apps/monitor/src/lib/monitor-ui.ts` `shouldRecoverHostSession` 只把 `Unauthorized/Forbidden` 视为需要恢复；但 `sdks/javascript/src/http-transport.ts` `JsonRpcHttpTransport.send` 在超时、Host 重启、端口失效、非 200 响应、响应损坏时抛出的都是普通 `Error`。这样 `apps/monitor/src/hooks/useHostSession.ts` 的定义读取、列表刷新、提交等路径在真实断连时往往只会显示错误，不会自动回到 rediscovery。
- Definition 工作区的异步加载缺少会话版本隔离：`apps/monitor/src/hooks/useDefinitionEditor.ts` 中 `openEditDefinitionWorkspace/openInstanceDefinitionWorkspace` 会在异步请求完成后直接回写状态，但没有在返回时校验 `sessionResetVersion` 或做取消处理。若请求期间发生 Host 重连、数据目录切换或 session reset，旧会话返回的数据仍可能覆盖当前 UI，形成 stale definition 污染。
- 过期 discovery 循环可能覆盖更新后的 bootstrap 状态：`apps/monitor/src-tauri/src/discovery.rs` `DiscoveryCoordinator::run_loop`（搜索 `build_launch_available_snapshot`）与 `apps/monitor/src-tauri/src/snapshot.rs` `SnapshotPublisher::publish`（搜索 `snapshot.clone()`）当前只在部分分支检查 generation。旧扫描任务晚到时仍可能把新的 bootstrap 快照覆盖成过期的 `launch_available`。
- 损坏的设置文件会直接阻断 Monitor 启动：`apps/monitor/src-tauri/src/settings.rs` `SettingsStore::load`（搜索 `serde_json::from_str::<MonitorSettings>`）把本地 `settings.json` 解析失败当成致命错误，而 `apps/monitor/src-tauri/src/lib.rs` `run`（搜索 `failed to initialize monitor state`）又直接 `expect`。一份损坏或手工改坏的设置文件就会让整个 Monitor 无法启动，也没有恢复路径。
- 启动中的 Host 会因为保存设置而过早清除去重保护：`apps/monitor/src-tauri/src/discovery.rs` `DiscoveryCoordinator::restart`（搜索 `if reason != "launch_requested"`）与 `apps/monitor/src-tauri/src/monitor.rs` `save_settings`（搜索 `finish_launch_attempt`）会在保存设置或切换数据目录时直接清掉 launch attempt 状态。这样用户在首个 Host 仍在启动时再次保存设置并重试启动，就可能拉起重复 Host 进程。


## 待实现功能

### Host

- 获取 host 的版本
- hub.apps.getInstance 获取 app 实例的详细信息？

### Monitor

#### 增加【测试】页面

- 发送请求
- 显示回报内容
