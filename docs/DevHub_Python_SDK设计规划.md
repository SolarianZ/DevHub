# DevHub Python SDK设计规划

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M5细化任务文档.md](./DevHub_M5细化任务文档.md)
> - [sdks/python/README.md](../sdks/python/README.md)
>
> 说明：
> - 本文档用于补齐 Python SDK 的设计、工程落点与 M5 文档口径。
> - 所有公开协议语义仍以 [Spec.md](./Spec.md) 为唯一权威来源；本文不得覆盖、替代或修改 Spec。

## 当前状态（截至 2026-03-14）

- 当前仓库已存在 `sdks/python/` 工作区，发布包名为 `devhub-sdk`，导入名为 `devhub_sdk`。
- 已落地同步 HTTP 客户端 `DevHubClient`、异步事件客户端 `DevHubEventsClient`、统一错误模型 `DevHubRpcException` / `DevHubRpcErrorCode`、公开模型与运行时发现抽象。
- 已提供可注入扩展点：`RuntimeResolver`、`JsonRpcHttpTransport`、`JsonRpcWsSession`，用于 fake transport、录制回放与自定义连接策略。
- 当前 `sdks/python/tests/` 下已包含 123 条单元测试用例与 10 条 SDK↔Hub 集成测试用例。
- 共享 conformance 向量、跨语言一致性 runner 与发布流水线尚未统一接入。

---

## 0. 目标与范围

### 0.1 目标

- 在 CPython 3.11+ 环境下提供符合 DevHub Hub v1.x 的 Python SDK。
- 让 CLI、自动化脚本、测试夹具与轻量服务可通过 Python API 调用 Hub，而不需要手工拼装 JSON-RPC。
- 与 `.NET`、`JS/TS` SDK 在公开方法语义、默认值、错误映射和可观测行为上保持同构。
- 优先保证严格对齐 `Spec.md`、最小依赖、可测试性与易调试性。

### 0.2 M5 范围

- 运行时发现：固定读取 `<data_dir>/runtime` 下的 `hub.json` / `token.txt`，支持 `DEVHUB_DATA_DIR` 覆盖。
- HTTP JSON-RPC：`hub.ping`、`hub.apps.*`、`hub.invoke.*`。
- WebSocket events：`hub.ws.authenticate`、`hub.events.subscribe`、`hub.events.unsubscribe`、`hub.event`。
- 统一错误模型与结构化辅助字段。
- 单元测试、SDK↔Hub 黑盒集成测试与文档化设计基线。

### 0.3 非目标

- 修改 `Spec.md` 协议字段、错误码或状态机。
- 浏览器运行时支持。
- 自动重连后的自动重订阅。
- 独立于共享 conformance 基线之外的 Python 私有协议解释。

---

## 1. 工程落点与打包形态

### 1.1 仓库目录

- Python SDK 源码：`sdks/python/src/devhub_sdk/`
- 单元测试：`sdks/python/tests/unit/`
- 集成测试：`sdks/python/tests/integration/`
- 构建配置：`sdks/python/pyproject.toml`

### 1.2 打包与依赖

- 包管理统一使用 `pyproject.toml` + `setuptools`。
- 发布名固定为 `devhub-sdk`，导入根命名空间固定为 `devhub_sdk`。
- 运行时最小外部依赖当前仅保留 `websockets`，HTTP 传输默认基于标准库 `urllib`。
- SDK 通过 `py.typed` 暴露类型标记，确保类型检查器可识别已发布类型信息。

### 1.3 设计取向

- HTTP 客户端保持同步调用风格，优先覆盖 CLI、脚本和测试夹具的主场景。
- 事件客户端采用异步接口，贴合 Python WebSocket 生态与持续读取事件流的自然用法。
- 公开扩展点放在包根导出，允许调用方替换运行时发现、HTTP 传输与 WS 会话实现，而不破坏主 API。

---

## 2. 公开 API 设计基线

### 2.1 客户端选项

- `DevHubClientOptions`
  - `client_id`
  - `client_session_id`
  - `data_dir`
  - `request_timeout`
  - `protocol_version`

约束：

- `client_id` 必须为非空字符串。
- `client_session_id` 必须符合 UUID 格式。
- `protocol_version` 当前固定为 `1`。

### 2.2 HTTP 客户端

- `DevHubClient.from_runtime()`
- `ping()`
- `list_definitions()`
- `get_definition()`
- `register_instance()`
- `heartbeat()`
- `unregister_instance()`
- `list_instances()`
- `launch()`
- `notify()`
- `request()`
- `poll()`
- `respond()`

