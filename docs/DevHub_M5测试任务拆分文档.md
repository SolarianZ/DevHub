# DevHub M5测试任务拆分文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M5细化任务文档.md](./DevHub_M5细化任务文档.md)
> - [DevHub_黑盒测试Spec严格符合性审查报告.md](./DevHub_黑盒测试Spec严格符合性审查报告.md)

## 当前状态（截至 2026-03-09）

- M5 测试任务状态：`.NET SDK` 子范围已落地，已完成 SDK 单测与 SDK↔Hub 黑盒集成测试；TS SDK、conformance 与跨语言一致性任务仍待后续阶段完成。
- M1~M4 的 Hub 白盒/黑盒体系已稳定，可作为 M5 SDK 验证基线。
- 下文涉及的 .NET SDK 单元测试路径统一为 `sdks/dotnet/tests/DevHub.Sdk.UnitTests/`，SDK↔Hub 黑盒场景当前落在 `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/`；`sdk/devhub-sdk-ts/tests/` 与 `tests/conformance/` 仍为后续目标测试资产。
- M5 测试目标：建立“SDK 单测 + SDK↔Hub 黑盒 + 向量契约一致性”三层闭环。
- 2026-03-09 已验证：`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 可通过（`.NET SDK` 40 条单元测试 + 14 条集成测试）。

---

## 0. 测试目标

### 0.1 覆盖目标（M5 必测）

- [ ] 覆盖 Discovery：`hub.json` 解析成功、缺失、字段非法。
- [ ] 覆盖 Auth：missing/invalid token、missing/mismatch protocol、missing `clientId/clientSessionId`、`clientSessionId` 格式非法。
- [ ] 覆盖 RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*` 全方法成功与关键错误码。
- [ ] 覆盖 WS：首条 `hub.ws.authenticate`、`subscribe/unsubscribe`、unknown type -> `-32602`、断线清理。
- [ ] 覆盖 Invocation 错误路径：`invocation_timeout`、`invocation_expired`、`delivery_conflict`、`invocation_failed`。
- [ ] 覆盖 Scope 规则：默认 global、显式 scope 不回退、`target.scope=""` 映射 global、`target.scope="global"` 作为显式字符串作用域合法。
- [ ] 覆盖跨 SDK 一致性：同向量在 .NET 与 TS 结果语义等价。

### 0.2 测试分层

- 白盒（SDK Unit）：
  - SDK 序列化、参数校验、错误映射、连接生命周期。
- 黑盒（SDK↔Hub E2E）：
  - 真实启动 Hub，验证 HTTP/WS 全链路行为。
- 契约（Conformance Vectors）：
  - 同一向量同时跑 .NET SDK 与 TS SDK，比对语义一致性（忽略 JSON 键序与空白）。

---

## 1. 用例编号规则（固定）

- `.NET 单测`：`M5-DN-UT-xxx`
- `TS 单测`：`M5-TS-UT-xxx`
- `契约测试`：`M5-CONF-xxx`
- `端到端 SDK↔Hub`：`M5-E2E-xxx`

---

## 2. 用例矩阵（M5 -> 测试文件映射）

