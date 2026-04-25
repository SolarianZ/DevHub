# DevHub Host 接口速览

- HTTP JSON-RPC 统一发送到 `POST {httpBaseUrl}/rpc`。
- 浏览器 / WebView 预检统一发送到 `OPTIONS {httpBaseUrl}/rpc`。
- WebSocket 统一连接 `hub.json.wsUrl`，连接建立后第一条消息必须是 `hub.ws.authenticate`。

## 1. 传输入口

| 入口                        | 用途                                                          |
| --------------------------- | ------------------------------------------------------------- |
| `POST {httpBaseUrl}/rpc`    | 承载全部 HTTP JSON-RPC 方法。                                 |
| `OPTIONS {httpBaseUrl}/rpc` | 供浏览器 / WebView 完成 CORS 预检，不承载 JSON-RPC 业务参数。 |
| `hub.json.wsUrl`            | 承载 WebSocket 鉴权、事件订阅、取消订阅和服务端事件推送。     |

所有 HTTP 调用都需要按协议携带 `Authorization`、`X-DevHub-Protocol`、`X-DevHub-ClientId` 和 `X-DevHub-ClientSessionId`。字段细节以 [`../../specification/protocol/Specification.md`](../../specification/protocol/Specification.md) 为准。

当前 Host 对 HTTP `X-DevHub-ClientSessionId` 和 WS `hub.ws.authenticate.clientSessionId` 都要求带连字符的 UUID 字符串（`D` 格式）。

## 2. 接口速览表

| 接口                          | 传输          | 能力说明                                                    |
| ----------------------------- | ------------- | ----------------------------------------------------------- |
| `hub.ping`                    | HTTP / WS     | 检查连通性，并可回显一段调用方提供的数据。                  |
| `hub.ws.authenticate`         | WS            | 绑定当前 WebSocket 连接的客户端身份，后续 WS 方法都依赖它。 |
| `hub.apps.heartbeat`          | HTTP          | 刷新已注册实例的在线时间。                                  |
| `hub.apps.listDefinitions`    | HTTP / WS     | 按应用和作用域列出 AppDefinition。                          |
| `hub.apps.getDefinition`      | HTTP / WS     | 按精确 `appId + scope` 读取单个 AppDefinition。             |
| `hub.apps.validateDefinition` | HTTP          | 校验待提交的 AppDefinition，不写入任何持久化状态。          |
| `hub.apps.upsertDefinition`   | HTTP          | 原子创建或更新 AppDefinition，并刷新定义快照。              |
| `hub.apps.deleteDefinition`   | HTTP          | 删除指定 `appId + scope` 的 AppDefinition。                 |
| `hub.apps.listInstances`      | HTTP / WS     | 按应用和作用域列出实例，可选择包含离线实例。                |
| `hub.apps.registerInstance`   | HTTP          | 注册应用实例，写入或更新实例镜像。                          |
| `hub.apps.unregisterInstance` | HTTP          | 注销应用实例并清理对应镜像。                                |
| `hub.apps.launch`             | HTTP          | 根据 Definition 的启动配置拉起应用进程。                    |
| `hub.invoke.notify`           | HTTP          | 向目标实例发送单向调用，不等待业务结果。                    |
| `hub.invoke.request`          | HTTP          | 向目标实例发送请求并等待业务结果。                          |
| `hub.invoke.poll`             | HTTP          | 由已注册实例轮询领取待处理调用。                            |
| `hub.invoke.respond`          | HTTP          | 由已领取调用的实例回传成功结果或业务错误。                  |
| `hub.events.subscribe`        | WS            | 在当前 WebSocket 连接上创建事件订阅。                       |
| `hub.events.unsubscribe`      | WS            | 取消当前 WebSocket 连接上的事件订阅。                       |
| `hub.event`                   | WS 服务端通知 | 向已订阅客户端投递事件。                                    |

## 3. 接口参数说明

### 3.1 `hub.ping`

| 参数           | 用途                                                                   |
| -------------- | ---------------------------------------------------------------------- |
| `echo`（可选） | 原样回显到响应结果中，便于做连通性验证、链路追踪或附带一段临时上下文。 |

### 3.2 `hub.ws.authenticate`

