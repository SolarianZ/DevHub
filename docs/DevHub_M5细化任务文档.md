# DevHub M5细化任务文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_黑盒测试Spec严格符合性审查报告.md](./DevHub_黑盒测试Spec严格符合性审查报告.md)

## 当前状态（截至 2026-03-27）

- 当前分支：`m5`。
- M1~M4 已完成并形成 v1.0.1 Hub 能力闭环（HTTP + WS + Invocation + Events）。
- `.NET SDK` 与 `JS/TS SDK` 主体能力已完成：`sdks/dotnet/` 与 `sdks/javascript/` 已实现 runtime discovery、HTTP/WS 客户端、统一错误模型，以及各自的 SDK 单元测试与 SDK↔Hub 黑盒集成测试；conformance 与跨语言 CI 门禁已接入仓库工作流。
- `Python SDK` 已在 `sdks/python/` 落地：当前已提供 runtime discovery、同步 HTTP JSON-RPC、异步 WebSocket events、统一错误模型、包根扩展抽象，以及 `sdks/python/tests/unit` / `sdks/python/tests/integration` 下的单元测试与 SDK↔Hub 集成测试；共享 conformance 与统一 CI 门禁已接入。
- 下文列出的 .NET SDK 路径为 `sdks/dotnet` 独立解决方案；`sdks/javascript` 是 JS/TS SDK 落点，`host/tests/conformance` 是后续 M5 目标落点。
- M5 实施基线：严格对齐 `docs/Spec.md`（v1.0.1），不修改 Spec 协议定义。
- 当前 Hub CI 已补充失败诊断日志、测试文本报告输出与诊断工件上传，便于后续 M5-CI 接入时快速定位门禁失败原因。
- 2026-03-18 已完成：仓库级文档采用 `DEVHUB_DATA_DIR` 数据根目录语义，并补充了多 Host 并行运行的文档约束。
- 2026-03-07 已修复 Windows `cross-platform-smoke` 中自定义发现路径用例的误报：问题来自测试夹具对 8.3 短路径与长路径的字面值比较，Hub 实际行为仍符合 `Spec`。
- 2026-03-09 已验证：`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` 可通过（`.NET SDK` 48 条单元测试 + 14 条集成测试）；已补齐 SDK 对 AppDefinition / AppInstance / Invocation 成功载荷的关键结构校验，并为注册载荷 `meta` 与 `respond.error.message` 增加本地参数校验。
- 2026-03-11 已验证：`sdks/javascript` 在 Node 24 下执行 `npm run build && npm test` 可通过（7 个测试文件 / 45 条测试），并通过 `python3 host/tests/test_runner.py --smoke --no-header` 冒烟回归。
- 2026-03-24 已完成：将原 `Python SDK` 独立设计规划并入本 M5 文档与 `DevHub_M5测试任务拆分文档.md`，统一 `.NET` / `JS/TS` / `Python` SDK 的设计与任务维护口径。
- 2026-03-25 已完成：补齐 Discovery + Auth + AppDef + AppInstance 共 20 条 conformance 向量；`vector_runner.py` 升级到 runner v1，支持向量级 `setup` 与自动 teardown，并保持 HTTP-first 的三语言一致性执行模型。
- 2026-03-25 已完成：补齐 Invocation Notify 6 + Request 10 共 16 条 conformance 向量，累计达到 36/52；`vector_runner.py` 升级到 runner v2，Invocation 向量改为真正走三语言 SDK `notify/request`，并由中立 raw-protocol helper 完成被调用方协作编排；已验证 `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`npm --prefix sdks/javascript run build`、`npm --prefix sdks/javascript test`、`python -m pytest sdks/python/tests`、`python host/tests/conformance/vector_runner.py` 与隔离 Hub 下的 `python host/tests/test_runner.py --smoke --no-header` 全部通过。
- 2026-03-26 已完成：补齐 WS Events 4 条 + Error 12 条 conformance 向量，累计达到 52/52；`vector_runner.py` 已支持 raw HTTP 字符串/数组请求、`raw.ws`、`sdk.events`、失败快照输出与稳定快照目录回溯；Host 已补齐 `rate_limited` 最小触发路径与仅测试环境启用的 `internal_error` 故障注入缝。已验证 `dotnet test host/src/DevHub.slnx -c Release`、`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`npm --prefix sdks/javascript run build`、`npm --prefix sdks/javascript test`、`python -m pytest sdks/python/tests`、`python host/tests/conformance/vector_runner.py`、隔离 Host 下的 `python host/tests/test_runner.py --smoke --no-header` 全部通过，并完成受控失败快照验收。
- 2026-03-27 已完成：将 `internal_error` conformance 故障注入改为按请求 ID 命中，避免引入非规范 RPC 方法；修复 Python conformance 适配器固定输出 UTF-8，并修复 `vector_runner.py` 在 Windows 默认控制台下打印失败报告时的编码稳定性。已验证 `dotnet test host/src/DevHub.Tests/DevHub.Tests.csproj -c Release --filter "FullyQualifiedName~RpcRouterFaultInjectionTests|FullyQualifiedName~InvocationHandlerBoundaryTests"`、`python host/tests/conformance/vector_runner.py` 与隔离 Host 下的 `python host/tests/test_runner.py --smoke --no-header` 可通过。
- 2026-03-27 已完成：在 `.github/workflows/ci.yml` 中新增 `sdk-dotnet-tests`、`sdk-ts-tests`、`sdk-python-tests`、`sdk-conformance` 四个阻断 job，并将触发范围扩展到 `host/**`、`sdks/**`、`.github/workflows/**` 与 `docs/Spec.md`；其中 conformance job 会上传失败日志与 `temp/conformance_snapshots/**` 工件。已验证 `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`npm --prefix sdks/javascript ci`、`npm --prefix sdks/javascript run build`、`npm --prefix sdks/javascript test`、`python -m pip install -e './sdks/python[test]' requests`、`dotnet build host/src/DevHub.Host/DevHub.Host.csproj -c Release`、`python -m pytest sdks/python/tests`、`dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`python host/tests/conformance/vector_runner.py` 与 `python host/tests/test_runner.py --smoke --no-header` 全部通过。
- 2026-03-27 已完成：补齐第三方无 SDK 接入资料，新增 `docs/无SDK接入指南.md`、`docs/schema/v1.0.1/`、`docs/protocol-examples/v1.0.1/` 与 `host/tests/conformance/README.md`，并补充 Hub v1.x 兼容口径说明，使第三方可仅基于 `Spec`、版本化 `Schema`、原始协议示例与 conformance 说明完成自研接入。已验证 `dotnet test host/src/DevHub.slnx -c Release`、`dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`、`npm --prefix sdks/javascript ci`、`npm --prefix sdks/javascript run build`、`npm --prefix sdks/javascript test`、`python -m pip install -e "./sdks/python[test]" requests`、`python -m pytest sdks/python/tests`、`python host/tests/conformance/vector_runner.py` 与 `python host/tests/test_runner.py --smoke --no-header` 全部通过。
- 2026-03-28 已完成：`host/tests/conformance/vector_runner.py` 新增外部 adapter manifest 接入契约，第三方现在可通过 `--adapter-manifest` 挂接自研 adapter，直接复用官方向量与 runner 完成自测；`host/tests/conformance/README.md`、`docs/无SDK接入指南.md` 与 `docs/开发指南.md` 已同步更新运行方式与输入输出契约，`.github/workflows/ci.yml` 也新增了 conformance runner 自测门禁。已验证 `python -m unittest discover -s host/tests -p "test_conformance_runner.py"`、`dotnet test host/src/DevHub.slnx -c Release`、`python host/tests/conformance/vector_runner.py --adapter-manifest temp/devhub-external-adapter.json --vector-id discovery.valid_runtime_layout_reads_token`、`python host/tests/conformance/vector_runner.py` 与 `python host/tests/test_runner.py --smoke --no-header` 可通过。

