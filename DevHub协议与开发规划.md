# DevHub协议与开发规划

> 状态：Draft（可进入实现）  
> 目标：在 **本机 per-user** 场景下，为多个开发工具/插件/服务提供统一的：  
> - 实例注册（AppInstance）与发现  
> - 应用启动（auto-launch / dedupe）  
> - 方法调用编排（Invocation orchestration）  
> - 事件订阅与推送（Events）  
>
> v1 核心原则：**能稳定跑通闭环 > 过度设计**。  
> v1 明确不做：跨机器、强一致持久队列、分布式锁、复杂权限系统。

---

## 目录

1. [背景与目标](#1-背景与目标)  
2. [术语](#2-术语)  
3. [总体架构](#3-总体架构)  
4. [本机部署与发现](#4-本机部署与发现)  
5. [安全模型（Token + ClientId + Session）](#5-安全模型token--clientid--session)  
6. [传输与消息格式（JSON-RPC 2.0）](#6-传输与消息格式json-rpc-20)  
7. [数据模型](#7-数据模型)  
8. [方法定义（RPC Methods）](#8-方法定义rpc-methods)  
9. [路由规则（Invocation Routing）](#9-路由规则invocation-routing)  
10. [离线、排队与 autoLaunch 行为矩阵（v1 固化）](#10-离线排队与-autolaunch-行为矩阵v1-固化)  
11. [事件系统（subscribe/unsubscribe）](#11-事件系统subscribeunsubscribe)  
12. [错误码规范](#12-错误码规范)  
13. [实现建议（v1 必要工程细节）](#13-实现建议v1-必要工程细节)  
14. [开发里程碑（更新版）](#14-开发里程碑更新版)  
15. [v1 已知限制与 v2 展望](#15-v1-已知限制与-v2-展望)  
16. [附录：示例](#16-附录示例)

---

## 1. 背景与目标

### 1.1 背景
在一个开发者的本机环境中，常见形态包括：
- 游戏引擎（Unity / Unreal）
- 扩展工具（任务编辑器、配置编辑器）
- CLI 工具（git hooks、资源处理器、编译/打包助手）
- UI（WPF/Avalonia/Electron/Web）

它们需要相互调用、互相发现、互相启动，并能隔离不同 workspace/分支/工程上下文（scope）。

### 1.2 v1 目标（必须实现）
- **Hub 本机常驻**（per-user），提供统一 RPC 入口（HTTP + WebSocket）。
- **AppDefinition**（静态定义）与 **AppInstance**（运行时注册）的基本闭环。
- **Invocation** 调用编排：调用方发起 -> Hub 路由 -> 被调方 poll 拉取 -> 被调方回传结果 -> Hub 转发给调用方。
- **scope 隔离**：不同 scope 不互相 fallback（避免串 workspace）。
- **Events**：订阅与推送（用于 UI/监控/调试）。

### 1.3 v1 非目标（明确不做）
- 跨机器通信 / 多机集群
- 强一致持久化队列（Hub 重启后不保证保留 pending/invocation）
- 复杂授权（RBAC、多用户共享）
- exactly-once 投递保证（v1 为 at-least-once + 幂等建议）

---

## 2. 术语

| 名称          | 含义                                                                    |
| ------------- | ----------------------------------------------------------------------- |
| Hub           | DevHub 服务进程，本机路由中枢                                           |
| Client        | 任意调用方（IDE 插件、CLI、UI）                                         |
| Callee        | 被调用方应用实例（AppInstance）                                         |
| AppDefinition | 某类应用的静态定义（如何启动、支持什么能力等）                          |
| AppInstance   | 某个已运行进程/服务的运行时实例信息                                     |
| Invocation    | 一次调用（请求/通知），可排队、可等待结果                               |
| scope         | 工作空间隔离标识（例如 `p4ws://...`、`git://...`），global 表示无 scope |
| global scope  | “不带 scope / scope 为 null”的默认范围                                  |

---

## 3. 总体架构

```mermaid
flowchart LR
  subgraph ClientSide[调用侧]
    C1[IDE Plugin]
    C2[CLI]
    C3[UI]
  end

  subgraph HubSide[DevHub（本机 per-user）]
    H[Hub HTTP/WS JSON-RPC]
    REG[(App Registry<br/>in-memory + files)]
    INV[(Invocation Queue<br/>in-memory)]
  end

  subgraph AppSide[被调侧]
    A1[AppInstance A]
    A2[AppInstance B]
  end

  C1 -->|HTTP JSON-RPC| H
  C2 -->|HTTP JSON-RPC| H
  C3 -->|WS JSON-RPC| H

  H <--> REG
  H <--> INV

  A1 -->|poll (HTTP)| H
  A2 -->|poll (HTTP)| H
  A1 -->|respond (HTTP)| H
  A2 -->|respond (HTTP)| H

  H -->|hub.event (WS notify)| C3
```

说明：
- v1 的核心投递采用 **Callee 轮询（poll）**，减少 Hub push 的连接状态复杂度。
- WS 主要用于 UI/调试/监控等需要推送的事件流。

---

## 4. 本机部署与发现

### 4.1 per-user 安装与运行原则
- Hub **每个 OS 用户 1 个**（single instance）。
- 仅监听 `127.0.0.1` / `localhost`。
- 不支持跨用户访问（默认以文件 ACL + token 保证）。

### 4.2 数据目录（Windows 参考）
- Root：`%LOCALAPPDATA%\DevHub\`
  - `runtime\hub.json`：Hub 运行信息（端口、协议版本、启动时间等）
  - `runtime\token.txt`：访问 token（仅当前用户可读）
  - `apps\definitions\*.json`：AppDefinition 文件
  - `apps\instances\*.json`：AppInstance 注册镜像（便于调试/诊断；v1 以 in-memory 为主）
  - `logs\*.log`

> 注：目录结构可在 macOS/Linux 做等价映射（如 `~/.local/share/DevHub`），但 v1 优先 Windows。

### 4.3 Hub 发现（Discovery）
Client 通过读取 `runtime\hub.json` 获取：
- `httpBaseUrl`（例如 `http://127.0.0.1:47231`）
- `wsUrl`（例如 `ws://127.0.0.1:47231/ws`）
- `protocolVersion`

示例：

```json
{
  "protocolVersion": 1,
  "httpBaseUrl": "http://127.0.0.1:47231",
  "wsUrl": "ws://127.0.0.1:47231/ws",
  "startedAtUtc": "2026-01-28T10:00:00Z"
}
```

---

## 5. 安全模型（Token + ClientId + Session）

### 5.1 访问 token
- Hub 启动时生成随机 token 写入 `runtime\token.txt`
- token 仅用于本机、仅用于当前 OS 用户
- token 需要配合文件 ACL（Windows：仅当前用户可读）避免其他用户读取

### 5.2 Client 身份
- `clientId`：逻辑客户端身份（例如 `DevHubUI`、`VSPlugin`、`CLI`）
- `clientSessionId`：一次进程生命周期内的 session（用于区分重启/重连；建议 GUID）

### 5.3 HTTP 鉴权（保持 v1 简单一致）
- HTTP RPC 必须携带：
  - `Authorization: Bearer {token}`
  - `X-DevHub-Protocol: 1`
  - `X-DevHub-ClientId: ...`
  - `X-DevHub-ClientSessionId: ...`

### 5.4 WebSocket 鉴权 —— **v1 固化为“首个 JSON-RPC 方法鉴权/注册”（方案 B）**
> 变更原因：浏览器/Electron renderer 无法自定义 WS header。  
> v1 采用：**WS 连接允许先建立，但必须先调用 `hub.ws.authenticate`**。

规则：
1. 客户端建立 WS 连接：`ws://127.0.0.1:{port}/ws`
2. 客户端必须在连接建立后 **发送第一条 request：`hub.ws.authenticate`**
3. 在认证成功前：
   - 仅允许调用 `hub.ws.authenticate`
   - 调用其他方法：返回 JSON-RPC error `unauthorized`
4. 认证成功后，Hub 将该 WS connection 绑定到：
   - `clientId`
   - `clientSessionId`
   - protocolVersion
   - 授权状态
5. WS 断开时：自动清理该连接关联的订阅/临时状态。

> 注：若未来需要更严格握手阶段拒绝连接，可在 v2 扩展，但 v1 以“消息层认证”为准。

---

## 6. 传输与消息格式（JSON-RPC 2.0）

### 6.1 JSON-RPC 版本
- 使用 JSON-RPC 2.0：`"jsonrpc": "2.0"`

### 6.2 HTTP 传输
- Endpoint：`POST {httpBaseUrl}/rpc`
- Content-Type：`application/json`
- **HTTP 状态码始终返回 200**（包括鉴权失败、参数错误），错误通过 JSON-RPC error 表达。  
  > 本机内网 + SDK 场景下，这能简化调用侧一致性处理。  
  > 例外：Hub 自身崩溃或网络错误，由 HTTP 失败体现。
- 若 `X-DevHub-Protocol` 不为 `1`：返回 JSON-RPC error `not_supported`

### 6.3 WebSocket 传输
- Endpoint：`{wsUrl}`（例如 `ws://127.0.0.1:47231/ws`）
- 消息：每条 message 是一个 JSON-RPC request/response/notification（v1 不支持 batch）。

### 6.4 请求/响应基本形态

**Request**
```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "hub.apps.listDefinitions",
  "params": {}
}
```

**Response（成功）**
```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "result": { "ok": true }
}
```

**Response（失败）**
```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "error": {
    "code": -32001,
    "message": "unauthorized",
    "data": { "reason": "missing token" }
  }
}
```

**Notification**
```json
{
  "jsonrpc": "2.0",
  "method": "hub.event",
  "params": { "type": "app.instance.registered", "payload": {} }
}
```

---

## 7. 数据模型

### 7.1 scope 规则（v1 固化）
- `scope` 字段可为：
  - **缺省（omitted）**
  - **null**
  - **非空字符串（例如 `p4ws://host:1666/clientname`）**
- 缺省或 null 均代表 **global scope**
- 禁止使用字符串 `"global"` 作为 scope 值（避免歧义）
- 只要请求指定了 scope，就 **不允许 fallback 到 global**（严格隔离）

### 7.2 AppDefinition（静态定义）
建议字段（v1 需要的最小集合）：

```json
{
  "appId": "asset.indexer",
  "displayName": "Asset Indexer",
  "description": "Indexes assets for fast search",
  "scopePolicy": "any | globalOnly | required",
  "capabilities": {
    "rpc": true,
    "events": true
  },
  "launch": {
    "exePath": "C:\\Tools\\AssetIndexer\\AssetIndexer.exe",
    "argsTemplate": "--scope \"{scope}\" --hub \"{httpBaseUrl}\"",
    "workingDirectory": "C:\\Tools\\AssetIndexer",
    "dedupeKeyTemplate": "{appId}:{scope}"
  }
}
```

字段说明：
- `scopePolicy`
  - `any`：允许 global 或任意 scope
  - `globalOnly`：仅允许 global（scope 必须 omitted/null）
  - `required`：必须提供非空 scope

校验规则（v1 固化）：
- 若存在 AppDefinition：
  - `hub.apps.registerInstance` / `hub.apps.launch` / `hub.invoke.*` 必须满足 scopePolicy，否则返回 `forbidden`
- 若不存在 AppDefinition：
  - 允许 `registerInstance`
  - `hub.apps.launch` 返回 `app_definition_not_found`
  - `hub.invoke.*` 仅在有在线实例时可路由；不触发 autoLaunch
- `dedupeKeyTemplate`：用于多次 launch 的去重（避免重复启动）

### 7.3 AppInstance（运行时实例）
```json
{
  "instanceId": "asset.indexer:pid-12345:8a4c0f6d-...",
  "appId": "asset.indexer",
  "scope": "p4ws://host:1666/clientname",
  "pid": 12345,
  "registeredAtUtc": "2026-01-28T10:00:00Z",
  "lastSeenUtc": "2026-01-28T10:01:00Z",
  "endpoints": {
    "poll": true,
    "respond": true
  },
  "meta": {
    "machine": "MYPC",
    "user": "alice"
  }
}
```

约束建议：
- `instanceId` 建议限制为 `<= 256` 字符
- 建议字符集：`[a-zA-Z0-9._:-]`（避免日志/文件问题）

### 7.4 Invocation（调用）
核心字段（v1）：

```json
{
  "invocationId": "invk-20260128-000001",
  "appId": "asset.indexer",
  "target": {
    "scope": "p4ws://host:1666/clientname",
    "instanceId": null
  },
  "method": "index.rebuild",
  "params": { "full": true },
  "kind": "request | notify",
  "createdAtUtc": "2026-01-28T10:02:00Z",
  "delivery": {
    "leaseSeconds": 30,
    "attempt": 1
  },
  "caller": {
    "clientId": "VSPlugin",
    "clientSessionId": "b3d2..."
  }
}
```

补充：
- `delivery.leaseSeconds` 与 `delivery.attempt` 由 Hub 分配与更新（见 [13.6](#136-invocation-lease-与重投递v1-固化)）

---

## 8. 方法定义（RPC Methods）

> 约定：除特别说明外，所有方法都通过 HTTP `/rpc` 可用；WS 在认证后也可用。  
> result 建议统一返回 DTO（避免 SDK 特例）。  
> v1 不支持 batch。

### 8.1 `hub.ping`
- 用于连通性测试

**params**
```json
{}
```

**result**
```json
{ "ok": true, "serverTimeUtc": "..." }
```

建议：心跳间隔 10s（或与 `invoke.poll` 同步），在线判定阈值见 [13.3](#133-lastseen-更新时间规则v1-固化)。

---

### 8.2 WebSocket 认证

### 8.2.1 `hub.ws.authenticate`（WS-only，必须第一条）
**说明**：WS 连接建立后，客户端必须立即调用此方法完成认证与注册 client 信息。

**params**
```json
{
  "token": "string",
  "protocolVersion": 1,
  "clientId": "DevHubUI",
  "clientSessionId": "guid-like"
}
```

**result**
```json
{
  "ok": true,
  "protocolVersion": 1
}
```

失败返回：
- `unauthorized`：token 不合法
- `invalid_params`：缺字段
- `not_supported`：protocolVersion 不支持

---

### 8.3 AppDefinition 管理

### 8.3.1 `hub.apps.listDefinitions`
**params**
```json
{}
```

**result**
```json
{
  "definitions": [
    { "appId": "asset.indexer", "displayName": "Asset Indexer", "scopePolicy": "any" }
  ]
}
```

### 8.3.2 `hub.apps.getDefinition`
**params**
```json
{ "appId": "asset.indexer" }
```

**result**
```json
{ "definition": { "...": "..." } }
```

> 若不存在该 `appId`，返回 `app_definition_not_found`。

---

### 8.4 AppInstance 注册与存活

### 8.4.1 `hub.apps.registerInstance`
**说明**：被调方启动后必须注册实例；Hub 以 in-memory 为主，同时可落一份镜像文件便于诊断。

**params**
```json
{
  "instance": {
    "instanceId": "asset.indexer:pid-12345:...",
    "appId": "asset.indexer",
    "scope": null,
    "pid": 12345,
    "endpoints": { "poll": true, "respond": true },
    "meta": { }
  }
}
```

**result**
```json
{ "ok": true }
```

> `registerInstance` 会更新 `lastSeenUtc`（见 [13.3](#133-lastseen-更新时间规则v1-固化)）。

规则：
- 若 `instanceId` 已存在：视为更新（upsert）
- 若存在 AppDefinition 且 scopePolicy 不允许：返回 `forbidden`

### 8.4.2 `hub.apps.heartbeat`
**params**
```json
{ "instanceId": "asset.indexer:pid-12345:..." }
```

**result**
```json
{ "ok": true, "serverTimeUtc": "..." }
```

### 8.4.3 `hub.apps.unregisterInstance`
**params**
```json
{ "instanceId": "asset.indexer:pid-12345:..." }
```

**result**
```json
{ "ok": true }
```

### 8.4.4 `hub.apps.listInstances`
**params**
```json
{
  "appId": "asset.indexer",
  "scope": null,
  "includeAllScopes": false
}
```

**result**
```json
{
  "instances": [
    {
      "instanceId": "asset.indexer:pid-12345:...",
      "appId": "asset.indexer",
      "scope": null,
      "pid": 12345,
      "lastSeenUtc": "..."
    }
  ]
}
```

> `includeAllScopes=true` 时忽略 `scope`，返回所有 scope（主要用于 UI/诊断）。  
> 默认仅返回在线实例（在线判定见 [13.3](#133-lastseen-更新时间规则v1-固化)）。

> lastSeen 更新规则见 [13.3](#133-lastseen-更新时间规则v1-固化)。

---

### 8.5 启动（Launch）

### 8.5.1 `hub.apps.launch`
**说明**：Hub 根据 AppDefinition 启动应用；对相同 dedupeKey 的并发请求进行去重/合并。

规则补充（v1 固化）：
- `dedupeKey` 可省略；省略时 Hub 以 `AppDefinition.launch.dedupeKeyTemplate` 生成
- `dedupeKeyTemplate` 支持占位符：`{appId}`、`{scope}`、`{scopeOrGlobal}`
- `dedupeKeyTemplate` 缺省时，默认等价于 `{appId}:{scopeOrGlobal}`
- 若 `appId` 不存在定义：返回 `app_definition_not_found`
- `waitForRegisterMs` 为 `0` 表示不等待注册，直接返回 `starting`

**params**
```json
{
  "appId": "asset.indexer",
  "scope": "p4ws://host:1666/clientname",
  "dedupeKey": "asset.indexer:p4ws://host:1666/clientname",
  "waitForRegisterMs": 3000
}
```

**result**
```json
{
  "status": "started | starting | already_running",
  "pid": 12345,
  "launchId": "launch-20260128-0001"
}
```

语义：
- `starting`：已触发启动，但在 `waitForRegisterMs` 内未观察到 registerInstance（可能正在启动）
- `started`：启动并在等待窗口内观察到实例注册或进程已可确认
- `already_running`：基于 dedupeKey 判定已有启动/运行流程（join）  
  - `pid` 可能为空（例如无法立刻确认 pid）；SDK 需兼容 `pid` 缺省

---

### 8.6 Invocation（调用编排）

### 8.6.1 `hub.invoke.notify`
**说明**：通知型调用，不等待结果。

**params**
```json
{
  "appId": "asset.indexer",
  "target": { "scope": null, "instanceId": null },
  "method": "index.warmup",
  "params": { "level": "fast" },
  "options": {
    "autoLaunch": true,
    "queueIfOffline": true,
    "ttlMs": 60000
  }
}
```

**result**
```json
{ "accepted": true, "invocationId": "..." }
```

### 8.6.2 `hub.invoke.request`
**说明**：请求型调用，等待被调方回传结果。

**params**
```json
{
  "appId": "asset.indexer",
  "target": { "scope": "p4ws://host:1666/clientname", "instanceId": null },
  "method": "index.rebuild",
  "params": { "full": true },
  "options": {
    "autoLaunch": true,
    "queueIfOffline": true,
    "ttlMs": 300000,
    "waitTimeoutMs": 120000
  }
}
```

**result**
```json
{
  "invocationId": "...",
  "value": { "ok": true }
}
```

> 注意：如果超时/被取消，返回相应 error code（见 [12](#12-错误码规范)）。
> 规则：`waitTimeoutMs` 仅对 request 生效，且必须 `<= ttlMs`。

### 8.6.3 `hub.invoke.poll`（Callee 拉取调用）
**说明**：被调方长轮询拉取待处理 invocation。  
Hub 会按路由规则选择适合该 instance 的待处理调用。

**params**
```json
{
  "instanceId": "asset.indexer:pid-12345:...",
  "maxCount": 1,
  "waitMs": 25000
}
```

**result**
```json
{
  "items": [
    {
      "invocationId": "...",
      "kind": "request",
      "appId": "asset.indexer",
      "method": "index.rebuild",
      "params": { "full": true },
      "caller": { "clientId": "VSPlugin", "clientSessionId": "..." },
      "target": { "scope": "p4ws://...", "instanceId": null },
      "delivery": { "leaseSeconds": 30, "attempt": 1 }
    }
  ],
  "serverTimeUtc": "..."
}
```

> 返回 items 即视为 lease 开始，规则见 [13.6](#136-invocation-lease-与重投递v1-固化)。

### 8.6.4 `hub.invoke.respond`（Callee 回传结果/错误）
**params（成功）**
```json
{
  "instanceId": "asset.indexer:pid-12345:...",
  "invocationId": "...",
  "result": { "ok": true }
}
```

**params（失败）**
```json
{
  "instanceId": "asset.indexer:pid-12345:...",
  "invocationId": "...",
  "error": { "code": -32050, "message": "index failed", "data": { } }
}
```

**result**
```json
{ "ok": true }
```

---

## 9. 路由规则（Invocation Routing）

当 Hub 收到 `invoke.notify`/`invoke.request` 时，需要把 invocation 绑定到一个目标实例或进入 pending。

### 9.1 scope 匹配（强规则）
- invocation 指定了 `target.scope = S`：
  - 只能路由到 `AppInstance.scope == S`
  - **不允许 fallback 到 global**
- invocation 未指定 scope（omitted/null）：
  - 只路由到 `AppInstance.scope == null`（global）

### 9.2 instanceId 精确路由（可选）
- 若 `target.instanceId` 非空：只路由到该 instanceId（且 scope 必须一致）
- 若该 instance 不在线：按离线矩阵处理（见 [10](#10-离线排队与-autolaunch-行为矩阵v1-固化)）

### 9.3 选择策略（多实例时）
当存在多个在线实例符合条件时：
- v1 采用：选择 `lastSeenUtc` 最新的实例（最简单、可解释）
- 未来可扩展：round-robin、负载、capabilities 匹配等

---

## 10. 离线、排队与 autoLaunch 行为矩阵（v1 固化）

Invocation options：

```json
{
  "autoLaunch": true,
  "queueIfOffline": true,
  "ttlMs": 60000,
  "waitTimeoutMs": 120000
}
```

- `ttlMs`：invocation 在 Hub 内的存活时间（过期则丢弃/失败）
- `waitTimeoutMs`：仅对 `invoke.request` 生效，调用方最多等待多久（超时则返回 timeout）

v1 默认值（可配置但需保持一致）：
- `invoke.notify`：`ttlMs = 60000`
- `invoke.request`：`ttlMs = 300000`，`waitTimeoutMs = 120000`
- `autoLaunch = true`，`queueIfOffline = true`
- 若 `waitTimeoutMs > ttlMs`：返回 `invalid_params`
> `options` 可省略或部分省略；省略项使用默认值。
> 对于 `invoke.request`，`waitTimeoutMs` 到期即终止 invocation（见 [13.6](#136-invocation-lease-与重投递v1-固化)）；因此 request 的实际存活时间为 `min(ttlMs, waitTimeoutMs)`。

校验优先级（v1 固化）：
- 若存在 AppDefinition 且 scopePolicy 不允许：直接返回 `forbidden`（不进入矩阵）
- 若不存在 AppDefinition：不触发 autoLaunch；仅当有在线实例时才可路由

### 10.1 行为矩阵（必须按此实现）
> “在线实例”判定见 [13.3](#133-lastseen-更新时间规则v1-固化)。

| 场景                             | queueIfOffline | autoLaunch | 行为（v1 固化）                                                                   |
| -------------------------------- | -------------: | ---------: | --------------------------------------------------------------------------------- |
| 有在线实例可路由                 |           任意 |       任意 | 立即进入队列，等待 callee poll 获取                                               |
| 无在线实例，但存在 AppDefinition |           true |       true | 进入 pending；触发 `hub.apps.launch`（内部）；等待实例注册后投递（直到 ttl 到期） |
| 无在线实例，但存在 AppDefinition |           true |      false | 进入 pending；不自动启动；等待未来某实例注册（直到 ttl 到期）                     |
| 无在线实例且无 AppDefinition     |           true | true/false | 返回 `instance_not_found`（error），并在 data 标明原因                            |
| 无在线实例（任意 AppDefinition） |          false |       任意 | 直接返回 `instance_not_found`（error），不入队，不启动                            |

> 解释：`queueIfOffline=false` 表示“必须在线才发起调用”；`autoLaunch=true` 仅在允许排队且存在 definition 的前提下才有意义。

### 10.2 pending 到 active 的转换
当满足以下条件时，pending invocation 变为可投递：
- 某个 instance 注册后与其匹配（appId + scope + instanceId 条件）
- 且 invocation 未过期（`now - createdAtUtc < ttlMs`）
- 对于 request：还需 `now - createdAtUtc < waitTimeoutMs`；否则视为 timeout 并终止

---

## 11. 事件系统（subscribe/unsubscribe）

### 11.1 目标
- 让 UI/调试工具可以实时看到：
  - instance 注册/心跳/下线
  - invocation 入队/投递/完成/失败
  - hub 自身状态

### 11.2 `hub.events.subscribe`
**params**
```json
{
  "types": [
    "app.instance.registered",
    "app.instance.unregistered",
    "invocation.queued",
    "invocation.delivered",
    "invocation.completed",
    "invocation.failed"
  ]
}
```

**result**
```json
{ "subscriptionId": "sub-20260128-0001" }
```

### 11.3 `hub.events.unsubscribe`
**params**
```json
{ "subscriptionId": "sub-20260128-0001" }
```

**result**
```json
{ "ok": true }
```

### 11.4 `hub.event`（Server -> Client notification，WS）
**params**
```json
{
  "subscriptionId": "sub-20260128-0001",
  "type": "invocation.completed",
  "timeUtc": "2026-01-28T10:03:00Z",
  "payload": {
    "invocationId": "...",
    "appId": "asset.indexer",
    "instanceId": "asset.indexer:pid-12345:..."
  }
}
```

### 11.5 生命周期规则（v1 固化）
- subscription **绑定 WS connection**
- WS 断开：Hub **自动清理**该连接所有 subscriptions
- 允许重复 subscribe（返回新的 subscriptionId）；由客户端自行管理 unsubscribe
- v1 不提供“事件重放”，断线期间事件丢失（UI 需在重连后主动 query/list 补齐）

---

## 12. 错误码规范

### 12.1 JSON-RPC 标准错误
- `-32600` invalid_request
- `-32601` method_not_found
- `-32602` invalid_params
- `-32603` internal_error

### 12.2 DevHub 自定义错误（建议）
|   code | message                  | 说明                                                         |
| -----: | ------------------------ | ------------------------------------------------------------ |
| -32001 | unauthorized             | token 不合法 / WS 未 authenticate                            |
| -32002 | forbidden                | scopePolicy 不允许 / 访问被拒绝                              |
| -32010 | instance_not_found       | 未找到可路由实例（且按矩阵不排队）                           |
| -32011 | invocation_expired       | pending/invocation 过期                                      |
| -32012 | invocation_timeout       | invoke.request 等待超时（waitTimeoutMs）                     |
| -32013 | instance_offline         | 指定 instanceId 但不在线（可与 not_found 合并，v1 可二选一） |
| -32014 | app_definition_not_found | 未找到 AppDefinition                                         |
| -32020 | launch_failed            | 启动失败                                                     |
| -32030 | delivery_conflict        | invocation 被重复 respond / lease 冲突等                     |
| -32040 | rate_limited             | 触发限流/资源上限                                            |
| -32099 | not_supported            | 当前协议/版本不支持                                          |

> v1 允许把 `instance_offline` 合并为 `instance_not_found`，但建议区分以便排障。

---

## 13. 实现建议（v1 必要工程细节）

> 本节是“必须考虑的工程落点”，不属于过度设计。

### 13.1 并发与取消
- `invoke.request` 内部等待建议用 `TaskCompletionSource` + `CancellationToken`
- 当 HTTP 请求被客户端取消（连接断开）：
  - 必须取消等待并清理 waiter（避免内存泄露）
- 建议限制：
  - `maxWaitingRequestsPerClient`（例如 200）
  - `maxPendingInvocationsTotal`（例如 5000）
  - 超限返回 `internal_error` 或专用 `rate_limited`（可选）

### 13.2 poll 的长轮询实现
- `hub.invoke.poll.waitMs` 在服务端用异步等待（不要阻塞线程）
- wait 到期返回空数组
- poll 也是心跳的一部分（见 lastSeen）

### 13.3 lastSeen 更新时间规则（v1 固化）
Hub 在以下任一事件发生时更新 `AppInstance.lastSeenUtc`：
- `registerInstance`
- `heartbeat`
- `invoke.poll`（只要 poll 成功到达 Hub，就算空列表也更新）
- `invoke.respond`

在线判定（v1 固化）：
- 若 `now - lastSeenUtc <= 30s`：视为在线
- 超过 30s 视为离线，不参与路由与 autoLaunch 选择

### 13.4 文件写入原子性
- `runtime\hub.json`、`instances\*.json` 等建议使用：
  - 写临时文件 + 原子 rename/replace
- 避免半写入导致 discovery/诊断读取失败

### 13.5 单实例（single instance）
- Windows 建议使用 Mutex：`Global\DevHub_{UserSid}` 或 `Local\DevHub_{UserSid}`
- 若已有实例运行：
  - 新进程退出或转为“客户端模式”提示如何连接（可选）

### 13.6 Invocation lease 与重投递（v1 固化）
- Hub 在 `invoke.poll` 返回时为每条 invocation 生成 lease（默认 `leaseSeconds = 30`）
- lease 期间仅分配给该 instance；到期仍未 `respond` 且未过期时，放回队列并 `attempt++`
- `invoke.request` 达到 `waitTimeoutMs`：Hub 返回 `invocation_timeout` 并终止该 invocation
- 若 invocation 已完成：后续 `respond` 返回 `delivery_conflict`
- 若 invocation 已超时：`respond` 返回 `invocation_timeout`
- 若 invocation 已过期：`respond` 返回 `invocation_expired`

### 13.7 launch 去重生命周期（v1 固化）
- Hub 对 `dedupeKey` 维护 launching 记录，默认 `launchDedupeWindowMs = 30000`
- launching 期间重复 `launch`：返回 `already_running`（复用同一 `launchId`）
- 若在窗口内观察到匹配实例注册：状态转为 running，后续仍返回 `already_running`
- 若已有在线实例（见 [13.3](#133-lastseen-更新时间规则v1-固化)）匹配 `appId + scope`：直接返回 `already_running`
- 窗口到期仍未注册：清理 dedupe 记录，允许再次启动

---

## 14. 开发里程碑（更新版）

> 根据本次修订（WS 采用消息层 authenticate、补齐离线矩阵、补齐 unsubscribe 等），对里程碑做“轻微调序”，避免 M1 被 WS 细节拖累。

### 14.1 里程碑概览

| Milestone | 目标                               | 交付物                                                                                        | 备注               |
| --------- | ---------------------------------- | --------------------------------------------------------------------------------------------- | ------------------ |
| M0        | 文档冻结 + Spec v0                 | DevHub.md + 接入规范（Spec v0，协议/DTO/错误码/时序）+ JSON schemas（draft）                  | 本文档即为 M0 产物 |
| M1        | Hub（HTTP）基础能力 + Spec v1 冻结 | `/rpc`、token、client headers、apps definitions/instances、TTL/lastSeen + Spec v1（协议定稿） | WS 可先不做        |
| M2        | Invocation 闭环（HTTP）            | invoke.notify/request/poll/respond、离线矩阵、autoLaunch、launch dedupe                       | v1 核心            |
| M3        | scopePolicy 与严格隔离             | any/globalOnly/required + 错误码 + 测试用例                                                   |                    |
| M4        | WebSocket（认证 + events）         | `/ws`、hub.ws.authenticate、subscribe/unsubscribe、hub.event 推送                             | UI/监控可接入      |
| M5        | SDK（.NET + JS/TS）                | .NET SDK、JS/TS SDK、签名测试向量、契约测试、Spec 定稿（必要变更）                            | Spec 已前置        |
| M6        | 治理与诊断增强（可选）             | 指标、日志、dump、限流配置                                                                    | 不阻塞 v1          |

> 若团队人力足够，M4 可与 M2 并行；否则按上表顺序更稳。

### 14.2 里程碑验收清单（最小可验收）
**M0**
- DevHub.md 冻结；AppDefinition/AppInstance/Invocation 的 JSON Schema（draft）可校验示例
- 接入规范（Spec v0）完成：协议/DTO/错误码/时序/状态机/边界条件

**M1**
- 启动 Hub 生成 `runtime/hub.json` 与 `runtime/token.txt`
- `hub.ping` 返回 ok；缺 token 返回 `unauthorized`
- `hub.apps.listDefinitions` 能读取 `apps/definitions/*.json`
- `hub.apps.registerInstance` 后可 `listInstances` 看到；`lastSeen` 更新
- Spec v1 冻结：与 M1 实现行为一致（鉴权/错误码/时序）

**M2**
- `invoke.request`：caller -> hub -> callee poll -> respond -> caller 收到结果
- `invoke.notify`：可入队并被 poll 取走（无需 caller 等待）
- `queueIfOffline + autoLaunch` 能触发 `hub.apps.launch`，注册后投递
- lease 到期可重投递（`attempt++`）；TTL 到期返回 `invocation_expired`

**M3**
- `scopePolicy` 三种规则对 register/launch/invoke 生效
- 任意 scope 不允许 fallback 到 global

**M4**
- WS 必须先 `hub.ws.authenticate`；未认证调用返回 `unauthorized`
- `subscribe/unsubscribe` 可用；断线自动清理订阅
- 事件覆盖注册、投递、完成/失败

**M5**
- 基于已冻结 Spec 完成 .NET SDK 与 JS/TS SDK：发现（hub.json）、认证、invoke、poll、events
- 错误码、超时/重试与 scopePolicy 行为与 Spec 保持一致
- 提供签名测试向量（API 方法/DTO 结构化用例，含请求/响应/错误预期）
- 通过 Hub↔SDK 契约测试（正向/异常/兼容性场景）
- 如需变更，需同步更新 Spec 与测试向量

---

### 14.3 SDK 设计与开发规划
**定位与原则**
- SDK 面向外部工具接入 DevHub，封装通用通信与鉴权
- Spec-first：在 M0/M1 产出并冻结语言无关 Spec，Hub 与各语言 SDK 实现与验收以 Spec 为唯一依据
- SDK 仅提供通用能力与稳定 API 面，业务逻辑由上层应用承担

**SDK 设计方式**
- 分层模块：Discovery（hub.json）、Auth（token/headers）、Transport（HTTP/WS JSON-RPC）
- 调用模块：Invocation（request/notify/poll/respond）、Events（subscribe/unsubscribe）
- 基础模块：ErrorMapping、Retry/Timeout、日志与可观测性扩展点

**开发规划（M5）**
- 基于已冻结 Spec 实现 SDK；若需变更，先更新 Spec 与测试向量
- .NET SDK 以 NuGet 交付；JS/TS SDK 以 NPM 交付并提供类型定义
- Hub 与 SDK 的发布以 Spec 为主线同步，变更需更新 Spec 与测试向量

**测试与验收**
- 签名测试向量：以 API 方法/DTO 结构化用例表达请求/响应/错误预期
- 契约测试：Hub 与 SDK 双向互测，覆盖鉴权、离线矩阵、错误码一致性与事件订阅

---

## 15. v1 已知限制与 v2 展望

### 15.1 v1 限制
- Hub 重启会丢失：
  - pending invocations
  - request 等待中的 TCS 状态
- 投递语义为 **at-least-once**：
  - 若 callee 取到 invocation 后崩溃，可能被重新投递（取决于 lease/实现）
- Events 不支持重放：断线期间事件丢失

### 15.2 v2 可能增强
- pending/invocation 持久化（轻量本地 KV）
- delivery ack + 更明确 lease 机制
- events replay（按 subscription 游标）
- 更细粒度的权限模型（若出现多用户共享需求）

---

## 16. 附录：示例

### 16.1 HTTP 调用示例（curl）
```bash
curl -X POST "http://127.0.0.1:47231/rpc" ^
  -H "Content-Type: application/json" ^
  -H "Authorization: Bearer <token>" ^
  -H "X-DevHub-Protocol: 1" ^
  -H "X-DevHub-ClientId: CLI" ^
  -H "X-DevHub-ClientSessionId: 2f7d..." ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"hub.apps.listDefinitions\",\"params\":{}}"
```

### 16.2 WS 认证 + 订阅示例（伪代码）
```js
const ws = new WebSocket("ws://127.0.0.1:47231/ws");

ws.onopen = () => {
  // 必须第一条：authenticate
  ws.send(JSON.stringify({
    jsonrpc: "2.0",
    id: "1",
    method: "hub.ws.authenticate",
    params: {
      token: "<token>",
      protocolVersion: 1,
      clientId: "DevHubUI",
      clientSessionId: "a1b2c3..."
    }
  }));
};

ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);

  // authenticate ok 后再 subscribe
  if (msg.id === "1" && msg.result?.ok) {
    ws.send(JSON.stringify({
      jsonrpc: "2.0",
      id: "2",
      method: "hub.events.subscribe",
      params: { types: ["invocation.completed", "app.instance.registered"] }
    }));
  }

  // event push
  if (msg.method === "hub.event") {
    console.log("event:", msg.params);
  }
};
```
