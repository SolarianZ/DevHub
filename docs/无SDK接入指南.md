# DevHub 无 SDK 接入指南

本文面向不准备直接使用仓库内 `.NET` / `JS/TS` / `Python` SDK 的第三方开发者，说明如何仅基于公开协议资料完成 DevHub Hub v1.x 的原始接入与自测。

## 1. 适用范围

- 权威协议来源始终是 [`Spec.md`](./Spec.md)。
- 本文只整理“不依赖 SDK 源码”的最小接入路径，不扩展或重写任何协议语义。
- 当前兼容基线为 `protocolVersion=1`，适用 Hub v1.x。

如果你已经有自己的 HTTP、WebSocket 与 JSON 处理栈，只需要组合下列公开资料即可完成接入：

- [`Spec.md`](./Spec.md)
- [`docs/schema/v1.0.1/README.md`](./schema/v1.0.1/README.md)
- [`docs/protocol-examples/v1.0.1/README.md`](./protocol-examples/v1.0.1/README.md)
- [`host/tests/conformance/README.md`](../host/tests/conformance/README.md)

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

`hub.json` 至少需要读取这些字段：

- `protocolVersion`
- `httpBaseUrl`
- `wsUrl`
- `tokenFile`
- `runtimeTuning`

接入侧必须把 `hub.json` 当作地址与端口的唯一权威来源，禁止硬编码 `http://127.0.0.1:<port>` 或 WebSocket URL。

## 3. HTTP 鉴权与最小调用

HTTP 端点固定为 `POST {httpBaseUrl}/rpc`，请求体使用 JSON-RPC 2.0 对象。

每个 HTTP 请求都必须携带：

- `Authorization: Bearer <token>`
- `X-DevHub-Protocol: 1`
- `X-DevHub-ClientId: <stable-client-id>`
- `X-DevHub-ClientSessionId: <uuid>`
- `Content-Type: application/json`

最小健康检查可以直接调用 `hub.ping`。原始 JSON 示例见：

- [`ping.request.json`](./protocol-examples/v1.0.1/http/ping.request.json)
- [`ping.success.json`](./protocol-examples/v1.0.1/http/ping.success.json)

如果要接入实例注册与调用链路，可继续参考：

- [`register-instance.request.json`](./protocol-examples/v1.0.1/http/register-instance.request.json)
- [`register-instance.success.json`](./protocol-examples/v1.0.1/http/register-instance.success.json)
- [`invoke-request.request.json`](./protocol-examples/v1.0.1/http/invoke-request.request.json)
- [`invoke-request.success.json`](./protocol-examples/v1.0.1/http/invoke-request.success.json)

## 4. WebSocket 鉴权与事件订阅

WebSocket 连接地址必须直接使用 `hub.json.wsUrl`。

连接建立后：

1. 第一条消息必须是带 `id` 的 `hub.ws.authenticate` 请求。
2. 只有鉴权成功后，才能继续发送 `hub.events.subscribe` / `hub.events.unsubscribe`。
3. Hub 下发事件时，服务端会通过 `hub.event` JSON-RPC 通知推送。

原始 JSON 示例见：

- [`authenticate.request.json`](./protocol-examples/v1.0.1/ws/authenticate.request.json)
- [`authenticate.success.json`](./protocol-examples/v1.0.1/ws/authenticate.success.json)
- [`subscribe.request.json`](./protocol-examples/v1.0.1/ws/subscribe.request.json)
- [`subscribe.success.json`](./protocol-examples/v1.0.1/ws/subscribe.success.json)
- [`event.notification.json`](./protocol-examples/v1.0.1/ws/event.notification.json)
- [`unsubscribe.request.json`](./protocol-examples/v1.0.1/ws/unsubscribe.request.json)
- [`unsubscribe.success.json`](./protocol-examples/v1.0.1/ws/unsubscribe.success.json)

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

- [`invoke-request.invocation-failed.error.json`](./protocol-examples/v1.0.1/http/invoke-request.invocation-failed.error.json)

### 5.3 客户端兼容建议

- 必须忽略未知响应字段。
- 必须将未知错误码按通用错误处理，而不是直接崩溃。
- 必须把 HTTP 状态码 `200 OK` 与 JSON-RPC `error` 区分开看：HTTP 成功不代表 RPC 成功。

## 6. Schema 与原始协议示例

如果你需要做严格输入输出校验，可直接消费仓库内发布的 v1.0.1 Schema：

- [`docs/schema/v1.0.1/README.md`](./schema/v1.0.1/README.md)

其中至少包含：

- `hub-runtime.json`
- `app-definition.json`
- `app-instance.json`
- `invocation.json`
- `rpc-request.json`
- `rpc-response.json`
- `error-response.json`

如果你需要快速拼接请求或对照消息形态，请优先使用：

- [`docs/protocol-examples/v1.0.1/README.md`](./protocol-examples/v1.0.1/README.md)

## 7. Conformance 自测入口

仓库内的 conformance 向量是面向 Hub v1.0.1 的最小公开符合性基线。你可以用它验证自研实现或自研客户端接入是否满足 Spec §10.1 / §10.2。

使用说明见：

- [`host/tests/conformance/README.md`](../host/tests/conformance/README.md)

最常用命令：

```bash
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
python -m pip install -e "./sdks/python[test]" requests
python host/tests/conformance/vector_runner.py
```

如果你只想聚焦某条向量，可运行：

```bash
python host/tests/conformance/vector_runner.py --vector-id auth.valid_credentials_ping_success
```

## 8. SDK 与 Hub 版本兼容口径

本仓库当前采用“协议版本”和“包版本”分离的兼容策略：

- 兼容基线是 `protocolVersion=1` 与 Hub v1.x。
- SDK 包版本号不要求与 Hub 版本号完全一致；第三方接入也不需要追求版本号对齐。
- 只要客户端严格遵循 [`Spec.md`](./Spec.md) §9 的兼容规则，就可以与 Hub v1.x 正常协作。

v1.x 内允许的兼容扩展：

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

本轮文档发布只定义兼容口径，不调整 `sdks/dotnet`、`sdks/javascript`、`sdks/python` 的现有包版本号。