| 用例编号 | 场景 | 类型 | 责任文件 |
| --- | --- | --- | --- |
| `M5-DN-UT-001` | `hub.json` 正常解析并读取 `tokenFile` | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Discovery/RuntimeDiscoveryTests.cs` |
| `M5-DN-UT-002` | `hub.json` 缺失字段（`httpBaseUrl/wsUrl/tokenFile`）抛出统一异常 | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Discovery/RuntimeDiscoveryTests.cs` |
| `M5-DN-UT-003` | HTTP 鉴权头组装与 `protocol/clientId/clientSessionId` 必填校验 | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Transport/HttpTransportTests.cs` |
| `M5-DN-UT-004` | RPC 错误响应映射为 `DevHubRpcException` | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Rpc/RpcErrorMappingTests.cs` |
| `M5-DN-UT-005` | WS 首条必须认证（未认证调用被拒绝） | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Events/WsLifecycleTests.cs` |
| `M5-DN-UT-006` | `target.scope` 与 `target.instanceId` 默认值构造逻辑 | .NET 白盒 | `sdks/dotnet/tests/DevHub.Sdk.UnitTests/Rpc/InvocationRequestBuilderTests.cs` |
| `M5-TS-UT-001` | Node runtime discovery 成功路径 | TS 白盒 | `sdk/devhub-sdk-ts/tests/discovery.test.ts` |
| `M5-TS-UT-002` | discovery 缺失/非法字段错误映射 | TS 白盒 | `sdk/devhub-sdk-ts/tests/discovery.test.ts` |
| `M5-TS-UT-003` | HTTP header 与 `protocolVersion/clientId/clientSessionId` 校验 | TS 白盒 | `sdk/devhub-sdk-ts/tests/http-transport.test.ts` |
| `M5-TS-UT-004` | JSON-RPC 错误映射为 `DevHubRpcError` | TS 白盒 | `sdk/devhub-sdk-ts/tests/rpc-error.test.ts` |
| `M5-TS-UT-005` | WS 连接鉴权生命周期与事件流中断处理 | TS 白盒 | `sdk/devhub-sdk-ts/tests/ws-events.test.ts` |
| `M5-TS-UT-006` | `hub.invoke.respond` 的 `value/error` 互斥参数构造 | TS 白盒 | `sdk/devhub-sdk-ts/tests/invocation-builder.test.ts` |
| `M5-E2E-001` | SDK `Ping` 正向调用闭环（HTTP） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` |
| `M5-E2E-002` | SDK `apps.*` 管理链路（register/list/heartbeat/unregister/launch） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` / `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/LaunchFlowTests.cs` |
| `M5-E2E-003` | SDK `invoke.notify/request/poll/respond` 主链路 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-E2E-004` | SDK WS 认证 + 订阅 + 取消订阅 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Events/EventsFlowTests.cs` |
| `M5-E2E-005` | unknown event type 订阅返回 `-32602 invalid_params` | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Events/EventsFlowTests.cs` |
| `M5-E2E-006` | scope 默认 global 与显式 scope 不回退验证 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-E2E-007` | `invocation_timeout` / `invocation_expired` 错误路径 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-E2E-008` | `delivery_conflict` / `invocation_failed` 错误路径 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-E2E-009` | 缺失 `X-DevHub-ClientId` 返回 `-32600 invalid_request` | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` |
| `M5-E2E-010` | WS 断开后订阅状态清理（重连后需重新订阅） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Events/EventsFlowTests.cs` |
| `M5-E2E-011` | `target.scope=""` 路由至 global，`target.scope="global"` 仅命中字面量作用域 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-CONF-001` | Discovery 类向量执行与断言 | 契约 | `tests/conformance/v1.0.1/discovery.*.json` |
| `M5-CONF-002` | Auth 类向量执行与断言 | 契约 | `tests/conformance/v1.0.1/auth.*.json` |
| `M5-CONF-003` | AppDef/AppInstance 向量执行与断言 | 契约 | `tests/conformance/v1.0.1/apps.*.json` |
| `M5-CONF-004` | Notify/Request 向量执行与断言 | 契约 | `tests/conformance/v1.0.1/invocation.*.json` |
| `M5-CONF-005` | .NET 与 TS 对同向量结果语义比较 | 契约 | `tests/conformance/vector_runner.py` |
| `M5-CONF-006` | 契约失败报告输出（向量ID/实际/期望/差异字段） | 契约 | `tests/conformance/vector_runner.py` |
| `M5-CONF-007` | Events 断连清理向量执行与断言 | 契约 | `tests/conformance/v1.0.1/events.*.json` |
| `M5-CONF-008` | Spec §8 全量错误码与 `error.data` 字段向量断言 | 契约 | `tests/conformance/v1.0.1/errors.*.json` |

---

## 3. 契约向量最小计数（Spec §10.1）

| 分类 | 最小计数 | 任务状态 |
| --- | ---: | --- |
| Discovery | 3 | [ ] |
| Auth | 5 | [ ] |
| AppDef | 4 | [ ] |
| AppInstance | 8 | [ ] |
| Notify | 6 | [ ] |
| Request | 10 | [ ] |
| Events | 4 | [ ] |
| Error | 12 | [ ] |
| **总计** | **52** | [ ] |