设计要求：

- 方法命名尽量遵循 Python 习惯，但必须与 DevHub RPC 公开能力一一对应。
- 请求构造阶段完成本地参数校验，避免把明显非法的请求推迟到 Hub 端暴露。
- `InvokeRequest` 必须保留“省略 `args`”与“显式传入 `None`（序列化为 `null`）”的语义差异。

### 2.3 WebSocket 事件客户端

- `DevHubEventsClient.from_runtime()`
- `authenticate()`
- `subscribe()`
- `unsubscribe()`
- `read_events()`
- `close()`

设计要求：

- 调用方必须先执行 `authenticate()`，之后才能订阅或读取事件。
- `read_events()` 以异步迭代器暴露事件流。
- 连接终止后必须结束事件流并向后续请求暴露一致错误，而不是静默吞掉连接异常。

### 2.4 数据模型与错误模型

- 公开模型使用 `dataclass(slots=True)` 组织，覆盖 `AppDefinition`、`AppInstance`、`Invocation`、`LaunchResult`、`DevHubEvent` 等核心协议对象。
- 协议错误统一映射为 `DevHubRpcException`，并通过 `DevHubRpcErrorCode` 暴露已知错误码枚举。
- 异常对象需要提供 `known_code`、`reason`、`invocation_id`、`callee_error`、`is_code(...)` 等读取辅助，降低调用方解析 `error.data` 的样板代码。

---

## 3. 分层架构设计

### 3.1 运行时发现层

- `RuntimeResolver`：运行时发现抽象。
- `FileSystemRuntimeResolver`：默认文件系统实现。
- `discover_runtime()` / `resolve_data_directory()`：便于脚本和测试直接调用的辅助入口。

设计要求：

- 文档与示例默认遵循数据根目录布局：`<DEVHUB_DATA_DIR>/runtime/hub.json`。
- SDK 只接受数据根目录输入；误传 `runtime/` 子目录或旧版直接 runtime 目录输入时，应给出明确迁移错误。
- SDK 必须始终以 `hub.json` 为真实端点来源，禁止硬编码端口、HTTP 地址或 WS 地址。

### 3.2 负载构造与解析层

- `_payloads.py` 负责按 Spec 构造请求参数。
- `_parsing.py` 负责将返回载荷解析为公开模型。
- `_validation.py` / `_json.py` 负责 JSON 合法性、值约束与文本解析。

设计要求：

- 对 `echo`、`meta`、`args`、`value`、`error.data` 等 JSON 值执行严格校验，拒绝 `NaN`、回调、循环引用或其他不受支持类型。
- 对成功响应与错误响应都执行结构校验，避免静默接受缺字段、错字段或不符合 Spec 的载荷。
- 对可选但非可空字段保持严格解析，不因服务端返回 `null` 而自动吞并或兜底。

### 3.3 传输与会话层

- `JsonRpcHttpTransport`：HTTP 传输抽象。
- `UrllibJsonRpcHttpTransport`：默认同步 HTTP JSON-RPC 实现。
- `JsonRpcWsSession`：WS 会话抽象。
- `WebSocketJsonRpcSession`：默认 WebSocket JSON-RPC 会话实现。

设计要求：

- HTTP 请求必须自动补齐 `Authorization`、`X-DevHub-Protocol`、`X-DevHub-ClientId`、`X-DevHub-ClientSessionId`。
- WS 会话必须保证请求 ID 与响应 ID 严格配对，并在连接终止时显式失败所有挂起请求。
- 事件通知仅接受 `hub.event`；未知通知应按实现约定忽略或拒绝，但不得污染已确认的订阅状态。

---

## 4. 与 Spec 的对齐策略

### 4.1 协议权威来源

- `docs/Spec.md` 是唯一权威来源。
- Python SDK 设计文档只描述工程落点、实现边界与测试规划，不重新定义协议。

### 4.2 一致性要求

- 运行时发现、HTTP/WS 鉴权、错误码、字段命名、作用域语义、状态转换必须严格服从 `Spec.md`。
- Python SDK 与 `.NET`、`JS/TS` SDK 的差异只允许存在于语言习惯与调用风格层面，不允许存在于协议解释层面。
- 任何新增辅助 API 都不得改变公开协议默认值与错误语义。

### 4.3 兼容策略

