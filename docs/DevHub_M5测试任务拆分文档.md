# DevHub M5测试任务拆分文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M5细化任务文档.md](./DevHub_M5细化任务文档.md)
> - [DevHub_黑盒测试Spec严格符合性审查报告.md](./DevHub_黑盒测试Spec严格符合性审查报告.md)

## 当前状态（截至 2026-03-26）

- M5 测试任务状态：`.NET SDK`、`JS/TS SDK` 与 `Python SDK` 主体能力已落地，已完成各自的 SDK 单测、SDK↔Hub 黑盒集成测试与 conformance 契约回归；跨语言一致性已在 52 条向量上完成验证，CI 门禁接入仍待后续阶段推进。
- M1~M4 的 Hub 白盒/黑盒体系已稳定，可作为 M5 SDK 验证基线。
- `Python SDK` 当前已在 `sdks/python/tests/` 下落地 runtime、payloads、parsing、HTTP、events、package exports 等单元测试模块，以及 HTTP / Invocation / Events SDK↔Hub 集成测试，并已接入共享 conformance runner 与跨语言一致性验证链路。
- 下文涉及的 .NET SDK 单元测试路径为 `sdks/dotnet/tests/DevHub.Sdk.UnitTests/`，SDK↔Hub 黑盒场景位于 `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/`；`sdks/javascript/tests/` 承载 TS SDK 单测与 SDK↔Hub 黑盒场景，`sdks/python/tests/` 承载 Python SDK 单测与 SDK↔Hub 黑盒场景，`host/tests/conformance/` 承载当前版本的跨语言契约测试资产。
- M5 测试目标：建立“SDK 单测 + SDK↔Hub 黑盒 + 向量契约一致性”三层闭环。
- 2026-03-09 已验证：`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 可通过（`.NET SDK` 40 条单元测试 + 14 条集成测试）。
- 2026-03-11 已验证：`sdks/javascript` 在 Node 24 下执行 `npm run build && npm test` 可通过（7 个测试文件 / 45 条测试），并通过 `python3 host/tests/test_runner.py --smoke --no-header` 冒烟回归。
- 2026-03-24 已完成：将原 `Python SDK` 独立设计规划中的测试基线并入本 M5 测试文档，与 `.NET` / `JS/TS` / `Python` SDK 采用统一粒度维护。
- 2026-03-25 已完成：Discovery/Auth/AppDef/AppInstance 共 20 条向量已可直接由 `vector_runner.py` 跑通；runner v1 已支持向量级 `setup` 与自动 teardown。
- 2026-03-25 已完成：Invocation Notify 6 + Request 10 共 16 条向量已落地并跑通，累计 36/52；runner v2 已支持 Invocation 向量的 per-SDK 独立沙箱、`orchestration` 三阶段编排，以及中立 raw-protocol helper 模拟被调用方。
- 2026-03-26 已完成：WS Events 4 + Error 12 共 16 条向量已落地并跑通，累计达到 52/52；runner v3 已支持 raw HTTP 字符串/数组请求、`raw.ws`、`sdk.events`、失败快照输出与 suite host 日志回溯。已验证 `dotnet test host/src/DevHub.slnx -c Release`、`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`npm --prefix sdks/javascript run build`、`npm --prefix sdks/javascript test`、`python -m pytest sdks/python/tests`、`python host/tests/conformance/vector_runner.py` 与隔离 Host 下的 `python host/tests/test_runner.py --smoke --no-header` 全部通过。

---

## 0. 测试目标

### 0.1 覆盖目标（M5 必测）

