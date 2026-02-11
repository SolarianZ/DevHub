# DevHub M4细化任务文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M3细化任务文档.md](./DevHub_M3细化任务文档.md)
> - [DevHub_M3测试任务拆分文档.md](./DevHub_M3测试任务拆分文档.md)

## 当前状态（截至 2026-02-08）

- M4 总体状态：**已启动并完成首轮实现（WS 认证 + 事件链路）**。
- M3 基线保持不变：HTTP `/rpc` 既有行为、scope 路由与 M3 回归能力未被修改。
- 本轮交付覆盖：
  - Host 侧 `/ws` 接入与连接生命周期管理。
  - `hub.ws.authenticate` 首条消息约束与鉴权闭环。
  - `hub.events.subscribe/unsubscribe` 与连接绑定订阅模型。
  - `hub.event` 服务端通知推送。
  - 事件发布链路：`register/unregister`、`queued/delivered/completed/failed`。

---

## 0. M4 目标与验收边界

### 0.1 M4 必须实现
- [x] 暴露 WebSocket 端点 `/ws`，并保持 `/rpc` 行为不变。
- [x] 强制 `hub.ws.authenticate` 为首条消息，且必须是 JSON-RPC 请求（带 `id`）。
- [x] 鉴权前处理符合 Spec §3.3/§4.3：
  - [x] 非鉴权请求（带 `id`）返回 `-32001 unauthorized`（实现加严：返回后断连）。
  - [x] 非鉴权通知（无 `id`）关闭连接。
- [x] 完成 WS 鉴权参数校验与错误映射：
  - [x] token 无效 -> `-32001 unauthorized`。
  - [x] 协议版本不匹配 -> `-32099 not_supported`。
  - [x] `unauthorized` 的 `error.data.reason` 对齐为 `missing_token/invalid_token`。
- [x] 落地订阅模型：
  - [x] `hub.events.subscribe` 支持全量/按类型订阅。
  - [x] `hub.events.unsubscribe` 幂等。
  - [x] 连接断开后自动清理订阅。
- [x] 落地事件交付：按 Spec §6.3.16 通过 `hub.event` 通知推送。

### 0.2 M4 事件类型范围（v1）
- [x] `app.instance.registered`
- [x] `app.instance.unregistered`
- [x] `invocation.queued`
- [x] `invocation.delivered`
- [x] `invocation.completed`
- [x] `invocation.failed`

### 0.3 非 M4 范围
- 不实现事件重放、持久化订阅、跨 Hub 重连恢复（v2 范围）。
- 不调整 invocation 持久化模型（仍为内存态）。
- 不修改 Spec 文档定义（仅实现与测试、文档对齐）。

---

## 1. 架构落点与代码变更清单

### 1.1 核心新增
- [x] `src/DevHub.Core/Services/Events/HubEventBus.cs`
  - 连接注册/移除、鉴权状态、订阅管理、投递队列。
- [x] `src/DevHub.Core/Services/Events/HubEventMessage.cs`
  - 统一事件消息模型。
- [x] `src/DevHub.Core/Services/Events/HubEventDelivery.cs`
  - 统一投递描述模型。

### 1.2 DI 与宿主接入
- [x] `src/DevHub.Core/Extensions/ServiceCollectionExtensions.cs`
  - 注册 `HubEventBus` 单例。
- [x] `src/DevHub.Host/Program.cs`
  - 增加 `app.UseWebSockets()`。
  - 增加 `/ws` 端点与连接处理循环。
  - 增加 WS 协议处理辅助函数（鉴权、订阅、通知发送、帧收发）。

### 1.3 业务事件发布链路
- [x] `src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`
  - 注册成功发布 `app.instance.registered`。
  - 实际注销成功发布 `app.instance.unregistered`。
- [x] `src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`
  - `notify/request` 入队发布 `invocation.queued`。
  - `respond` 成功后按 value/error 发布 `invocation.completed` / `invocation.failed`。
- [x] `src/DevHub.Core/Services/Invocation/InvocationStore.cs`
  - `poll` 认领租约时发布 `invocation.delivered`。

---

## 2. WS 协议实现要点（落地规则）

### 2.1 鉴权前规范行为
- [x] JSON 解析失败返回 `-32700 parse_error`（`id:null`），并在未鉴权阶段关闭连接。
- [x] JSON-RPC 信封非法返回 `-32600 invalid_request`，并在未鉴权阶段关闭连接。
- [x] 首条非 `hub.ws.authenticate`：
  - [x] 请求（有 `id`）返回 `-32001 unauthorized` 后关闭连接（实现加严）；
  - [x] 通知（无 `id`）直接关闭连接。
- [x] 未鉴权门禁优先级高于 `hub.*` 参数形态校验：
  - [x] 未鉴权请求（`params=[]`）仍返回 `unauthorized` 并断连；
  - [x] 未鉴权通知（`params=[]`）仍直接断连。