- 目标行为仅接受数据根目录输入，并统一从 `<data_dir>/runtime/hub.json` 发现 Hub；检测到旧环境变量或旧布局时应直接报迁移错误。
- 当前实现优先保证严格解析公开响应；一旦 Host 返回不符合 Spec 的结构，SDK 应尽早失败并给出清晰异常，而不是静默兼容。
- 后续若需要对接共享 conformance 资产，Python 侧必须使用与 `.NET`、`JS/TS` 一致的向量与比较规则。

---

## 5. 测试与验证规划

### 5.1 单元测试

当前单元测试重点覆盖：

- runtime discovery 成功/失败路径、`DEVHUB_DATA_DIR` 环境变量覆盖、误传 `runtime/` 子目录错误与旧环境变量迁移错误。
- payload 构造默认值、边界值、非法参数与 JSON 校验。
- HTTP 客户端 header 组装、错误映射与响应结构校验。
- WS 事件客户端鉴权、订阅、事件流生命周期与连接终止语义。
- 异常辅助属性、包根导出与扩展抽象注入。

### 5.2 集成测试

当前集成测试重点覆盖：

- `ping`、definition、instance 管理与 `launch` 闭环。
- `notify` / `request` / `poll` / `respond` 主链路与关键错误路径。
- `events` 鉴权、订阅、取消订阅与事件投递。

### 5.3 最小验证命令

- `python3 -m compileall sdks/python/src sdks/python/tests`
- `python3 -m pytest sdks/python/tests/unit`
- `python3 -m pytest sdks/python/tests/integration`
- `python3 tests/test_runner.py --smoke --no-header`

说明：

- Python SDK 自身验证以 `sdks/python/tests/` 为主。
- 仓库级 `smoke` 仍用于确认 Hub 公开行为未因 SDK 配套改动发生回归。

### 5.4 后续补齐项

- 将 Python SDK 接入 `tests/conformance/vector_runner.py` 或同类共享运行器。
- 在跨语言一致性门禁中纳入 Python 与 `.NET` / `JS/TS` 的同向量语义比较。

---

## 6. M5 任务拆分（Python 子范围）

- [x] `M5-PY-001`：建立 `sdks/python/` 包结构与 `pyproject.toml`。
- [x] `M5-PY-002`：实现 runtime discovery、`hub.json` / `token.txt` 读取与严格校验。
- [x] `M5-PY-003`：实现同步 HTTP JSON-RPC 客户端与请求头组装。
- [x] `M5-PY-004`：实现异步 WS 鉴权、订阅与事件流读取。
- [x] `M5-PY-005`：实现统一错误模型与结构化辅助字段读取。
- [x] `M5-PY-006`：公开运行时发现、HTTP 传输与 WS 会话扩展抽象。
- [x] `M5-PY-007`：补齐 SDK 单元测试与 SDK↔Hub 集成测试。
- [ ] `M5-PY-008`：接入共享 conformance 向量与跨语言一致性比较。
- [ ] `M5-PY-009`：明确 wheel/sdist 发布、版本策略与发布流水线。
- [x] `M5-PY-DOC-001`：补充 `docs/` 下的 Python SDK 设计规划文档。

---

## 7. 交付物与完成定义

### 7.1 交付物

- `sdks/python/src/devhub_sdk/` Python SDK 源码。
- `sdks/python/tests/unit/` 与 `sdks/python/tests/integration/` 测试资产。
- `sdks/python/README.md` 使用说明。
- `docs/DevHub_Python_SDK设计规划.md` 设计规划文档。

### 7.2 Python SDK 子范围 DoD

- 公开 API、错误语义、字段命名与 `Spec.md` 保持一致。
- Python SDK 单测与 SDK↔Hub 集成测试可稳定执行。
- 文档已覆盖目标、范围、分层架构、测试策略与未完成项。
- 不通过修改 `Spec.md` 迁就 Python 实现。

说明：

- 共享 conformance runner 与跨语言门禁属于 M5 公共资产，仍由仓库级 M5-CONF / M5-CT 任务统一推进，不在本子文档中单独拆出私有协议口径。

---

## 8. 默认选择

- 默认目标解释器为 CPython 3.11+。
- 默认 HTTP 客户端为同步模型，默认 WS 事件客户端为异步模型。
- 默认文档示例使用数据根目录布局，而不是旧式直接 runtime 目录输入。
- 默认与其他 SDK 共享同一套协议事实来源、错误码定义与后续 conformance 基线。