- [x] 覆盖 Discovery：`hub.json` 解析成功、缺失、字段非法。
- [x] 覆盖 Auth：missing/invalid token、missing/mismatch protocol、missing `clientId/clientSessionId`、`clientSessionId` 格式非法。
- [x] 覆盖 RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*` 全方法成功与关键错误码。
- [x] 覆盖 WS：首条 `hub.ws.authenticate`、`subscribe/unsubscribe`、unknown type 规范拒绝路径、断线清理。
- [x] 覆盖 Invocation 错误路径：`invocation_timeout`、`invocation_expired`、`delivery_conflict`、`invocation_failed`。
- [x] 覆盖 Scope 规则：默认 global、显式 scope 不回退、`target.scope=""` 映射 global、`target.scope="global"` 作为显式字符串作用域合法。
- [x] 覆盖跨 SDK 一致性：同向量在 `.NET`、`TS` 与 `Python` 结果语义等价。

### 0.2 测试分层

- 白盒（SDK Unit）：
  - SDK 序列化、参数校验、错误映射、连接生命周期。
- 黑盒（SDK↔Hub E2E）：
  - 真实启动 Hub，验证 HTTP/WS 全链路行为。
- 契约（Conformance Vectors）：
  - 同一向量同时跑 `.NET SDK`、`TS SDK` 与 `Python SDK`，比对语义一致性（忽略 JSON 键序与空白）。

---

## 1. 用例编号规则（固定）

- `.NET 单测`：`M5-DN-UT-xxx`
- `TS 单测`：`M5-TS-UT-xxx`
- `Python 单测`：`M5-PY-UT-xxx`
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
| `M5-TS-UT-001` | Node runtime discovery 成功路径 | TS 白盒 | `sdks/javascript/tests/unit/runtime.test.ts` |
| `M5-TS-UT-002` | discovery 缺失/非法字段错误映射 | TS 白盒 | `sdks/javascript/tests/unit/runtime.test.ts` |
| `M5-TS-UT-003` | HTTP header 与 `protocolVersion/clientId/clientSessionId` 校验 | TS 白盒 | `sdks/javascript/tests/unit/client.test.ts` |
| `M5-TS-UT-004` | JSON-RPC 错误映射为 `DevHubRpcError` | TS 白盒 | `sdks/javascript/tests/unit/client.test.ts` |
| `M5-TS-UT-005` | WS 连接鉴权生命周期与事件流中断处理 | TS 白盒 | `sdks/javascript/tests/unit/events-client.test.ts` |
| `M5-TS-UT-006` | `hub.invoke.respond` 的 `value/error` 互斥参数构造 | TS 白盒 | `sdks/javascript/tests/unit/client.test.ts` |
| `M5-PY-UT-001` | runtime discovery 成功路径、默认数据根目录派生、`DEVHUB_DATA_DIR` 覆盖与 `tokenFile` 读取 | Python 白盒 | `sdks/python/tests/unit/test_runtime.py` |
| `M5-PY-UT-002` | dataDir 输入约束、`runtime/` 子目录误传、直放 `hub.json` 布局与 `ClientOptions` 非法值校验 | Python 白盒 | `sdks/python/tests/unit/test_runtime.py` |
| `M5-PY-UT-003` | HTTP header 组装、本地参数校验、错误映射与响应结构严格解析 | Python 白盒 | `sdks/python/tests/unit/test_http_client.py` |
| `M5-PY-UT-004` | `notify/request/poll/respond` 载荷默认值、`args` 省略/null 语义、互斥约束与 JSON 校验 | Python 白盒 | `sdks/python/tests/unit/test_payloads.py` |
| `M5-PY-UT-005` | 模型解析、已知错误码辅助字段、`calleeError` 提取与 JSON-RPC envelope 校验 | Python 白盒 | `sdks/python/tests/unit/test_parsing.py` / `sdks/python/tests/unit/test_exceptions.py` / `sdks/python/tests/unit/test_jsonrpc.py` |
| `M5-PY-UT-006` | WS 鉴权生命周期、未知事件类型拒绝、连接终止语义与包根扩展点导出 | Python 白盒 | `sdks/python/tests/unit/test_events_client.py` / `sdks/python/tests/unit/test_package_exports.py` |
| `M5-E2E-001` | SDK `Ping` 正向调用闭环（HTTP） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` / `sdks/javascript/tests/integration/http-flow.test.ts` / `sdks/python/tests/integration/test_http_flow.py` |
| `M5-E2E-002` | SDK `apps.*` 管理链路（register/list/heartbeat/unregister/launch） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` / `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/LaunchFlowTests.cs` / `sdks/javascript/tests/integration/http-flow.test.ts` / `sdks/python/tests/integration/test_http_flow.py` |
| `M5-E2E-003` | SDK `invoke.notify/request/poll/respond` 主链路 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` / `sdks/javascript/tests/integration/invocation-flow.test.ts` / `sdks/python/tests/integration/test_invocation_flow.py` |
| `M5-E2E-004` | SDK WS 认证 + 订阅 + 取消订阅 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Events/EventsFlowTests.cs` / `sdks/javascript/tests/integration/events-flow.test.ts` / `sdks/python/tests/integration/test_events_flow.py` |
| `M5-E2E-005` | unknown event type 订阅返回 `-32602 invalid_params` 或由客户端按规范本地拒绝 | SDK 黑盒 | `sdks/javascript/tests/integration/events-flow.test.ts` / `sdks/python/tests/integration/test_events_flow.py` |
| `M5-E2E-006` | scope 默认 global 与显式 scope 不回退验证 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` / `sdks/javascript/tests/integration/invocation-flow.test.ts` / `sdks/python/tests/integration/test_invocation_flow.py` |
| `M5-E2E-007` | `invocation_timeout` / `invocation_expired` 错误路径 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` / `sdks/javascript/tests/integration/invocation-flow.test.ts` / `sdks/python/tests/integration/test_invocation_flow.py` |
| `M5-E2E-008` | `delivery_conflict` / `invocation_failed` 错误路径 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` / `sdks/javascript/tests/integration/invocation-flow.test.ts` / `sdks/python/tests/integration/test_invocation_flow.py` |
| `M5-E2E-009` | 缺失 `X-DevHub-ClientId` 返回 `-32600 invalid_request` | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` |
| `M5-E2E-010` | WS 断开后订阅状态清理（重连后需重新订阅） | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Events/EventsFlowTests.cs` / `sdks/javascript/tests/integration/events-flow.test.ts` / `sdks/python/tests/integration/test_events_flow.py` |
| `M5-E2E-011` | `target.scope=""` 路由至 global，`target.scope="global"` 仅命中字面量作用域 | SDK 黑盒 | `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/InvocationFlowTests.cs` |
| `M5-CONF-001` | Discovery 类向量执行与断言 | 契约 | `host/tests/conformance/v1.0.1/discovery.*.json` |
| `M5-CONF-002` | Auth 类向量执行与断言 | 契约 | `host/tests/conformance/v1.0.1/auth.*.json` |
| `M5-CONF-003` | AppDef/AppInstance 向量执行与断言 | 契约 | `host/tests/conformance/v1.0.1/apps.*.json` |
| `M5-CONF-004` | Notify/Request 向量执行与断言 | 契约 | `host/tests/conformance/v1.0.1/invocation.*.json` |
| `M5-CONF-005` | `.NET`、`TS` 与 `Python` 对同向量结果语义比较 | 契约 | `host/tests/conformance/vector_runner.py` |
| `M5-CONF-006` | 契约失败报告输出（向量ID/实际/期望/差异字段） | 契约 | `host/tests/conformance/vector_runner.py` |
| `M5-CONF-007` | Events 断连清理向量执行与断言 | 契约 | `host/tests/conformance/v1.0.1/events.*.json` |
| `M5-CONF-008` | Spec §8 全量错误码与 `error.data` 字段向量断言 | 契约 | `host/tests/conformance/v1.0.1/errors.*.json` |

