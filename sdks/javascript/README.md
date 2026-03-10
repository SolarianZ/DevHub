# DevHub JS/TS SDK

DevHub JS/TS SDK 基于 `docs/Spec.md` 的 Hub v1.x 协议，目标运行时为 Node.js。

## 当前状态

- 已初始化工程骨架（M5-ARCH-002）。
- 已提供基础模型、运行时发现与统一错误模型。
- 已实现 HTTP JSON-RPC 客户端封装（`ping` / `apps` / `invoke` 等）。
- 已实现 WebSocket 事件客户端封装（`authenticate` / `subscribe` / `unsubscribe` / 事件流）。

> 若运行时未提供全局 `WebSocket`（例如 Node.js 18），请安装 `ws` 依赖以启用事件客户端。

## 规划能力范围

- 运行时发现：读取 `hub.json` 与 `token.txt`。
- HTTP JSON-RPC：`ping`、`apps`、`invoke` 等。
- WebSocket 事件：鉴权、订阅、取消订阅、事件流读取。
- 统一错误模型：`DevHubRpcError`。

## 开发命令

```bash
npm install
npm run build
npm test
```

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