### 2.2 鉴权约束
- [x] `hub.ws.authenticate` 参数：`token/protocolVersion/clientId/clientSessionId`。
- [x] `clientSessionId` 按 UUID 校验。
- [x] 鉴权成功后返回 `{ "ok": true, "protocolVersion": 1 }`。

### 2.3 订阅与交付
- [x] `types` 省略/null/空数组 => 订阅全部事件。
- [x] `types` 包含未知事件类型 => `-32602 invalid_params`。
- [x] `hub.event` 通知字段包含：`subscriptionId/type/timeUtc/payload`。

---

## 3. 开发完成定义（M4 DoD）

### 3.1 代码实现
- [x] `/ws` 与 WS 生命周期管理代码已落地。
- [x] 鉴权、订阅、事件推送主链路已打通。
- [x] Host 与 Core 的依赖装配已完成。

### 3.2 测试与文档
- [x] 白盒测试：事件总线与发布钩子覆盖已补齐。
- [x] 黑盒测试：WS 首条认证、断线清理、事件流覆盖已补齐。
- [x] 黑盒测试补充未鉴权 `params=[]` 防回归用例（M4-WS-009/010）。
- [x] runner 已纳入 M4 模块执行。
- [x] 新增 M4 细化与测试拆分文档。

---

## 4. 后续建议（面向 M4 收尾）

- 在 full 回归中持续观察 WS 连接高并发下的事件丢弃行为。
- 评估是否在 v1.x 增加可选 `maxPendingEvents` 保护参数（不改协议字段）。
- 为 M5 SDK 增加统一 WS 客户端封装（鉴权、重连、订阅恢复策略）。

---

## 5. M4 收尾补充：DI 兼容构造收敛（2026-02-11）

### 5.1 完成项
- [x] 删除 Core 侧 8 个兼容构造，统一仅保留主构造（显式依赖注入）：
  - [x] `AppRegistry(ILogger<...>)`
  - [x] `AppInstancesHandler(AppRegistry, ILogger, eventBus?)`
  - [x] `InvocationStore(ILogger, routingService, eventBus?)`
  - [x] `InvocationHandler(..., ILogger, eventBus?)`
  - [x] `FileSystemManager(ILogger, string? definitionsPath)`
  - [x] `FileSystemManager(ILogger)`
  - [x] `RuntimeHttpBaseUrlProvider(ILogger)`
  - [x] `LaunchCoordinator(..., ILogger)`（内部默认 `ProcessLauncher/SystemClock`）
- [x] 全量迁移仓内调用点到主构造，测试与 Host 测试均改为显式传入 `SystemClock`、`ProcessLauncher`、`RuntimePathOptions`。
- [x] 新增 `src/DevHub.Tests/GlobalUsings.cs`，统一测试工程对 `DevHub.Core.Services.Abstractions` 的可见性，避免重复 `using`。

### 5.2 影响范围
- Core 改造文件：
  - `src/DevHub.Core/Services/AppRegistry.cs`
  - `src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`
  - `src/DevHub.Core/Services/Invocation/InvocationStore.cs`
  - `src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`
  - `src/DevHub.Core/Services/FileSystemManager.cs`
  - `src/DevHub.Core/Services/Invocation/RuntimeHttpBaseUrlProvider.cs`
  - `src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs`
- 测试与辅助构造文件：`src/DevHub.Tests/*`、`src/DevHub.Host.Tests/TestHelpers/HostTestContextFactory.cs`。
- 本次未修改 `Spec.md`，未改变协议字段、错误码语义与运行时数据格式。

### 5.3 验证记录
- 已执行并通过：
  - `dotnet build src/DevHub.slnx -c Release`
  - `dotnet test src/DevHub.Tests/DevHub.Tests.csproj -c Release --no-build`
  - `dotnet test src/DevHub.Host.Tests/DevHub.Host.Tests.csproj -c Release --no-build`
- 黑盒 `python3 tests/test_runner.py --fast --no-header`：
  - 在当前沙箱环境下连接本地 Host 端口时报 `Operation not permitted/Connection refused`，未形成可用结果；
  - 该失败表现为环境限制，不属于本次 DI 收敛改造引入的编译/单测回归。

---

## 6. M4 收尾补充：协议一致性收敛（2026-02-11）

### 6.1 完成项
- [x] `docs/Spec.md` 调整 Invocation 模型：`args` 允许任意 JSON 值（`object/array/string/number/boolean/null`）。
- [x] `src/DevHub.Host/RpcHttpEndpointHandler.cs` 对 HTTP 通知（无 `id`）改为“不返回 JSON-RPC 响应”，仅保留服务端处理与日志。

### 6.2 影响范围
- 协议文档：
  - `docs/Spec.md`
- Host 接入层：
  - `src/DevHub.Host/RpcHttpEndpointHandler.cs`
- 本次未改动调用路由、错误码映射与 WS 行为。