| 参数              | 用途                                                                  |
| ----------------- | --------------------------------------------------------------------- |
| `token`           | 读取自 `tokenFile` 的 Bearer Token，用于证明当前客户端有权连接 Host。 |
| `protocolVersion` | 声明客户端期望使用的协议版本；当前公开版本为 `1`。                    |
| `clientId`        | 标识调用方应用或组件，便于 Hub 记录调用来源和后续审计。               |
| `clientSessionId` | 标识本次客户端会话实例，用于区分同一 `clientId` 下的不同连接。        |

约束：

- `protocolVersion` 当前只接受 `1`。
- `clientSessionId` 当前 Host 实现要求带连字符的 UUID 字符串（`D` 格式）。

### 3.3 `hub.apps.heartbeat`

| 参数                   | 用途                                   |
| ---------------------- | -------------------------------------- |
| `instanceId`           | 指定要刷新在线时间的实例标识。         |
| `instanceSessionToken` | 当前实例会话凭据，用于校验实例所有权。 |

约束：

- `instanceId` 与 `instanceSessionToken` 都必须是非空字符串。
- 当 `instanceSessionToken` 与当前实例会话不匹配时，Host 返回 `forbidden`，并携带 `reason = "instance_session_token_mismatch"`。

### 3.4 `hub.apps.listDefinitions`

| 参数            | 用途                                                                                                               |
| --------------- | ------------------------------------------------------------------------------------------------------------------ |
| `appId`（可选） | 只查询指定应用的 Definition；省略时遍历全部应用。                                                                  |
| `scope`         | 控制作用域过滤方式：`null` 表示不过滤作用域，`""` 表示只看 Global Definition，其他合法字符串表示只看该精确作用域。 |

约束：

- `scope` 必须显式出现；`null` 与 `""` 语义不同。
- 当前 Host 对 `appId` 额外执行 `^[a-z0-9][a-z0-9.-]*$` 格式校验。

### 3.5 `hub.apps.getDefinition`

| 参数    | 用途                                                                     |
| ------- | ------------------------------------------------------------------------ |
| `appId` | 指定要读取的应用标识。                                                   |
| `scope` | 指定要读取的精确作用域；`""` 表示 Global，其他合法字符串表示显式作用域。 |

约束：

- `scope` 必须是显式字符串，不能传 `null`。
- 当前 Host 对 `appId` 额外执行 `^[a-z0-9][a-z0-9.-]*$` 格式校验。

### 3.6 `hub.apps.validateDefinition`

| 参数                                          | 用途                                                                                           |
| --------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `definition`                                  | 待校验的完整 AppDefinition。                                                                   |
| `definition.appId`                            | 定义所属应用的稳定标识，用于持久化命名和后续路由。                                             |
| `definition.scope`                            | 定义所属作用域；`""` 表示 Global，其他合法字符串表示显式作用域。                               |
| `definition.displayName`                      | 面向用户展示的应用名称。                                                                       |
| `definition.description`（可选）              | 面向用户或维护者展示的说明文字。                                                               |
| `definition.capabilities`（可选）             | 定义该应用公开能力开关的集合。                                                                 |
| `definition.capabilities.rpc`（可选）         | 控制 Host 是否允许把 `hub.invoke.notify` / `hub.invoke.request` 路由到该应用；省略时默认允许。 |
| `definition.capabilities.events`（可选）      | 预留事件能力声明；v1 中 Host 会忽略该字段。                                                    |
| `definition.launch`（可选）                   | 声明由 Host 负责拉起该应用时使用的启动配置。                                                   |
| `definition.launch.exePath`                   | 当提供 `definition.launch` 时必须给出，指定启动程序路径。                                      |
| `definition.launch.argsTemplate`（可选）      | 启动参数模板，可使用规范定义的占位符拼接命令行参数。                                           |
| `definition.launch.workingDirectory`（可选）  | 启动进程时使用的工作目录。                                                                     |
| `definition.launch.dedupeKeyTemplate`（可选） | 启动去重键模板，用于合并去重窗口内的重复启动请求。                                             |

约束：

- `definition`、`definition.capabilities`、`definition.launch` 如果出现，都必须是对象，不能是 `null`。
- `definition.displayName`、`definition.launch.exePath` 在当前 Host 中都要求非空字符串。

### 3.7 `hub.apps.upsertDefinition`