---

## 3. 契约向量最小计数（Spec §10.1）

| 分类 | 最小计数 | 任务状态 |
| --- | ---: | --- |
| Discovery | 3 | [x] |
| Auth | 5 | [x] |
| AppDef | 4 | [x] |
| AppInstance | 8 | [x] |
| Notify | 6 | [x] |
| Request | 10 | [x] |
| Events | 4 | [x] |
| Error | 12 | [x] |
| **总计** | **52** | [x] |

### 3.1 向量格式约束（Spec §10.2）

每条向量必须包含以下字段：

- `id`
- `description`
- `transport`
- `request`
- `expectedResponse`
- `tags`

说明：若为 HTTP 场景，可额外包含 `http.headers`；若为 WS 场景，可增加 `ws` 配置字段，但不得删除上述核心字段。

- runner v2 允许仓库内扩展字段 `setup`：
  - `setup.definitions`：支持 `{ fileName, definition }` / `{ fileName, rawText }`，写入 suite Host 的 `apps/definitions`。
  - `setup.instances`：支持 `{ state, instance, waitSeconds? }`，通过 HTTP 预注册实例并可等待到离线状态。
  - `setup.dataDir.files`：支持 `{ path, text }` / `{ path, json }`，写入向量私有数据根目录。
- runner 自动 teardown，不单独引入向量级 `teardown` 字段：会删除预置 definition、注销预置 instance，并清理向量临时数据根目录。
- Invocation 类向量允许扩展字段 `orchestration`，固定使用 `beforeCaller` / `duringCaller` / `afterCaller` 三阶段编排。
- `orchestration` 中的协作步骤统一由 runner 内部 raw-protocol helper 执行，当前支持 `register_instance`、`sleep`、`poll_expect_invocation`、`poll_expect_empty`、`respond_value`、`respond_error`。
- Invocation 主链路向量要求 `request.kind` 使用 `sdk.notify` / `sdk.request`，由三语言适配器真正调用 SDK 公共 API；helper 只承担被调用方模拟，不再复用 SDK 当被调用方。
- runner v3 允许 `request` 使用字符串、对象或数组，并对 `transport="http"` / `transport="ws"` 分别走原始 HTTP 与原始 WS 序列化发送，以覆盖 `parse_error`、非法信封、batch root array 与 WS 首包非法文本等场景。
- Events 类向量允许 `request.kind = "sdk.events"`，由三语言适配器统一负责 EventsClient 的认证、订阅、取消订阅、断线重连与事件读取。
- Events 类向量必须包含“断开连接后订阅清理”场景。
- Error 类向量必须覆盖 Spec §8.1 + §8.2 全量错误码与关键 `error.data` 字段。

