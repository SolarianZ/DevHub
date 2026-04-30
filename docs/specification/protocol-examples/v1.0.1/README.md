# DevHub 原始协议示例（v1.0.1）

本目录提供不依赖 SDK 的原始协议示例，帮助第三方开发者直接按 JSON-RPC 2.0 组织 HTTP / WebSocket 报文。

## 1. 目录说明

- `http/`：HTTP `POST /rpc` 的 JSON 请求体与响应体示例
- `ws/`：WebSocket 入站 / 出站 JSON-RPC 消息示例

这些文件只描述 JSON 消息本身，不重复封装 SDK 调用，也不替代 [`Specification.md`](../../protocol/Specification.md)。

## 2. 样例值约定

本目录中的 JSON 文件统一使用固定字面值，目的是提供可直接消费、可直接校验的协议报文样例。正向样例使用合法业务值；用于演示校验失败或错误路径的样例，会在保持 JSON-RPC 报文形状合法的前提下包含故意构造的业务无效值。涉及运行时生成或返回的字段时，示例统一采用以下样例值：

- Host token：`devhub-host-token-sample`
- Host version：`1.0.1`
- `subscriptionId`：`sub-sample-001`
- `invocationId`：`invk-sample-request-001`
- `launchId`：`launch-sample-app-global-001`
- `instanceSessionToken`：`inst-session-node-01-alpha-001`
- `serverTimeUtc`：`2026-03-28T12:34:56Z`
- `registeredAtUtc`：`2026-03-28T12:35:01Z`
- `lastSeenUtc`：`2026-03-28T12:35:16Z`
- `event.timeUtc`：`2026-03-28T12:36:00Z`

本目录的正向示例统一采用以下 canonical 标识符：

- `appId = "Sample.App"`
- Global `scope = ""`
- 显式 `scope = "Workspace-A.v2"`
- `instanceId = "NODE_01.alpha"` / `NODE_01.beta`

JSON-RPC request `id` 的示例默认优先使用字符串，避免跨语言 numeric 精度差异；如果接入方自行改用 numeric `id`，该值必须是有符号 64 位整数范围内的整数，Hub 会拒绝小数或超出该范围的 numeric `id`。

## 3. 动态读取规则

以下地址、凭据与运行时返回值由运行中的 Hub 决定；示例中的固定字面值仅用于说明报文形状：

- HTTP 地址：`hub.json.httpBaseUrl`
- WebSocket 地址：`hub.json.wsUrl`
- token：`hub.json.tokenFile`
- Host 版本：`hub.getVersion.result.version`
- `subscriptionId`：`hub.events.subscribe.result.subscriptionId`
- `instanceSessionToken`：`hub.apps.registerInstance.result.instanceSessionToken`
- `invocationId`：`hub.invoke.notify` / `hub.invoke.request` 的结果或错误载荷
- `launchId`：`hub.apps.launch.result.launchId`
- 所有服务端生成的 UTC 时间戳字段

HTTP 示例默认对应：

- 请求方法：`POST`
- 路径：`${httpBaseUrl}/rpc`

并且仍需在真实请求中补齐这些请求头：

- `Authorization: Bearer ${HOST_TOKEN}`
- `X-DevHub-Protocol: 1`
- `X-DevHub-ClientId: <client-id>`
- `X-DevHub-ClientSessionId: <uuid>`
- `Content-Type: application/json`

## 4. 文件清单

HTTP：

- [`http/ping.request.json`](./http/ping.request.json)
- [`http/ping.success.json`](./http/ping.success.json)
- [`http/get-version.request.json`](./http/get-version.request.json)
- [`http/get-version.success.json`](./http/get-version.success.json)
- [`http/list-definitions.null-scope.request.json`](./http/list-definitions.null-scope.request.json)
- [`http/list-definitions.global.request.json`](./http/list-definitions.global.request.json)
- [`http/list-definitions.success.json`](./http/list-definitions.success.json)
- [`http/list-definitions.global.success.json`](./http/list-definitions.global.success.json)
- [`http/get-definition.request.json`](./http/get-definition.request.json)
- [`http/get-definition.success.json`](./http/get-definition.success.json)
- [`http/validate-definition.valid.request.json`](./http/validate-definition.valid.request.json)
- [`http/validate-definition.valid.success.json`](./http/validate-definition.valid.success.json)
- [`http/validate-definition.invalid.request.json`](./http/validate-definition.invalid.request.json)
- [`http/validate-definition.invalid.success.json`](./http/validate-definition.invalid.success.json)
- [`http/upsert-definition.request.json`](./http/upsert-definition.request.json)
- [`http/upsert-definition.success.json`](./http/upsert-definition.success.json)
- [`http/upsert-definition.definition-invalid.error.json`](./http/upsert-definition.definition-invalid.error.json)
- [`http/delete-definition.request.json`](./http/delete-definition.request.json)
- [`http/delete-definition.success.json`](./http/delete-definition.success.json)
- [`http/register-instance.request.json`](./http/register-instance.request.json)
- [`http/register-instance.success.json`](./http/register-instance.success.json)
- [`http/heartbeat.request.json`](./http/heartbeat.request.json)
- [`http/heartbeat.success.json`](./http/heartbeat.success.json)
- [`http/list-instances.null-scope.request.json`](./http/list-instances.null-scope.request.json)
- [`http/list-instances.global.request.json`](./http/list-instances.global.request.json)
- [`http/list-instances.success.json`](./http/list-instances.success.json)
- [`http/list-instances.global.success.json`](./http/list-instances.global.success.json)
- [`http/get-instance.request.json`](./http/get-instance.request.json)
- [`http/get-instance.success.json`](./http/get-instance.success.json)
- [`http/unregister-instance.request.json`](./http/unregister-instance.request.json)
- [`http/unregister-instance.success.json`](./http/unregister-instance.success.json)
- [`http/launch.request.json`](./http/launch.request.json)
- [`http/launch.success.json`](./http/launch.success.json)
- [`http/invoke-notify.notification.request.json`](./http/invoke-notify.notification.request.json)
- [`http/invoke-request.request.json`](./http/invoke-request.request.json)
- [`http/invoke-request.success.json`](./http/invoke-request.success.json)
- [`http/invoke-request.invocation-failed.error.json`](./http/invoke-request.invocation-failed.error.json)
- [`http/invoke-poll.request.json`](./http/invoke-poll.request.json)
- [`http/invoke-respond.request.json`](./http/invoke-respond.request.json)