| 参数                                          | 用途                                                                             |
| --------------------------------------------- | -------------------------------------------------------------------------------- |
| `definition`                                  | 要写入的完整 AppDefinition。                                                     |
| `definition.appId`                            | 定义所属应用的稳定标识，用于决定落盘身份和后续查找键。                           |
| `definition.scope`                            | 定义所属作用域；`""` 表示 Global，其他合法字符串表示显式作用域。                 |
| `definition.displayName`                      | 面向用户展示的应用名称。                                                         |
| `definition.description`（可选）              | 面向用户或维护者展示的说明文字。                                                 |
| `definition.capabilities`（可选）             | 定义该应用公开能力开关的集合。                                                   |
| `definition.capabilities.rpc`（可选）         | 控制 Host 是否允许该应用接收 `hub.invoke.notify` / `hub.invoke.request`。        |
| `definition.capabilities.events`（可选）      | 预留事件能力声明；v1 中 Host 会忽略该字段。                                      |
| `definition.launch`（可选）                   | 声明 Host 拉起该应用时使用的启动配置。                                           |
| `definition.launch.exePath`                   | 当提供 `definition.launch` 时必须给出，指定启动程序路径。                        |
| `definition.launch.argsTemplate`（可选）      | 启动参数模板，可注入 `appId`、`scope`、`scopeOrGlobal`、`httpBaseUrl` 等占位符。 |
| `definition.launch.workingDirectory`（可选）  | 启动进程时使用的工作目录。                                                       |
| `definition.launch.dedupeKeyTemplate`（可选） | 启动去重键模板，用于识别相同启动请求。                                           |

约束：

- 参数结构和 `hub.apps.validateDefinition` 完全一致。
- 当前 Host 对 `definition` 应用与 `validateDefinition` 相同的校验规则后才允许写入。

### 3.8 `hub.apps.deleteDefinition`

| 参数    | 用途                                                                     |
| ------- | ------------------------------------------------------------------------ |
| `appId` | 指定要删除的应用标识。                                                   |
| `scope` | 指定要删除的精确作用域；`""` 表示 Global，其他合法字符串表示显式作用域。 |

约束：

- `scope` 必须是显式字符串，不能传 `null`。
- 当前 Host 对 `appId` 额外执行 `^[a-z0-9][a-z0-9.-]*$` 格式校验。

### 3.9 `hub.apps.registerInstance`

| 参数                      | 用途                                                                                                 |
| ------------------------- | ---------------------------------------------------------------------------------------------------- |
| `password`                | 当前 `instanceId` 的注册口令；首次注册时会与实例绑定，后续同一实例的再次注册仍需使用。               |
| `instance`                | 待注册实例的公开信息。                                                                               |
| `instance.instanceId`     | 当前实例的稳定唯一标识，用于心跳、调用投递、注销和回包。                                             |
| `instance.appId`          | 指明该实例属于哪个应用。                                                                             |
| `instance.scope`          | 指明该实例属于哪个作用域；`""` 表示 Global，其他合法字符串表示显式作用域。                           |
| `instance.pid`            | 该实例对应的本地进程 ID，便于诊断和运行态展示。                                                      |
| `instance.invoke`         | 声明该实例支持的调用能力。                                                                           |
| `instance.invoke.poll`    | 表示实例是否允许通过 `hub.invoke.poll` 领取待处理调用。                                              |
| `instance.invoke.respond` | 表示实例是否允许通过 `hub.invoke.respond` 回传调用结果。                                             |
| `instance.meta`（可选）   | 附加元数据字典，可携带实例侧的附加信息；如果实例要回绑启动记录，也可在此放入与启动过程关联的元数据。 |

约束：

- `password` 必须是非空字符串。
- `instance.instanceId` 最大长度为 `256`，并且必须匹配 `^[a-zA-Z0-9._:-]+$`。
- `instance.appId` 必须匹配 `^[a-z0-9][a-z0-9.-]*$`。
- `instance.scope` 必须是显式字符串，不能传 `null`。
- `instance.pid` 必须是大于等于 `1` 的整数。
- `instance.invoke.poll` 与 `instance.invoke.respond` 都必须显式提供布尔值。
- `instance.meta` 如果出现，必须是对象。

返回说明：

- 成功结果顶层会返回新的 `instanceSessionToken`。
- 每次成功的 re-register 都会轮换 `instanceSessionToken`；旧 token 随即失效。
- `instanceSessionToken` 不会出现在 `AppInstance`、`hub.apps.listInstances` 或 `app.instance.*` 事件载荷中。

