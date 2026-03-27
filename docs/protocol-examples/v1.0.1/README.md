# DevHub 原始协议示例（v1.0.1）

本目录提供不依赖 SDK 的原始协议示例，帮助第三方开发者直接按 JSON-RPC 2.0 组织 HTTP / WebSocket 报文。

## 1. 目录说明

- `http/`：HTTP `POST /rpc` 的 JSON 请求体与响应体示例
- `ws/`：WebSocket 入站 / 出站 JSON-RPC 消息示例

这些文件只描述 JSON 消息本身，不重复封装 SDK 调用，也不替代 [`docs/Spec.md`](../../Spec.md)。

## 2. 占位符约定

示例中的占位符均为字面字符串，使用前需要替换成真实值：

- `${HOST_TOKEN}`：从 `hub.json.tokenFile` 读取到的 bearer token
- `${SUBSCRIPTION_ID}`：订阅成功后返回的 `subscriptionId`
- `${INVOCATION_ID}`：调用成功或错误响应中的 `invocationId`
- `${SERVER_TIME_UTC}`：服务端返回的 UTC 时间戳
- `${INSTANCE_REGISTERED_AT_UTC}` / `${INSTANCE_LAST_SEEN_UTC}`：服务端管理的实例时间戳
- `${EVENT_TIME_UTC}`：事件消息中的时间戳

## 3. 动态读取规则

以下值必须从运行中的 Hub 动态获取，不能硬编码：

- HTTP 地址：`hub.json.httpBaseUrl`
- WebSocket 地址：`hub.json.wsUrl`
- token：`hub.json.tokenFile`

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
- [`http/register-instance.request.json`](./http/register-instance.request.json)
- [`http/register-instance.success.json`](./http/register-instance.success.json)
- [`http/invoke-request.request.json`](./http/invoke-request.request.json)
- [`http/invoke-request.success.json`](./http/invoke-request.success.json)
- [`http/invoke-request.invocation-failed.error.json`](./http/invoke-request.invocation-failed.error.json)

WebSocket：

- [`ws/authenticate.request.json`](./ws/authenticate.request.json)
- [`ws/authenticate.success.json`](./ws/authenticate.success.json)
- [`ws/subscribe.request.json`](./ws/subscribe.request.json)
- [`ws/subscribe.success.json`](./ws/subscribe.success.json)
- [`ws/event.notification.json`](./ws/event.notification.json)
- [`ws/unsubscribe.request.json`](./ws/unsubscribe.request.json)
- [`ws/unsubscribe.success.json`](./ws/unsubscribe.success.json)

## 5. 与 Schema / Conformance 的关系

- 如果你需要做结构校验，请配合 [`docs/schema/v1.0.1/README.md`](../../schema/v1.0.1/README.md) 使用。
- 如果你需要验证实现是否满足 Spec §10 的最小基线，请配合 [`host/tests/conformance/README.md`](../../../host/tests/conformance/README.md) 使用。