WebSocket：

- [`ws/authenticate.request.json`](./ws/authenticate.request.json)
- [`ws/authenticate.success.json`](./ws/authenticate.success.json)
- [`ws/get-version.request.json`](./ws/get-version.request.json)
- [`ws/get-version.success.json`](./ws/get-version.success.json)
- [`ws/subscribe.request.json`](./ws/subscribe.request.json)
- [`ws/subscribe.success.json`](./ws/subscribe.success.json)
- [`ws/event.notification.json`](./ws/event.notification.json)
- [`ws/event.notification.definition-upserted.json`](./ws/event.notification.definition-upserted.json)
- [`ws/event.notification.definition-deleted.json`](./ws/event.notification.definition-deleted.json)
- [`ws/unsubscribe.request.json`](./ws/unsubscribe.request.json)
- [`ws/unsubscribe.success.json`](./ws/unsubscribe.success.json)

## 5. 与 Schema / Conformance 的关系

- 本目录中的 JSON 文件本身可直接作为对应 JSON-RPC 信封 schema 的结构校验输入；其中演示失败路径的业务载荷，应按具体方法语义解释，不作为通用数据模型 schema 的正向样例。
- 带 `id` 的 HTTP / WS 请求示例对应 [`rpc-request.json`](../../schema/v1.0.1/rpc-request.json)。
- 省略 `id` 的 [`http/invoke-notify.notification.request.json`](./http/invoke-notify.notification.request.json) 对应 [`rpc-notification.json`](../../schema/v1.0.1/rpc-notification.json)。
- 所有 `*.success.json` 响应示例对应 [`rpc-response.json`](../../schema/v1.0.1/rpc-response.json)。
- 所有 `*.error.json` 错误示例对应 [`error-response.json`](../../schema/v1.0.1/error-response.json)。
- 所有 `ws/event.notification*.json` 事件示例对应 [`event-notification.json`](../../schema/v1.0.1/event-notification.json)。
- `get-instance.*.json` 演示 `hub.apps.getInstance` 的精确实例查询语义；`launch.*.json` 演示 `hub.apps.launch` 的显式 `appId + scope` 启动语义。
- Definition 相关示例始终按精确复合身份 `appId + scope` 组织；`scope: ""` 表示 Global Definition，其他合法非空字符串表示显式作用域 Definition。示例中的 `getDefinition`、`deleteDefinition`、`upsertDefinition` 与 `app.definition.*` 事件都不会演示仅按 `appId` 定位或 `{appId}.json` 持久化。
- `listDefinitions` 与 `listInstances` 都演示了 `scope = null` 时的不按作用域过滤语义，以及 `scope = ""` 时仅匹配 Global 的语义。除这两个列表查询外，本目录不会用 `scope: null` 表示 Global，也不会省略必须显式存在的 `scope` 字段。
- `register-instance.success.json` 会返回顶层 `instanceSessionToken`；后续 `heartbeat`、`unregisterInstance`、`hub.invoke.poll` 与 `hub.invoke.respond` 示例都复用该 token，但该 token 不会出现在 `AppInstance`、`listInstances` 或事件载荷中。
- `invoke-notify.notification.request.json` 演示的是省略 `id` 的 JSON-RPC notification。该用法在 HTTP 下对应空的 `200 OK` 响应体；如需获得 JSON-RPC `error` 或成功结果，必须改为发送带 `id` 的普通 request。
- 如需做结构校验，请配合 [`schema/v1.0.1/README.md`](../../schema/v1.0.1/README.md) 使用。
- 如需验证实现是否满足 Spec §10 的最小基线，请配合 [`host/tests/conformance/README.md`](../../../../host/tests/conformance/README.md) 使用。
