# DevHub 原始协议接入指南

本文面向不准备直接使用仓库内 `.NET` / `JS/TS` / `Python` SDK 的第三方开发者，说明如何仅基于公开协议资料完成 DevHub Hub v1.x 的原始接入与自测。

如果你只是需要先启动 Host 或对照官方 SDK 的最小上手路径，可先阅读：

- [`../host/quickstart.md`](../host/quickstart.md)
- [`../sdk/README.md`](../sdk/README.md)

## 1. 适用范围

- 权威协议来源始终是 [`Specification.md`](../../specification/protocol/Specification.md)。
- 本文只整理“不依赖 SDK 源代码”的最小接入路径，不扩展或重写任何协议语义。
- 当前兼容基线为 `protocolVersion=1`，适用 Hub v1.x。

如果你已经有自己的 HTTP、WebSocket 与 JSON 处理栈，只需组合下列公开资料即可完成接入：

- [`Specification.md`](../../specification/protocol/Specification.md)
- [`docs/specification/schema/v1.0.1/README.md`](../../specification/schema/v1.0.1/README.md)
- [`docs/specification/protocol-examples/v1.0.1/README.md`](../../specification/protocol-examples/v1.0.1/README.md)
- [`host/tests/conformance/README.md`](../../../host/tests/conformance/README.md)

## 2. 运行时发现

客户端必须先定位数据根目录，再从 `<dataDir>/runtime/hub.json` 读取运行时信息。

默认数据根目录：

- Windows：`%LOCALAPPDATA%/DevHub/`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`$XDG_DATA_HOME/DevHub/`，若未设置则为 `~/.local/share/DevHub/`

也可以通过环境变量 `DEVHUB_DATA_DIR` 显式覆盖整个数据根目录。

标准目录结构如下：

```text
<dataDir>/
├── runtime/
│   ├── hub.json
│   └── token.txt
├── apps/
│   ├── definitions/
│   └── instances/
└── logs/
```

其中 `apps/definitions/` 的公开持久化契约是“一份 Definition 对应一个 `{appId}--{scopeKey}.json` 文件，且 payload 显式包含 `scope`”。Global Definition 使用 `scope: ""` 与 `scopeKey = global`；显式作用域 Definition 使用首尾均不含空白字符的非空 scope 字符串和对应的稳定文件名安全编码。旧式 `{appId}.json`、缺失 `scope`、`scope: null` 或首尾包含空白字符的 `scope` 都不属于合法 Definition 资产。

`hub.json` 至少需要读取这些字段：

- `protocolVersion`
- `httpBaseUrl`
- `wsUrl`
- `tokenFile`
- `runtimeTuning`

接入侧必须把 `hub.json` 当作地址与端口的唯一权威来源，禁止硬编码 `http://127.0.0.1:<port>` 或 WebSocket URL。

## 3. HTTP 鉴权与最小调用

HTTP JSON-RPC 端点固定为 `POST {httpBaseUrl}/rpc`，请求体使用 JSON-RPC 2.0 对象。浏览器 / WebView 直连同样使用该端点；首次跨源请求前，运行时通常会先向 `OPTIONS {httpBaseUrl}/rpc` 发送预检。

每个 `POST /rpc` 请求都必须携带：

- `Authorization: Bearer <token>`
- `X-DevHub-Protocol: 1`
- `X-DevHub-ClientId: <stable-client-id>`
- `X-DevHub-ClientSessionId: <uuid>`
- `Content-Type: application/json`

`OPTIONS /rpc` 预检不要求携带上述协议头，也不携带 JSON-RPC body。

浏览器 / WebView 直连前提：

- 宿主应用必须先从 `<dataDir>/runtime/hub.json` 和 `tokenFile` 读取运行时连接信息，再把 `httpBaseUrl`、`wsUrl` 和 Bearer Token 交给前端；禁止硬编码端口、固定 URL 或绕过 token。
- 前端必须能够直接访问 Host 暴露的回环地址 `httpBaseUrl`；官方支持路径是“前端直连 Host”，而不是要求原生层代理 `/rpc`。
- 浏览器 / WebView 首次向 `/rpc` 发起带 `Origin` 的调用时，Host 会先处理 `OPTIONS /rpc` 预检，回显当前请求 `Origin`，并声明 `POST`、`OPTIONS` 以及 `Authorization`、`Content-Type`、`X-DevHub-Protocol`、`X-DevHub-ClientId`、`X-DevHub-ClientSessionId` 可用于后续正式请求。
- 实际 `POST /rpc` 仍必须携带本节列出的全部协议头与 Bearer Token；无论 RPC 结果成功还是返回 JSON-RPC `error`，带 `Origin` 的响应都可以读取原始响应体。