---

## 0. M5 目标与验收边界

### 0.1 M5 必须实现

- [x] 交付 `.NET SDK`（Node 外调用方可通过 .NET API 调用 DevHub）。
- [x] 交付 `JS/TS SDK`（Node.js 环境调用 DevHub）。
- [x] 补齐 `Python SDK` 设计/工程基线，并并入 M5 文档统一维护。
- [x] 建立 `签名测试向量` 基线（对齐 Spec §10.1/§10.2）。
- [x] 建立 `Hub↔SDK 契约测试`（同向量驱动 `.NET` / `TS` / `Python` 多实现并校验语义一致）。
- [x] 补齐面向第三方开发者的“无 SDK 接入资料”发布基线（`Spec`、版本化 `Schema` 包、原始协议示例、conformance 使用说明）。

### 0.2 M5 协议覆盖面（必须）

- [x] Discovery：`hub.json/tokenFile` 读取与校验。
- [x] HTTP 鉴权：Header、token、protocolVersion 校验链路。
- [x] WS 鉴权：`hub.ws.authenticate` 首条请求约束与错误映射。
- [x] RPC 全方法面：`hub.ping`、`hub.apps.*`、`hub.invoke.*`。
- [x] 事件能力：`hub.events.subscribe/unsubscribe` + `hub.event` 通知读取 + 断连清理语义。
- [x] 错误模型：JSON-RPC 标准错误 + DevHub 自定义错误完整映射。

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
- JS/TS SDK：`/Users/qiuyu/projects/DevHub/sdks/javascript/`
- JS/TS SDK 测试：`/Users/qiuyu/projects/DevHub/sdks/javascript/tests/`
- Python SDK：`/Users/qiuyu/projects/DevHub/sdks/python/src/devhub_sdk/`
- Python SDK 单元测试：`/Users/qiuyu/projects/DevHub/sdks/python/tests/unit/`
- Python SDK 集成测试：`/Users/qiuyu/projects/DevHub/sdks/python/tests/integration/`
- Python SDK 构建配置：`/Users/qiuyu/projects/DevHub/sdks/python/pyproject.toml`
- 签名向量基线：`/Users/qiuyu/projects/DevHub/host/tests/conformance/v1.0.1/`
- 契约运行器：`/Users/qiuyu/projects/DevHub/host/tests/conformance/vector_runner.py`