### 3.1 向量格式约束（Spec §10.2）

每条向量必须包含以下字段：

- `id`
- `description`
- `transport`
- `request`
- `expectedResponse`
- `tags`

说明：若为 HTTP 场景，可额外包含 `http.headers`；若为 WS 场景，可增加 `ws` 配置字段，但不得删除上述核心字段。

- Events 类向量必须包含“断开连接后订阅清理”场景。
- Error 类向量必须覆盖 Spec §8.1 + §8.2 全量错误码与关键 `error.data` 字段。

---

## 4. 任务拆分

### 4.1 .NET SDK 测试任务

- [x] 创建 `DevHub.Sdk.UnitTests` 与 `DevHub.Sdk.IntegrationTests` 工程并接入 `sdks/dotnet/DevHub.DotNetSdk.slnx`。
- [x] 先完成 Discovery/Auth/错误映射白盒，再补全 apps/invoke/events API 白盒。
- [x] 增加对 `DevHubRpcException` 字段完整性断言（`Code/Message/Data/RequestId`）。

### 4.2 TS SDK 测试任务

- [ ] 在 `sdk/devhub-sdk-ts/tests/` 建立 Discovery/HTTP/WS/Invocation 测试模块。
- [ ] 统一错误断言对象结构（`code/message/data/requestId`）。
- [ ] 在 Node 环境下完成事件流读取（`AsyncIterator`）稳定性校验。

### 4.3 SDK↔Hub 黑盒任务

- [x] 以隔离 runtime/appdefs 目录启动 Host，执行 SDK 调用闭环。
- [x] 覆盖 `hub.apps.launch` 主链路（含 `already_running/starting/started` 状态）。
- [x] 覆盖必测错误路径：`invocation_timeout/invocation_expired/delivery_conflict/invocation_failed`。
- [x] 覆盖 scope MUST 规则与 WS unknown type 错误映射。
- [x] 覆盖 `X-DevHub-ClientId` 缺失返回 `-32600 invalid_request`。
- [x] 覆盖 WS 断线清理后重连行为（需重新订阅才可收事件）。

### 4.4 契约测试任务

- [ ] 生成 52 条最小向量，按分类落盘。
- [ ] `vector_runner.py` 同时驱动 .NET 与 TS SDK，逐向量比对。
- [ ] 报告中必须输出失败差异字段，支持快速定位跨实现偏差。
- [ ] Events 类向量显式包含断开连接清理场景。
- [ ] Error 类向量显式覆盖 Spec §8 全量错误码与 `error.data` 关键字段。

---

## 5. Runner / CI 接入要求

- CI 必须同时执行：
  - `dotnet test`（含 SDK 测试）
  - `npm test`（TS SDK）
  - `python3 tests/conformance/vector_runner.py`
- 失败报告必须包含：
  - 向量 ID
  - 实际响应
  - 期望响应
  - 差异字段
- 推荐新增阶段：
  - `sdk-dotnet-tests`
  - `sdk-ts-tests`
  - `sdk-conformance`

---

## 6. 完成定义（DoD）

- [ ] 四类编号用例（`M5-DN-UT` / `M5-TS-UT` / `M5-E2E` / `M5-CONF`）已建立并可自动执行。
- [ ] Spec §10.1 最小 52 条向量全部落地且可回归。
- [ ] .NET 与 TS 对同向量结果语义一致（忽略键序与空白）。
- [ ] CI 已接入三类执行入口并作为门禁。
- [ ] 文档与测试实现一致，且未修改 `docs/Spec.md`。

---

## 7. 假设与默认选择

- 默认同时推进 .NET 与 JS/TS SDK，能力覆盖保持同构。
- 默认 TS SDK 目标为 Node.js，不承诺浏览器运行时 discovery。
- 默认协议固定为 `1`，不引入 v2 兼容分支逻辑。
- 默认签名向量是跨 SDK 一致性的唯一事实来源。