最小健康检查可以直接调用 `hub.ping`。原始 JSON 示例见：

- [`ping.request.json`](../../specification/protocol-examples/v1.0.1/http/ping.request.json)
- [`ping.success.json`](../../specification/protocol-examples/v1.0.1/http/ping.success.json)

如果要接入定义管理、实例注册与调用链路，可继续参考：

- [`list-definitions.null-scope.request.json`](../../specification/protocol-examples/v1.0.1/http/list-definitions.null-scope.request.json)
- [`list-definitions.omitted-scope.request.json`](../../specification/protocol-examples/v1.0.1/http/list-definitions.omitted-scope.request.json)
- [`list-definitions.global.request.json`](../../specification/protocol-examples/v1.0.1/http/list-definitions.global.request.json)
- [`list-definitions.success.json`](../../specification/protocol-examples/v1.0.1/http/list-definitions.success.json)
- [`get-definition.request.json`](../../specification/protocol-examples/v1.0.1/http/get-definition.request.json)
- [`get-definition.success.json`](../../specification/protocol-examples/v1.0.1/http/get-definition.success.json)
- [`validate-definition.valid.request.json`](../../specification/protocol-examples/v1.0.1/http/validate-definition.valid.request.json)
- [`validate-definition.valid.success.json`](../../specification/protocol-examples/v1.0.1/http/validate-definition.valid.success.json)
- [`validate-definition.invalid.request.json`](../../specification/protocol-examples/v1.0.1/http/validate-definition.invalid.request.json)
- [`validate-definition.invalid.success.json`](../../specification/protocol-examples/v1.0.1/http/validate-definition.invalid.success.json)
- [`upsert-definition.request.json`](../../specification/protocol-examples/v1.0.1/http/upsert-definition.request.json)
- [`upsert-definition.success.json`](../../specification/protocol-examples/v1.0.1/http/upsert-definition.success.json)
- [`upsert-definition.definition-invalid.error.json`](../../specification/protocol-examples/v1.0.1/http/upsert-definition.definition-invalid.error.json)
- [`delete-definition.request.json`](../../specification/protocol-examples/v1.0.1/http/delete-definition.request.json)
- [`delete-definition.success.json`](../../specification/protocol-examples/v1.0.1/http/delete-definition.success.json)
- [`register-instance.request.json`](../../specification/protocol-examples/v1.0.1/http/register-instance.request.json)
- [`register-instance.success.json`](../../specification/protocol-examples/v1.0.1/http/register-instance.success.json)
- [`unregister-instance.request.json`](../../specification/protocol-examples/v1.0.1/http/unregister-instance.request.json)
- [`unregister-instance.success.json`](../../specification/protocol-examples/v1.0.1/http/unregister-instance.success.json)
- [`invoke-request.request.json`](../../specification/protocol-examples/v1.0.1/http/invoke-request.request.json)
- [`invoke-request.success.json`](../../specification/protocol-examples/v1.0.1/http/invoke-request.success.json)

其中：

- `AppDefinition` 的公开身份是 `appId + scope`；持久化或提交 Definition 时必须显式携带 `scope`，其中 Global Definition 使用 `scope: ""`。
- `hub.apps.validateDefinition` 用于提交前预校验，不修改任何持久化状态。
- `hub.apps.listDefinitions` 支持可选 `appId` 与 `scope` 过滤；`scope` 省略或为 `null` 时不按作用域过滤，`scope: ""` 时仅返回 Global Definition，其他合法字符串按精确作用域过滤。
- `hub.apps.upsertDefinition` / `hub.apps.deleteDefinition` 仅支持 HTTP；`hub.apps.getDefinition` 仍支持 HTTP 与 WebSocket。
- `hub.apps.getDefinition` / `hub.apps.deleteDefinition` 都必须按精确 `appId + scope` 传参，并显式提供合法字符串 `scope`，不再支持仅按 `appId` 或 `scope: null` 定位 Definition。
- `hub.apps.registerInstance` / `hub.apps.unregisterInstance` 的 `password` 是顶层参数，不属于 `AppInstanceRegistration` 或 `AppInstance`，也不会出现在成功响应或事件载荷中；其中注册类 `scope` 必须显式给出合法字符串，`scope: ""` 表示 Global。
- 对 `hub.apps.listInstances.scope`、`hub.apps.launch.scope` 与 `hub.invoke.*.target.scope`，`scope` 省略或为 `null` 表示“不限制作用域”，`scope: ""` 表示仅限 Global。
- 浏览器 / WebView 预检成功仅代表 `/rpc` 可建立 HTTP 会话；WebSocket 连接与 `hub.ws.authenticate` 仍按协议规范单独处理。

