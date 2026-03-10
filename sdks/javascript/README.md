# DevHub JS/TS SDK

DevHub JS/TS SDK 基于 `docs/Spec.md` 的 Hub v1.x 协议，目标运行时为 Node.js。

## 当前状态

- 已初始化工程骨架（M5-ARCH-002）。
- 已提供基础模型、运行时发现与错误模型的初始实现。
- HTTP JSON-RPC 与 WebSocket 客户端封装将在后续迭代补齐。

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
