# DevHub JS/TS SDK

DevHub JS/TS SDK 基于 `docs/Spec.md` 的 Hub v1.x 协议，目标运行时为 Node.js。

## 当前状态

- 已初始化工程骨架（M5-ARCH-002）。
- 已提供基础模型、运行时发现与统一错误模型。
- 已实现 HTTP JSON-RPC 客户端封装（`ping` / `apps` / `launch` / `invoke` / `poll` / `respond` 等）。
- 已实现 WebSocket 事件客户端封装（`authenticate` / `subscribe` / `unsubscribe` / 事件流）。
- 已补齐本地参数校验、成功载荷结构校验与 `invocation_failed` 错误映射辅助，并对 `echo` / `args` / `meta` / `error.data` 等 JSON 载荷执行严格校验，避免静默丢字段或重写值。
- 已补齐 JS SDK 单元测试与 Host 级集成测试，覆盖 `launch`、`invoke` 往返、超时/过期、scope 路由与事件重连场景。
- 运行时发现现已同时支持标准运行时根目录布局（`<DEVHUB_RUNTIME_DIR>/runtime/hub.json`）与既有直接运行时目录布局（`<dir>/hub.json`）。
- 已公开运行时解析器、HTTP 传输与 WebSocket 会话扩展点，便于 fake transport、录制回放或自定义连接策略测试。

> SDK 已内置 `ws` 回退实现，因此在 Node.js 18/19 等未提供全局 `WebSocket` 的环境中也可直接使用事件客户端。

## 规划能力范围

- 运行时发现：读取 `hub.json` 与 `token.txt`。
- HTTP JSON-RPC：`ping`、`apps`、`launch`、`invoke`、`poll`、`respond` 等。
- WebSocket 事件：鉴权、订阅、取消订阅、事件流读取。
- 统一错误模型：`DevHubRpcError`、`reason` / `invocationId` / `calleeError` 辅助属性。
- 本地参数校验：在请求发出前校验关键字段与默认值约束。

## 开发命令

```bash
npm install
npm run typecheck
npm run build
npm test
```

## 已验证能力

- Runtime discovery：读取 `hub.json`、解析 `tokenFile`、应用 `DEVHUB_RUNTIME_DIR` 覆盖，并兼容标准运行时根目录与旧版直接运行时目录两种布局。
- HTTP flows：`ping`、应用定义查询、实例注册/心跳/注销、`launch`、`notify`、`request`、`poll`、`respond`。
- Launch semantics：`started`、`starting`、`already_running` 状态与去重/在线实例分支。
- Invocation semantics：默认选项、`delivery_conflict`、`invocation_timeout`、`invocation_expired`、`invocation_failed`。
- Scope routing：默认 Global、显式空字符串 scope、字面量 `global` 与命名 scope。
- Events flows：WS 鉴权、订阅/取消订阅、未知事件类型错误、断开后重新订阅。

## 快速示例

```ts
import { DevHubClient, DevHubEventsClient } from "@devhub/sdk";

const client = await DevHubClient.fromRuntime({ clientId: "demo" });
const ping = await client.ping({ value: 1 });
console.log(ping.serverTimeUtc, ping.echo);

const eventsClient = await DevHubEventsClient.fromRuntime({ clientId: "demo-events" });
await eventsClient.authenticate();
const subscriptionId = await eventsClient.subscribe();

for await (const evt of eventsClient.readEvents()) {
  console.log("event", evt.type, evt.payload);
}
```

## 高级扩展

默认情况下，推荐继续使用 `DevHubClient.fromRuntime(...)` 与 `DevHubEventsClient.fromRuntime(...)`。

如果需要接入自定义运行时发现、fake transport、录制/回放测试或自定义 WebSocket 会话，可以通过顶层公开导出的扩展点注入：

```ts
import {
  DevHubClient,
  FileSystemRuntimeResolver,
  JsonRpcHttpTransport
} from "@devhub/sdk";

const client = await DevHubClient.fromRuntime(
  { clientId: "example-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    transportFactory: (options, connection) => new JsonRpcHttpTransport(options, connection)
  }
);
```

```ts
import {
  DevHubEventsClient,
  FileSystemRuntimeResolver,
  JsonRpcWsSession
} from "@devhub/sdk";

const eventsClient = await DevHubEventsClient.fromRuntime(
  { clientId: "example-events-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    sessionFactory: (options) => new JsonRpcWsSession(options)
  }
);
```