## 4. WebSocket 鉴权与事件订阅

WebSocket 连接地址必须直接使用 `hub.json.wsUrl`。

连接建立后：

1. 第一条消息必须是带 `id` 的 `hub.ws.authenticate` 请求。
2. 只有鉴权成功后，才能发送 `hub.events.subscribe` / `hub.events.unsubscribe`。
3. Hub 下发事件时，服务端会通过 `hub.event` JSON-RPC 通知推送。

原始 JSON 示例见：

- [`authenticate.request.json`](../../specification/protocol-examples/v1.0.1/ws/authenticate.request.json)
- [`authenticate.success.json`](../../specification/protocol-examples/v1.0.1/ws/authenticate.success.json)
- [`subscribe.request.json`](../../specification/protocol-examples/v1.0.1/ws/subscribe.request.json)
- [`subscribe.success.json`](../../specification/protocol-examples/v1.0.1/ws/subscribe.success.json)
- [`event.notification.json`](../../specification/protocol-examples/v1.0.1/ws/event.notification.json)
- [`event.notification.definition-upserted.json`](../../specification/protocol-examples/v1.0.1/ws/event.notification.definition-upserted.json)
- [`event.notification.definition-deleted.json`](../../specification/protocol-examples/v1.0.1/ws/event.notification.definition-deleted.json)
- [`unsubscribe.request.json`](../../specification/protocol-examples/v1.0.1/ws/unsubscribe.request.json)
- [`unsubscribe.success.json`](../../specification/protocol-examples/v1.0.1/ws/unsubscribe.success.json)

## 5. 错误语义与处理建议

### 5.1 标准 JSON-RPC 错误

请至少处理以下标准错误：

- `-32700 parse_error`
- `-32600 invalid_request`
- `-32601 method_not_found`
- `-32602 invalid_params`
- `-32603 internal_error`

### 5.2 DevHub 自定义错误

DevHub v1 还定义了一组 `-320xx` 错误，例如：

- `-32001 unauthorized`
- `-32002 forbidden`
- `-32014 app_definition_not_found`
- `-32010 instance_not_found`
- `-32011 invocation_expired`
- `-32012 invocation_timeout`
- `-32020 launch_failed`
- `-32030 delivery_conflict`
- `-32050 invocation_failed`
- `-32099 not_supported`

其中 `invocation_failed` 的关键点是：

- `error.code = -32050`
- `error.message = "invocation_failed"`
- `error.data.invocationId` 指向当前调用
- `error.data.calleeError` 透传被调用方返回的应用错误对象

可直接参考原始错误示例：

- [`invoke-request.invocation-failed.error.json`](../../specification/protocol-examples/v1.0.1/http/invoke-request.invocation-failed.error.json)

定义管理与实例密码场景还需要额外处理以下分支：

- `hub.apps.upsertDefinition` 的业务校验失败走 `-32602 invalid_params`，并在 `error.data.reason="definition_invalid"` 下携带 `errors: ValidationIssue[]`。
- `hub.apps.unregisterInstance` 或同一 `instanceId` 的再次 `hub.apps.registerInstance` 在密码不匹配时返回 `-32002 forbidden`，并携带 `error.data.reason="instance_password_mismatch"`。
- `hub.apps.getDefinition` / `hub.apps.deleteDefinition` 查找未知 Definition 时返回 `-32014 app_definition_not_found`，并在 `error.data.appId` 与 `error.data.scope` 中回传请求目标。

### 5.3 客户端兼容建议

- 必须忽略未知响应字段。
- 必须将未知错误码按通用错误处理，而不是直接崩溃。
- 必须把 HTTP 状态码 `200 OK` 与 JSON-RPC `error` 区分开看：HTTP 成功不代表 RPC 成功。