### 1.2 与现有工程集成要求

- .NET 侧：独立维护 `sdks/dotnet/DevHub.DotNetSdk.slnx`，不纳入 `host/src/DevHub.slnx`。
- JS/TS 侧：`sdks/javascript` 独立包管理，测试命令通过 `npm test` 接入 CI。
- Python 侧：`sdks/python` 通过 `pyproject.toml` + `setuptools` 管理，发布包名固定为 `devhub-sdk`、导入命名空间固定为 `devhub_sdk`，测试命令通过 `python3 -m pytest` 接入 CI。
- 契约侧：统一由 `vector_runner.py` 驱动 `.NET` / `TS` / `Python` SDK，输出统一报告格式。

---

## 2. 重要公共 API / 接口 / 类型（M5 设计基线）

### 2.1 .NET SDK（`DevHub.Sdk`）

- [x] `DevHubClientOptions`
  - `ClientId`
  - `ClientSessionId`
  - `DataDir`
  - `RequestTimeout`
  - `ProtocolVersion`
- [x] `DevHubClient`
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
- [x] `DevHubEventsClient`
  - `AuthenticateAsync`
  - `SubscribeAsync`
  - `UnsubscribeAsync`
  - `ReadEventsAsync`
- [x] `DevHubRpcException`
  - `Code`
  - `Message`
  - `Data`
  - `RequestId`

### 2.2 JS/TS SDK（`@devhub/sdk`）

- [x] `DevHubClientOptions`
- [x] `DevHubClient.fromRuntime()`
- [x] 与 .NET 同构的方法集（Promise 版）
- [x] `DevHubEventsClient`（基于 `AsyncIterator` 读取事件流）
- [x] `DevHubRpcError`（字段语义与 .NET 对齐）

### 2.3 Python SDK（`devhub-sdk` / `devhub_sdk`）

- [x] `DevHubClientOptions`
  - `client_id`
  - `client_session_id`
  - `data_dir`
  - `request_timeout`
  - `protocol_version`
- [x] `DevHubClient`
  - `from_runtime()`
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
- [x] `DevHubEventsClient`
  - `from_runtime()`
  - `authenticate()`
  - `subscribe()`
  - `unsubscribe()`
  - `read_events()`
  - `close()`
- [x] `DevHubRpcException` / `DevHubRpcErrorCode`
- [x] `RuntimeResolver`、`JsonRpcHttpTransport`、`JsonRpcWsSession` 扩展抽象
- [x] HTTP 客户端保持同步调用模型，事件客户端保持异步事件流模型；`InvokeRequest.args` 保留“省略字段”与“显式 `None` -> JSON `null`”的语义差异

### 2.4 跨 SDK 一致性约束

- 方法名、参数名、错误码、错误数据字段在 `.NET`、`TS` 与 `Python` 侧保持语义同构。
- `target.scope` / `target.instanceId` 默认值与约束严格对齐 `Spec.md`。
- 三个 SDK 都只接受数据根目录输入，并固定通过 `<dataDir>/runtime/hub.json` / `token.txt` 发现运行时信息，禁止硬编码 HTTP / WS 端点。
- 对 Spec §8 全量错误码保持一致异常映射（`-32700/-32600/-32601/-32602/-32603/-32001/-32002/-32010/-32011/-32012/-32014/-32020/-32030/-32040/-32050/-32099`）。

---

## 3. 里程碑任务拆分（按任务编号）