### 3.10 `hub.apps.unregisterInstance`

| 参数                   | 用途                               |
| ---------------------- | ---------------------------------- |
| `instanceId`           | 指定要注销的实例标识。             |
| `instanceSessionToken` | 当前实例会话凭据，用于校验所有权。 |

约束：

- `instanceId` 与 `instanceSessionToken` 都必须是非空字符串。
- 当 `instanceSessionToken` 与当前实例会话不匹配时，Host 返回 `forbidden`，并携带 `reason = "instance_session_token_mismatch"`。

### 3.11 `hub.apps.listInstances`

| 参数                     | 用途                                                                                                         |
| ------------------------ | ------------------------------------------------------------------------------------------------------------ |
| `appId`（可选）          | 只查询指定应用的实例；省略时遍历全部应用。                                                                   |
| `scope`                  | 控制作用域过滤方式：`null` 表示不过滤作用域，`""` 表示只看 Global 实例，其他合法字符串表示只看该精确作用域。 |
| `includeOffline`（可选） | 是否把离线实例也包含在返回结果中；省略时默认 `false`。                                                       |

约束：

- `scope` 必须显式出现；`null` 与 `""` 语义不同。
- `includeOffline` 如果出现，必须是布尔值。
- 当前 Host 对 `appId` 额外执行 `^[a-z0-9][a-z0-9.-]*$` 格式校验。

### 3.12 `hub.apps.launch`

| 参数                        | 用途                                                                     |
| --------------------------- | ------------------------------------------------------------------------ |
| `appId`                     | 指定要启动的应用标识。                                                   |
| `scope`                     | 指定要启动的精确作用域；`""` 表示 Global，其他合法字符串表示显式作用域。 |
| `dedupeKey`（可选）         | 显式给出本次启动请求的去重键；省略时由 Host 根据 Definition 模板生成。   |
| `waitForRegisterMs`（可选） | 指定启动后等待实例完成注册的毫秒数；省略时默认 `0`，即不等待注册结果。   |

约束：

- `scope` 必须是显式字符串。
- `dedupeKey` 如果出现，必须是字符串或 `null`。
- `waitForRegisterMs` 如果出现，必须是大于等于 `0` 的整数。
- 对同一解析后 `dedupeKey`，只要已有启动记录对应的进程仍存活且尚未完成注册绑定，Host 都会继续返回 `already_running`，不会因为超过去重窗口就放行第二次启动。

### 3.13 `hub.invoke.notify`

| 参数                             | 用途                                                                                                                                    |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `appId`                          | 指定目标应用标识。                                                                                                                      |
| `target`                         | 描述路由目标。                                                                                                                          |
| `target.scope`                   | 指定调用应路由到哪个作用域；`""` 表示 Global，其他合法字符串表示显式作用域。即使指定了 `target.instanceId`，这个字段也必须显式提供。    |
| `target.instanceId`（可选）      | 指定精确目标实例；省略时由 Host 在 `appId + scope` 范围内选择可用实例。                                                                 |
| `method`                         | 传给目标应用的业务方法名。                                                                                                              |
| `args`                           | 传给目标应用的业务参数，可为任意 JSON 值。                                                                                              |
| `options`（可选）                | 调用投递与存活时间相关的行为选项。                                                                                                      |
| `options.ttlMs`（可选）          | 调用在 Host 内保持有效的最长毫秒数；省略时默认 `60000`。                                                                                |
| `options.queueIfOffline`（可选） | 当前没有在线实例时，是否允许调用先进入挂起队列；省略时默认 `true`。                                                                     |
| `options.autoLaunch`（可选）     | 当前没有在线实例时，是否允许 Host 自动拉起目标应用；未指定 `target.instanceId` 时默认 `true`，指定 `target.instanceId` 时默认 `false`。 |

约束：

- `target` 必须是对象。
- `target.scope` 必须是显式字符串。
- `target.instanceId` 如果出现，必须是非空字符串或 `null`。
- `options.ttlMs` 如果出现，必须是大于等于 `1000` 的整数。
- 指定 `target.instanceId` 时，当前 Host 不允许显式传 `options.autoLaunch = true`。
- 当前 Host 要求 `options.autoLaunch = true` 时同时满足 `options.queueIfOffline = true`。
- 如果以 JSON-RPC notification 方式省略 `id`，HTTP 层固定返回空的 `200 OK` 响应体；需要读取成功结果或错误时，必须改为发送带 `id` 的普通 request。