---

## 4. 任务拆分

### 4.1 .NET SDK 测试任务

- [x] 创建 `DevHub.Sdk.UnitTests` 与 `DevHub.Sdk.IntegrationTests` 工程并接入 `sdks/dotnet/DevHub.DotNetSdk.slnx`。
- [x] 先完成 Discovery/Auth/错误映射白盒，再补全 apps/invoke/events API 白盒。
- [x] 增加对 `DevHubRpcException` 字段完整性断言（`Code/Message/Data/RequestId`）。

### 4.2 TS SDK 测试任务

- [x] 在 `sdks/javascript/tests/` 建立 Discovery/HTTP/WS/Invocation 测试模块。
- [x] 统一错误断言对象结构（`code/message/data/requestId`）。
- [x] 在 Node 环境下完成事件流读取（`AsyncIterator`）稳定性校验。

### 4.3 Python SDK 测试任务

- [x] 在 `sdks/python/tests/unit/` 建立 runtime、payloads、parsing、HTTP、events 与 package exports 测试模块。
- [x] 统一验证本地 JSON 校验、严格响应解析，以及 `DevHubRpcException` 的辅助字段读取语义。
- [x] 在异步事件客户端中覆盖认证前约束、未知事件类型拒绝、连接终止后重认证与重订阅要求。

### 4.4 SDK↔Hub 黑盒任务

- [x] 以隔离数据根目录启动 Host，执行 SDK 调用闭环，避免依赖默认常驻 Hub。
- [x] 覆盖 `hub.apps.launch` 主链路（含 `already_running/starting/started` 状态）。
- [x] 覆盖必测错误路径：`invocation_timeout/invocation_expired/delivery_conflict/invocation_failed`。
- [x] 覆盖 scope MUST 规则与 WS unknown type 规范拒绝路径。
- [x] 覆盖 `X-DevHub-ClientId` 缺失返回 `-32600 invalid_request`。
- [x] 覆盖 WS 断线清理后重连行为（需重新订阅才可收事件）。

### 4.5 契约测试任务

- [x] 生成 52 条最小向量，按分类落盘（已完成 52/52：Discovery 3 + Auth 5 + AppDef 4 + AppInstance 8 + Notify 6 + Request 10 + Events 4 + Error 12）。
- [x] `vector_runner.py` 同时驱动 `.NET`、`TS` 与 `Python` SDK，逐向量比对。
- [x] 报告中必须输出失败差异字段，支持快速定位跨实现偏差，并在失败时输出快照目录。
- [x] Events 类向量显式包含断开连接清理场景。
- [x] Error 类向量显式覆盖 Spec §8 全量错误码与 `error.data` 关键字段。
- [x] 为 runner 增加失败快照机制，保存失败向量、实际结果与比较产物。

---

## 5. Runner / CI 接入要求

- CI 必须同时执行：
  - `dotnet test`（含 SDK 测试）
  - `npm test`（TS SDK）
  - `python3 -m pytest sdks/python/tests`
  - `python3 host/tests/conformance/vector_runner.py`
- 失败报告必须包含：
  - 向量 ID
  - 实际响应
  - 期望响应
  - 差异字段
- 推荐新增阶段：
  - `sdk-dotnet-tests`
  - `sdk-ts-tests`
  - `sdk-python-tests`
  - `sdk-conformance`

---

## 6. 完成定义（DoD）

- [x] 五类编号用例（`M5-DN-UT` / `M5-TS-UT` / `M5-PY-UT` / `M5-E2E` / `M5-CONF`）已建立并可自动执行。
- [x] Spec §10.1 最小 52 条向量全部落地且可回归。
- [x] `.NET`、`TS` 与 `Python` 对同向量结果语义一致（忽略键序与空白）。
- [ ] CI 已接入四类执行入口并作为门禁。
- [x] 文档与测试实现一致，且未修改 `docs/Spec.md`。

---

## 7. 假设与默认选择

- 默认同时推进 `.NET`、`JS/TS` 与 `Python` SDK，能力覆盖保持同构。
- 默认 TS SDK 目标为 Node.js，不承诺浏览器运行时 discovery。
- 默认协议固定为 `1`，不引入 v2 兼容分支逻辑。
- 默认签名向量是跨 SDK 一致性的唯一事实来源。