### 3.1 `M5-ARCH-*`（工程骨架与版本治理）

- [x] `M5-ARCH-001`：创建 .NET SDK 与测试工程目录结构。
- [x] `M5-ARCH-002`：创建 TS SDK 包结构与测试目录。
- [x] `M5-ARCH-003`：定义 SDK 版本策略（与 Hub v1.x 兼容口径）。
- [x] `M5-ARCH-004`：补充 SDK 最小可运行示例（README 片段）。

### 3.2 `M5-DN-*`（.NET SDK 实现）

- [x] `M5-DN-001`：Runtime discovery（`hub.json/tokenFile`）读取模块。
- [x] `M5-DN-002`：HTTP JSON-RPC 客户端与统一请求管线。
- [x] `M5-DN-003`：WS 客户端鉴权与订阅管线。
- [x] `M5-DN-004`：`hub.apps.*` API 封装（显式包含 `launch`）。
- [x] `M5-DN-005`：`hub.invoke.*` API 封装（含 request/notify/poll/respond）。
- [x] `M5-DN-006`：统一错误模型 `DevHubRpcException` 与错误数据透传。

### 3.3 `M5-TS-*`（JS/TS SDK 实现）

- [x] `M5-TS-001`：Node.js runtime discovery 实现。
- [x] `M5-TS-002`：HTTP JSON-RPC 客户端实现与 typed response 封装。
- [x] `M5-TS-003`：WS 鉴权、订阅、事件流读取实现。
- [x] `M5-TS-004`：`hub.apps.*` API 封装（显式包含 `launch`）。
- [x] `M5-TS-005`：`hub.invoke.*` API 封装。
- [x] `M5-TS-006`：统一错误模型 `DevHubRpcError` 与错误数据透传。

### 3.4 `M5-PY-*`（Python SDK 实现）

- [x] `M5-PY-001`：实现 runtime discovery、`DEVHUB_DATA_DIR` 覆盖与严格数据根目录校验。
- [x] `M5-PY-002`：实现同步 HTTP JSON-RPC 客户端、默认鉴权头与统一请求管线。
- [x] `M5-PY-003`：实现异步 WS 鉴权、订阅、取消订阅与事件流读取。
- [x] `M5-PY-004`：补齐 `hub.apps.*`、`hub.invoke.*` API 封装，并保留 `args` 省略/null 语义差异。
- [x] `M5-PY-005`：实现统一错误模型 `DevHubRpcException` / `DevHubRpcErrorCode` 与结构化辅助字段读取。
- [x] `M5-PY-006`：公开 `RuntimeResolver`、`JsonRpcHttpTransport`、`JsonRpcWsSession` 扩展抽象并接入 SDK 测试。

### 3.5 `M5-CONF-*`（签名测试向量）

- [x] `M5-CONF-001`：建立 `host/tests/conformance/v1.0.1/` 目录与向量元数据规范。
- [x] `M5-CONF-002`：按 Spec §10.1 生成最小 52 条向量（分类完整；当前已完成 52/52：Discovery/Auth/AppDef/AppInstance/Notify/Request/Events/Error）。
- [x] `M5-CONF-003`：每条向量固定字段：`id/description/transport/request/expectedResponse/tags`。
- [x] `M5-CONF-004`：建立语义比较规则（忽略 JSON 键序与空白）。
- [x] `M5-CONF-005`：显式覆盖 Events 断连清理与 Error 全量错误码/`error.data` 字段断言。

补充约定（runner v2）：

- 在不变更 `docs/Spec.md` 的前提下，仓库内 conformance runner 允许可选扩展字段 `setup`。
- `setup.definitions`：按顺序写入 suite Host 的 `apps/definitions`，支持 `{ fileName, definition }` 与 `{ fileName, rawText }`。
- `setup.instances`：按顺序通过 HTTP 预注册实例，支持 `state="registered"` 与 `state="offline"`；离线状态由 runner 等待 `waitSeconds` 触发。
- `setup.dataDir.files`：写入向量私有数据根目录，支持 `{ path, text }` 与 `{ path, json }`。
- runner 自动负责 teardown：删除向量创建的 definition 文件、注销预置实例并清理向量临时数据根目录。
- Invocation 类向量允许使用扩展字段 `orchestration`，固定阶段为 `beforeCaller` / `duringCaller` / `afterCaller`。
- `orchestration` 中的协作步骤由 runner 内部 raw-protocol helper 执行，当前支持 `register_instance`、`sleep`、`poll_expect_invocation`、`poll_expect_empty`、`respond_value`、`respond_error`。
- Invocation 类向量的 `request.kind` 允许使用 `sdk.notify` / `sdk.request`，要求三语言适配器真正走 SDK 公共 API；helper 只负责被调用方模拟，不再让 runner 同时把 SDK 作为“调用方”和“被调用方”。
- runner 对同一条 Invocation 向量按 SDK 维度使用独立执行沙箱，避免跨 SDK 共享临时 runtime / helper 状态造成串扰。