### 3.14 `hub.invoke.request`

| 参数                             | 用途                                                                                                                                    |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `appId`                          | 指定目标应用标识。                                                                                                                      |
| `target`                         | 描述路由目标。                                                                                                                          |
| `target.scope`                   | 指定调用应路由到哪个作用域；`""` 表示 Global，其他合法字符串表示显式作用域。即使指定了 `target.instanceId`，这个字段也必须显式提供。    |
| `target.instanceId`（可选）      | 指定精确目标实例；省略时由 Host 在 `appId + scope` 范围内选择可用实例。                                                                 |
| `method`                         | 传给目标应用的业务方法名。                                                                                                              |
| `args`                           | 传给目标应用的业务参数，可为任意 JSON 值。                                                                                              |
| `options`（可选）                | 调用投递、等待和自动启动相关的行为选项。                                                                                                |
| `options.ttlMs`（可选）          | 调用在 Host 内保持有效的最长毫秒数；省略时默认 `300000`。                                                                               |
| `options.waitTimeoutMs`（可选）  | 调用方等待业务响应的最长毫秒数；省略时默认 `120000`，且必须小于等于 `ttlMs`。                                                           |
| `options.queueIfOffline`（可选） | 当前没有在线实例时，是否允许调用先进入挂起队列；省略时默认 `true`。                                                                     |
| `options.autoLaunch`（可选）     | 当前没有在线实例时，是否允许 Host 自动拉起目标应用；未指定 `target.instanceId` 时默认 `true`，指定 `target.instanceId` 时默认 `false`。 |

约束：

- `target`、`target.scope`、`target.instanceId` 的约束与 `hub.invoke.notify` 相同。
- `options.ttlMs` 如果出现，必须是大于等于 `1000` 的整数。
- `options.waitTimeoutMs` 如果出现，必须是大于等于 `1` 的整数，并且必须小于等于 `ttlMs`。
- 指定 `target.instanceId` 时，当前 Host 不允许显式传 `options.autoLaunch = true`。
- 当前 Host 要求 `options.autoLaunch = true` 时同时满足 `options.queueIfOffline = true`。
- 当前 Host 在 HTTP caller 主动断连后只会结束当前等待，不会仅因断连把 invocation 推进到 timeout / expired；后续实例侧 `poll/respond` 仍按原始 `ttlMs` / `waitTimeoutMs` 预算继续生效。

### 3.15 `hub.invoke.poll`

| 参数                   | 用途                                                               |
| ---------------------- | ------------------------------------------------------------------ |
| `instanceId`           | 指定由哪个已注册实例来领取待处理调用。                             |
| `instanceSessionToken` | 当前实例会话凭据，用于校验实例所有权。                             |
| `maxCount`（可选）     | 一次最多领取多少条调用；省略时默认 `10`。                          |
| `waitMs`（可选）       | 长轮询等待时间；省略时默认 `25000`，`0` 表示立即返回当前可用结果。 |

约束：

- `instanceId` 与 `instanceSessionToken` 都必须是非空字符串。
- `maxCount` 如果出现，当前 Host 只接受 `1` 到 `100` 之间的整数。
- `waitMs` 如果出现，必须是大于等于 `0` 的整数。
- 当 `instanceSessionToken` 与当前实例会话不匹配时，Host 返回 `forbidden`，并携带 `reason = "instance_session_token_mismatch"`。

### 3.16 `hub.invoke.respond`

参数组合规则：`value` 与 `error` 必须二选一，且只能出现其中一个。

| 参数                 | 用途                               |
| -------------------- | ---------------------------------- |
| `instanceId`         | 指定当前由哪个实例回传处理结果。   |
| `instanceSessionToken` | 当前实例会话凭据，用于校验实例所有权。 |
| `invocationId`       | 指定要完成的调用标识。             |
| `value`              | 成功结果负载，与 `error` 二选一。  |
| `error`              | 业务错误负载，与 `value` 二选一。  |
| `error.code`         | 目标应用定义的业务错误码。         |
| `error.message`      | 目标应用定义的业务错误名称或说明。 |
| `error.data`（可选） | 目标应用附带的结构化错误上下文。   |

约束：