## 6. Schema 与原始协议示例

如果你需要做严格输入输出校验，可直接消费仓库内发布的 v1.0.1 Schema：

- [`docs/specification/schema/v1.0.1/README.md`](../../specification/schema/v1.0.1/README.md)

其中至少包含：

- `hub-runtime.json`
- `app-definition.json`
- `app-instance.json`
- `invocation.json`
- `rpc-request.json`
- `rpc-response.json`
- `error-response.json`

如果你需要快速拼接请求或对照消息形态，请优先使用：

- [`docs/specification/protocol-examples/v1.0.1/README.md`](../../specification/protocol-examples/v1.0.1/README.md)

## 7. Conformance 自测入口

仓库内的 conformance 向量面向 Spec v1.0.1 协议基线。你可以用它验证自研实现或自研客户端接入是否满足 Spec §10.1 / §10.2。

使用说明见：

- [`host/tests/conformance/README.md`](../../../host/tests/conformance/README.md)

如果你只是要复用官方向量与 runner 来验证“自研 adapter / 自研客户端”，最小前提是先构建 Host，并准备一个外部 adapter manifest：

```bash
dotnet build host/src/DevHub.Host/DevHub.Host.csproj -c Release
python host/tests/conformance/vector_runner.py --adapter-manifest path/to/devhub.adapter.json
```

manifest 需要声明你的 adapter 启动命令；runner 会在命令末尾自动追加 `execution-context.json` 路径，并通过 `DEVHUB_CONFORMANCE_CONTEXT` 环境变量暴露同一路径。第三方 adapter 只需要遵守 [`host/tests/conformance/README.md`](../../../host/tests/conformance/README.md) 中的 Manifest 与输入/输出契约，不需要阅读仓库内 SDK 源代码或参考官方适配器实现细节。

如果你只想聚焦某条向量，可运行：

```bash
python host/tests/conformance/vector_runner.py \
  --adapter-manifest path/to/devhub.adapter.json \
  --vector-id auth.valid_credentials_ping_success
```

如果你想按签名案例分组过滤，可运行：

```bash
python host/tests/conformance/vector_runner.py \
  --adapter-manifest path/to/devhub.adapter.json \
  --case-id CONF-001
```

如果你想把第三方实现与仓库内官方适配器一起对照跑，再额外准备官方 SDK 产物，并显式传入：

```bash
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
python -m pip install -e "./sdks/python[test]" requests
python host/tests/conformance/vector_runner.py \
  --adapter-manifest path/to/devhub.adapter.json \
  --include-official-adapters \
  --official-sdk python
```

## 8. SDK 与 Hub 版本兼容口径

本仓库当前采用“协议版本”和“包版本”分离的兼容策略：

- 当前公开基线是 `protocolVersion=1`，适用于 Hub v1.x。
- SDK 包版本号不要求与 Hub 版本号完全一致；第三方接入也不需要追求版本号对齐。
- 兼容边界以 [`Specification.md`](../../specification/protocol/Specification.md) §9 为准；第三方接入应直接遵循该节。

当前 v1.x 的 `AppDefinition` 基线已经固定为精确复合身份 `(appId, scope)`：持久化只承认 `{appId}--{scopeKey}.json` + 显式 `scope`，其中 `scope: ""` 是 Global 的唯一显式表示；Definition CRUD 与 launch 绑定只按精确 `appId + scope` 工作。历史资产或旧实现如果仍接受 `{appId}.json`、缺失 `scope`、`scope: null`、首尾包含空白字符的 `scope` 或仅按 `appId` 做 Definition CRUD，属于未收敛到当前 v1.x 基线，而不是 v1.x 允许保留的兼容分支。

`Specification.md` §9 中允许的兼容扩展包括：

- 向响应增加可选字段
- 增加新错误码
- 增加新 RPC 方法

客户端需要配套做到：

- 忽略未知字段
- 泛化处理未知错误码
- 允许忽略自己暂不支持的新方法

以下变更属于破坏性变更，必须进入 v2，而不是继续声称兼容 v1.x：

- 更改既有字段类型或语义
- 移除公开字段
- 收紧验证并拒绝此前合法的输入

本文只定义当前公开基线与 `Specification.md` 中的兼容口径，不调整 `sdks/dotnet`、`sdks/javascript`、`sdks/python` 的现有包版本号。
