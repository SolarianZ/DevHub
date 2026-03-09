# DevHub M5细化任务文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_黑盒测试Spec严格符合性审查报告.md](./DevHub_黑盒测试Spec严格符合性审查报告.md)

## 当前状态（截至 2026-03-09）

- 当前分支：`m5`。
- M1~M4 已完成并形成 v1.0.1 Hub 能力闭环（HTTP + WS + Invocation + Events）。
- `.NET SDK` 子范围已完成：`sdks/dotnet/` 已实现 runtime discovery、HTTP/WS 客户端、统一错误模型，以及 `.NET` 单元测试与 SDK↔Hub 黑盒集成测试；TS SDK、conformance 与跨语言 CI 仍待后续阶段完成。
- 下文列出的 .NET SDK 路径已调整为 `sdks/dotnet` 独立解决方案；`sdk/devhub-sdk-ts` 与 `tests/conformance` 仍为后续 M5 目标落点。
- M5 实施基线：严格对齐 `docs/Spec.md`（v1.0.1），不修改 Spec 协议定义。
- 当前 Hub CI 已补充失败诊断日志、测试文本报告输出与诊断工件上传，便于后续 M5-CI 接入时快速定位门禁失败原因。
- 2026-03-07 已修复 Windows `cross-platform-smoke` 中 `DEVHUB_RUNTIME_DIR` 用例的误报：问题来自测试夹具对 8.3 短路径与长路径的字面值比较，Hub 实际行为仍符合 `Spec`。
- 2026-03-09 已验证：`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 可通过（`.NET SDK` 34 条单元测试 + 14 条集成测试）。

---

## 0. M5 目标与验收边界

### 0.1 M5 必须实现

- [ ] 交付 `.NET SDK`（Node 外调用方可通过 .NET API 调用 DevHub）。
- [ ] 交付 `JS/TS SDK`（Node.js 环境调用 DevHub）。
- [ ] 建立 `签名测试向量` 基线（对齐 Spec §10.1/§10.2）。
- [ ] 建立 `Hub↔SDK 契约测试`（同向量驱动 .NET/TS 双实现并校验语义一致）。

### 0.2 M5 协议覆盖面（必须）

- [ ] Discovery：`hub.json/tokenFile` 读取与校验。
- [ ] HTTP 鉴权：Header、token、protocolVersion 校验链路。
- [ ] WS 鉴权：`hub.ws.authenticate` 首条请求约束与错误映射。
- [ ] RPC 全方法面：`hub.ping`、`hub.apps.*`、`hub.invoke.*`。
- [ ] 事件能力：`hub.events.subscribe/unsubscribe` + `hub.event` 通知读取 + 断连清理语义。
- [ ] 错误模型：JSON-RPC 标准错误 + DevHub 自定义错误完整映射。

### 0.3 非 M5 范围

- 浏览器 SDK（仅 Node.js 运行时为 M5 目标）。
- 协议版本升级（继续固定 `protocolVersion=1`）。
- 事件重放与持久化订阅。
- 自动重连后的自动订阅恢复。
- 修改 `Spec.md` 协议字段、错误码语义、状态机定义。

---

## 1. 架构落点与仓库目录规划

### 1.1 新增目录与职责

- .NET SDK：`/Users/qiuyu/projects/DevHub/sdks/dotnet/src/DevHub.Sdk/`
- .NET SDK 单元测试：`/Users/qiuyu/projects/DevHub/sdks/dotnet/tests/DevHub.Sdk.UnitTests/`
- .NET SDK 集成测试：`/Users/qiuyu/projects/DevHub/sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/`
- JS/TS SDK：`/Users/qiuyu/projects/DevHub/sdk/devhub-sdk-ts/`
- JS/TS SDK 测试：`/Users/qiuyu/projects/DevHub/sdk/devhub-sdk-ts/tests/`
- 签名向量基线：`/Users/qiuyu/projects/DevHub/tests/conformance/v1.0.1/`
- 契约运行器：`/Users/qiuyu/projects/DevHub/tests/conformance/vector_runner.py`

### 1.2 与现有工程集成要求

- .NET 侧：独立维护 `sdks/dotnet/DevHub.DotNetSdk.slnx`，不纳入 `src/DevHub.slnx`。
- JS/TS 侧：`sdk/devhub-sdk-ts` 独立包管理，测试命令通过 `npm test` 接入 CI。
- 契约侧：统一由 `vector_runner.py` 驱动 .NET/TS SDK，输出统一报告格式。

---

## 2. 重要公共 API / 接口 / 类型（M5 设计基线）

### 2.1 .NET SDK（`DevHub.Sdk`）

- [ ] `DevHubClientOptions`
  - `ClientId`
  - `ClientSessionId`
  - `RuntimeDir`
  - `RequestTimeout`
  - `ProtocolVersion`
- [ ] `DevHubClient`
  - `FromRuntimeAsync`
  - `PingAsync`
  - `ListDefinitionsAsync`
  - `GetDefinitionAsync`
  - `RegisterInstanceAsync`
  - `HeartbeatAsync`
  - `UnregisterInstanceAsync`
  - `ListInstancesAsync`
  - `LaunchAsync`
  - `NotifyAsync`
  - `RequestAsync`
  - `PollAsync`
  - `RespondAsync`
- [ ] `DevHubEventsClient`
  - `AuthenticateAsync`
  - `SubscribeAsync`
  - `UnsubscribeAsync`
  - `ReadEventsAsync`
- [ ] `DevHubRpcException`
  - `Code`
  - `Message`
  - `Data`
  - `RequestId`

### 2.2 JS/TS SDK（`@devhub/sdk`）

- [ ] `DevHubClientOptions`
- [ ] `DevHubClient.fromRuntime()`
- [ ] 与 .NET 同构的方法集（Promise 版）
- [ ] `DevHubEventsClient`（基于 `AsyncIterator` 读取事件流）
- [ ] `DevHubRpcError`（字段语义与 .NET 对齐）

### 2.3 跨 SDK 一致性约束

- 方法名、参数名、错误码、错误数据字段在 .NET 与 TS 侧保持语义同构。
- `target.scope` / `target.instanceId` 默认值与约束严格对齐 `Spec.md`。
- 对 Spec §8 全量错误码保持一致异常映射（`-32700/-32600/-32601/-32602/-32603/-32001/-32002/-32010/-32011/-32012/-32014/-32020/-32030/-32040/-32050/-32099`）。

---

## 3. 里程碑任务拆分（按任务编号）

### 3.1 `M5-ARCH-*`（工程骨架与版本治理）

- [ ] `M5-ARCH-001`：创建 .NET SDK 与测试工程目录结构。
- [ ] `M5-ARCH-002`：创建 TS SDK 包结构与测试目录。
- [ ] `M5-ARCH-003`：定义 SDK 版本策略（与 Hub v1.x 兼容口径）。
- [ ] `M5-ARCH-004`：补充 SDK 最小可运行示例（README 片段）。

### 3.2 `M5-DN-*`（.NET SDK 实现）

- [x] `M5-DN-001`：Runtime discovery（`hub.json/tokenFile`）读取模块。
- [x] `M5-DN-002`：HTTP JSON-RPC 客户端与统一请求管线。
- [x] `M5-DN-003`：WS 客户端鉴权与订阅管线。
- [x] `M5-DN-004`：`hub.apps.*` API 封装（显式包含 `launch`）。
- [x] `M5-DN-005`：`hub.invoke.*` API 封装（含 request/notify/poll/respond）。
- [x] `M5-DN-006`：统一错误模型 `DevHubRpcException` 与错误数据透传。

### 3.3 `M5-TS-*`（JS/TS SDK 实现）

- [ ] `M5-TS-001`：Node.js runtime discovery 实现。
- [ ] `M5-TS-002`：HTTP JSON-RPC 客户端实现与 typed response 封装。
- [ ] `M5-TS-003`：WS 鉴权、订阅、事件流读取实现。
- [ ] `M5-TS-004`：`hub.apps.*` API 封装（显式包含 `launch`）。
- [ ] `M5-TS-005`：`hub.invoke.*` API 封装。
- [ ] `M5-TS-006`：统一错误模型 `DevHubRpcError` 与错误数据透传。

### 3.4 `M5-CONF-*`（签名测试向量）

- [ ] `M5-CONF-001`：建立 `tests/conformance/v1.0.1/` 目录与向量元数据规范。
- [ ] `M5-CONF-002`：按 Spec §10.1 生成最小 52 条向量（分类完整）。
- [ ] `M5-CONF-003`：每条向量固定字段：`id/description/transport/request/expectedResponse/tags`。
- [ ] `M5-CONF-004`：建立语义比较规则（忽略 JSON 键序与空白）。
- [ ] `M5-CONF-005`：显式覆盖 Events 断连清理与 Error 全量错误码/`error.data` 字段断言。

### 3.5 `M5-CT-*`（契约测试）

- [ ] `M5-CT-001`：实现 `vector_runner.py`，统一调度 .NET/TS SDK 执行同一向量。
- [ ] `M5-CT-002`：输出统一报告（向量 ID、实际响应、期望响应、差异字段）。
- [ ] `M5-CT-003`：建立失败快照机制，便于跨 SDK 回归定位。

### 3.6 `M5-CI-*`（CI 接入）

- [ ] `M5-CI-001`：CI 增加 .NET SDK 单元测试入口。
- [ ] `M5-CI-002`：CI 增加 TS SDK 单元测试入口（`npm test`）。
- [ ] `M5-CI-003`：CI 增加 conformance 运行步骤（`python3 tests/conformance/vector_runner.py`）。
- [ ] `M5-CI-004`：将 SDK 与契约测试结果纳入门禁判定。

### 3.7 `M5-DOC-*`（文档与状态同步）

- [x] `M5-DOC-001`：更新 `README.md` 的“当前范围”与 SDK 使用说明。
- [ ] `M5-DOC-002`：补充 SDK 快速接入示例（.NET/TS）。
- [x] `M5-DOC-003`：同步里程碑状态文档；明确 `Spec.md` 不做修改。

---

## 4. 交付物清单与完成定义（DoD）

### 4.1 交付物清单

- `.NET SDK` 工程与测试工程。
- `JS/TS SDK` 工程与测试目录。
- `tests/conformance/v1.0.1/*.json` 向量文件。
- `tests/conformance/vector_runner.py` 契约运行器。
- CI 配置更新（SDK + conformance）。
- README 与里程碑文档更新。

### 4.2 M5 DoD

- [ ] M5 必须能力（.NET + TS + 向量 + 契约）全部落地。
- [ ] 向量覆盖满足 Spec §10.1 最小计数（总计 52）。
- [ ] 双 SDK 对同一向量给出语义一致的结果。
- [ ] CI 具备自动验证并阻断不一致变更。
- [ ] 文档已同步、范围边界清晰、`Spec.md` 未改动。

---

## 5. 约束与风险控制

- 所有协议语义以 `docs/Spec.md` 为唯一权威来源，禁止 SDK 自定义协议解释。
- SDK 对“默认值与约束”必须与 Hub 当前行为一致，避免客户端侧漂移。
- 向量优先覆盖 MUST 条款；增强项可在最小集通过后迭代补充。

---

## 6. 假设与默认选择

- 默认同时推进 .NET 与 JS/TS，公共 API 尽量同构。
- 默认 JS/TS 目标运行时为 Node.js，不承诺浏览器侧 runtime discovery。
- 默认协议版本固定为 `1`，不引入 v2 字段或行为。
- 默认以签名测试向量作为跨 SDK 一致性的唯一事实来源。