- `instanceId`、`instanceSessionToken` 与 `invocationId` 都必须是非空字符串。
- `error` 如果出现，必须是对象，并且至少包含整数 `code` 与非空字符串 `message`。
- 当 `instanceSessionToken` 与当前实例会话不匹配时，Host 返回 `forbidden`，并携带 `reason = "instance_session_token_mismatch"`。

### 3.17 `hub.events.subscribe`

| 参数            | 用途                                                                           |
| --------------- | ------------------------------------------------------------------------------ |
| `types`（可选） | 指定本次订阅关注的事件类型列表；省略或传空数组表示订阅当前连接可见的全部事件。 |

当前公开事件类型包括：

- `app.definition.upserted`
- `app.definition.deleted`
- `app.instance.registered`
- `app.instance.unregistered`
- `invocation.queued`
- `invocation.delivered`
- `invocation.completed`
- `invocation.failed`

约束：

- `types` 如果出现，必须是由非空字符串组成的数组。
- 当前 Host 会拒绝未知事件类型，并返回 `reason = "unsupported_event_type"`。

### 3.18 `hub.events.unsubscribe`

| 参数             | 用途                   |
| ---------------- | ---------------------- |
| `subscriptionId` | 指定要取消的订阅标识。 |

约束：

- `subscriptionId` 必须是非空字符串。
- 取消未知 `subscriptionId` 仍会返回 `{ "ok": true }`。

### 3.19 `hub.event`

`hub.event` 是 Host 主动发送到客户端的 JSON-RPC 通知，不需要客户端发起调用。

| 参数                        | 用途                                                       |
| --------------------------- | ---------------------------------------------------------- |
| `subscriptionId`            | 标识该通知命中了当前连接上的哪个订阅。                     |
| `type`                      | 标识事件类型，便于客户端按类型分发处理逻辑。               |
| `timeUtc`                   | 事件生成时间。                                             |
| `payload`                   | 当前事件类型对应的业务载荷。                               |
| `payload.appId`（可选）     | 事件关联的应用标识；定义、实例和调用相关事件都会包含它。   |
| `payload.scope`（可选）     | 事件关联的作用域；Global 使用 `""`。                       |
| `payload.definition`（可选） | `app.definition.upserted` 事件携带的最新 Definition。      |
| `payload.instanceId`（可选） | 实例注册、注销和调用路由相关事件关联的实例标识。           |
| `payload.invocationId`（可选） | 调用生命周期事件关联的调用标识。                         |
| `payload.error`（可选）     | `invocation.failed` 事件里由被调用方返回的业务错误详情。   |

不同事件会携带不同 `payload` 字段，客户端应按 `type` 解析，并忽略自己不认识的附加字段。

`hub.invoke.request` 的同步错误响应使用 `error.data.calleeError`，但 `invocation.failed` 事件在当前 Host 中使用 `payload.error`。

## 4. 核对结果

### 4.1 Specification 与 Host 实现未完全对齐的点

- `hub.ws.authenticate.clientSessionId`
  Specification 的方法参数表（`§6.3.2`）只写为 `string`；当前 Host 实现在 [`host/src/DevHub.Host/Transport/WebSocketAuthenticationProcessor.cs`](../../../host/src/DevHub.Host/Transport/WebSocketAuthenticationProcessor.cs) 中要求带连字符的 UUID 字符串（`D` 格式），不满足时返回 `-32602 invalid_params`。
- `hub.apps.listDefinitions` / `hub.apps.getDefinition` / `hub.apps.deleteDefinition` / `hub.apps.listInstances` 的 `appId`
  Specification 的对应接口段落聚焦于 `string` 与 `appId + scope` 的查找语义；当前 Host 实现在 [`host/src/DevHub.Core/Services/Rpc/Handlers/AppDefinitionsHandler.cs`](../../../host/src/DevHub.Core/Services/Rpc/Handlers/AppDefinitionsHandler.cs) 和 [`host/src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`](../../../host/src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs) 中额外执行 `^[a-z0-9][a-z0-9.-]*$` 格式校验，非法格式会返回 `-32602 invalid_params`。
- `hub.event` 中 `invocation.failed` 的错误载荷字段名
  Specification 当前没有单独固定 `invocation.failed` 事件负载里的错误字段名；当前 Host 实现在 [`host/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`](../../../host/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs) 中发布的是 `payload.error`，不是请求错误响应里的 `calleeError` 命名。
