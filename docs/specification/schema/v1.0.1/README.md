# DevHub Schema 包（v1.0.1）

本目录提供 DevHub Hub v1.0.1 的版本化 JSON Schema 资产，供第三方开发者在不阅读 SDK 源代码的前提下完成：

- `hub.json` 发现文件校验
- AppDefinition / AppInstance / AppInstanceRegistration / Invocation / ValidationIssue 数据结构校验
- JSON-RPC request、notification、成功响应、错误响应与 `hub.event` 事件通知的信封校验

## 1. 规范来源

- `app-definition.json`
- `app-instance.json`
- `app-instance-registration.json`
- `invocation.json`
- `hub-runtime.json`
- `validation-issue.json`

以上 6 个文件直接整理自 [`Specification.md`](../../protocol/Specification.md) §5。

- `rpc-request.json`
- `rpc-notification.json`
- `rpc-response.json`
- `error-response.json`
- `event-notification.json`

以上 5 个文件依据 [`Specification.md`](../../protocol/Specification.md) §3.1、§6.1、§6.3.19、§8 与附录 A 整理。

## 2. 版本与 URI 约定

- Schema 标准：Draft-07
- 发布目录版本：`v1.0.1`
- `$id` 沿用 Spec 约定的 `/v1/` URI，不改写为 `/v1.0.1/`

这意味着仓库目录版本用于发布与引用管理，`$id` 用于表达协议大版本稳定标识，两者不冲突。

## 3. 文件清单

- [`app-definition.json`](./app-definition.json)
- [`app-instance.json`](./app-instance.json)
- [`app-instance-registration.json`](./app-instance-registration.json)
- [`invocation.json`](./invocation.json)
- [`hub-runtime.json`](./hub-runtime.json)
- [`validation-issue.json`](./validation-issue.json)
- [`rpc-request.json`](./rpc-request.json)
- [`rpc-notification.json`](./rpc-notification.json)
- [`rpc-response.json`](./rpc-response.json)
- [`error-response.json`](./error-response.json)
- [`event-notification.json`](./event-notification.json)

## 4. 使用方式

本目录可作为 DevHub v1.0.1 的版本化协议资产；各文件与 [`Specification.md`](../../protocol/Specification.md) 保持同一套公开契约约束：

- 读取 `hub.json` 后，用 `hub-runtime.json` 做发现文件校验；该 schema 要求 `httpBaseUrl` 使用 loopback origin，`wsUrl` 使用 loopback WebSocket 绝对 URL 且路径固定为 `/ws`，并要求 `tokenFile` 为绝对路径。
- 读取或生成应用定义时，用 `app-definition.json` 校验；该 schema 要求 payload 显式携带 `scope`，其中 Global Definition 使用 `""`，显式作用域 Definition 使用 canonical identifier grammar 的非空字符串。`launch` 与 `launch.exePath` 均为可选结构；`launch.args` 为可选字符串数组，每个元素对应一个 argv 参数。
- 读取实例镜像或注册返回值时，用 `app-instance.json` 校验；该 schema 明确禁止 `password` 与 `instanceSessionToken` 出现在公共实例结构中。
- 组织 `hub.apps.registerInstance.params.instance` 时，用 `app-instance-registration.json` 校验；注册类 `scope` 字段必须显式出现，且 Global 作用域使用 `""`。该 schema 同样禁止 `password` 与 `instanceSessionToken` 混入 `params.instance`。注册成功结果顶层返回的 `instanceSessionToken` 属于方法结果信封字段，不属于 `AppInstance` / `AppInstanceRegistration` 结构本体。
- 处理轮询项或调用上下文时，用 `invocation.json` 校验；其中 `appId`、`target.scope` 与可选 `target.instanceId` 都遵循同一套 canonical identifier grammar，非 null `target.instanceId` 长度不超过 256 字符，`caller.clientSessionId` **必须**是 canonical UUID string。`delivery` 存在时必须包含 `leaseSeconds`、`attempt` 与 Hub 签发的 `leaseToken`。
- 解析 `hub.apps.validateDefinition` 或 `definition_invalid` 错误中的字段级诊断时，用 `validation-issue.json` 校验。
- 发送带 `id` 的 JSON-RPC request 时，用 `rpc-request.json` 校验；其中 numeric `id` 只接受有符号 64 位整数范围内的整数值。该 schema 将 `params` 的通用形状收敛为对象，并只为 `hub.getVersion` 保留 `params: null` 的规范例外；更细的方法级字段约束仍以 [`Specification.md`](../../protocol/Specification.md) 为准。
- 发送省略 `id` 的 JSON-RPC notification 时，用 `rpc-notification.json` 校验；该 schema 将 `params` 的通用形状收敛为对象，对应 DevHub 当前规范中的通知入口。
- 接收成功响应时，用 `rpc-response.json` 校验；接收错误响应时，用 `error-response.json` 校验。两个响应 schema 中的 numeric `id` 同样只接受有符号 64 位整数范围内的整数值。这两个 schema 分别约束成功/错误信封，不能同时接受同一个同时带 `result` 与 `error` 的响应对象。
- 接收 `hub.event` 事件通知时，用 `event-notification.json` 校验；该 schema 限定当前支持的 8 种事件类型，并对 Definition 生命周期事件、Instance 生命周期事件与 invocation 事件施加最小 payload 约束。`app.definition.upserted.payload.definition.scope` 与 `payload.scope` 的相等性属于 [`Specification.md`](../../protocol/Specification.md) §6.3.19 的规范要求；标准 Draft-07 schema 不表达跨字段动态相等校验，该一致性由协议实现或 conformance 测试校验。

实例所有权相关的 `instanceSessionToken` 还适用于 `hub.apps.heartbeat`、`hub.apps.unregisterInstance`、`hub.invoke.poll` 与 `hub.invoke.respond` 的顶层 `params`。`hub.invoke.respond` 还必须携带当前 delivery 的 `leaseToken`。这些字段属于方法级参数而不是通用数据模型，因此未单独收敛到 `app-instance*.json` 中。

`app-definition.json` 只描述单个 Definition payload 的结构；Definition 的公开身份仍以 [`Specification.md`](../../protocol/Specification.md) §4.1.4 / §5.1.1 为准，即精确 `(appId, scope)` 复合身份，并持久化到 `apps/definitions.json` 的版本化目录索引中。

[`protocol-examples/v1.0.1`](../../protocol-examples/v1.0.1/README.md) 中的原始协议示例可直接作为对应 JSON-RPC 信封与事件通知 schema 的结构校验样例；其中演示失败路径的业务载荷应按具体方法语义理解，不作为通用数据模型 schema 的正向样例。

如果需要请求/响应示例，请同时参考：

- [`protocol-examples/v1.0.1/README.md`](../../protocol-examples/v1.0.1/README.md)