### 3.6 `M5-CT-*`（契约测试）

- [x] `M5-CT-001`：实现 `vector_runner.py`，统一调度 `.NET` / `TS` / `Python` SDK 执行同一向量。
- [x] `M5-CT-002`：输出统一报告（向量 ID、实际响应、期望响应、差异字段）。
- [x] `M5-CT-003`：建立失败快照机制，便于跨 SDK 回归定位。

### 3.7 `M5-CI-*`（CI 接入）

- [x] `M5-CI-001`：CI 增加 .NET SDK 单元测试入口。
- [x] `M5-CI-002`：CI 增加 TS SDK 单元测试入口（`npm test`）。
- [x] `M5-CI-003`：CI 增加 conformance 运行步骤（`python3 host/tests/conformance/vector_runner.py`）。
- [x] `M5-CI-004`：将 SDK 与契约测试结果纳入门禁判定。
- [x] `M5-CI-005`：CI 增加 Python SDK 测试入口（`python3 -m pytest sdks/python/tests`）。

### 3.8 `M5-DOC-*`（文档与状态同步）

- [x] `M5-DOC-001`：更新 `README.md` 的“当前范围”与 SDK 使用说明。
- [x] `M5-DOC-002`：补充 SDK 快速接入示例（`.NET` / `TS` / `Python`）。
- [x] `M5-DOC-003`：同步里程碑状态文档；明确 `Spec.md` 不做修改。
- [x] `M5-DOC-004`：补充“无 SDK 接入指南”，面向第三方开发者说明 Discovery、HTTP/WS 鉴权、方法调用、错误语义与自测入口。
- [x] `M5-DOC-005`：发布版本化 `Schema` 包（至少包含 `hub-runtime`、`app-definition`、`app-instance`、`invocation`、`rpc-request`、`rpc-response`、`error-response`）。
- [x] `M5-DOC-006`：补充原始 HTTP / WebSocket 协议示例与 conformance 使用说明，确保第三方可不依赖 SDK 完成接入与自测。

---

## 4. 交付物清单与完成定义（DoD）

### 4.1 交付物清单

- `.NET SDK` 工程与测试工程。
- `JS/TS SDK` 工程与测试目录。
- `Python SDK` 工程与测试目录。
- `host/tests/conformance/v1.0.1/*.json` 向量文件。
- `host/tests/conformance/vector_runner.py` 契约运行器。
- CI 配置更新（SDK + conformance）。
- README 与里程碑文档更新（含 Python SDK 设计/测试基线并入 M5 文档）。
- 面向第三方开发者的无 SDK 接入指南（含 Discovery、HTTP/WS、错误码、最小自测路径）。
- 版本化 `Schema` 包与对应发布说明。
- 原始协议示例与 conformance 使用说明。

### 4.2 M5 DoD

- [x] M5 必须能力（`.NET` + `TS` + `Python` + 向量 + 契约）全部落地。
- [x] 向量覆盖满足 Spec §10.1 最小计数（总计 52）。
- [x] `.NET`、`TS` 与 `Python` 对同一向量给出语义一致的结果。
- [x] CI 具备自动验证并阻断不一致变更。
- [x] 文档已同步、范围边界清晰、`Spec.md` 未改动。
- [x] 第三方开发者可仅基于 `Spec`、版本化 `Schema`、原始协议示例与 conformance 说明完成自研接入，无需依赖 SDK 源码。

---

## 5. 约束与风险控制

- 所有协议语义以 `docs/Spec.md` 为唯一权威来源，禁止 SDK 自定义协议解释。
- SDK 对“默认值与约束”必须与 Hub 当前行为一致，避免客户端侧漂移。
- 向量优先覆盖 MUST 条款；增强项可在最小集通过后迭代补充。

---

## 6. 假设与默认选择

- 默认同时推进 `.NET`、`JS/TS` 与 `Python`，公共 API 尽量同构。
- 默认 JS/TS 目标运行时为 Node.js，不承诺浏览器侧 runtime discovery。
- 默认协议版本固定为 `1`，不引入 v2 字段或行为。
- 默认以签名测试向量作为跨 SDK 一致性的唯一事实来源。
