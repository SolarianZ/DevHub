# DevHub M1细化任务文档

---

## 0. 目标与验收对齐（必须满足）

### 0.1 M1 必须实现（对齐里程碑验收清单）
- 启动 Hub 生成：
  - `runtime/hub.json`
  - `runtime/token.txt`
- HTTP JSON-RPC：
  - `POST /rpc`
  - header 鉴权：`Authorization: Bearer <token>`
  - 协议版本：`X-DevHub-Protocol: 1`
  - 客户端信息：`X-DevHub-ClientId`、`X-DevHub-ClientSessionId`
- RPC 方法（M1 用到的最小集合）：
  - `hub.ping`
  - `hub.apps.listDefinitions`（读取 `apps/definitions/*.json`）
  - `hub.apps.getDefinition`
  - `hub.apps.registerInstance`
  - `hub.apps.heartbeat`
  - `hub.apps.unregisterInstance`
  - `hub.apps.listInstances`（在线过滤 + lastSeen 更新验证）
- `lastSeenUtc` 更新：至少覆盖 **registerInstance / heartbeat**（协议 v1 还要求 poll/respond 更新，但属于 M2；M1 先把 Registry 的“更新时间 API”设计好即可）

### 0.2 输出规范（协议强约束）
- **HTTP 状态码始终返回 200**（包括鉴权失败、参数错误等），错误通过 JSON-RPC error 返回。
- JSON-RPC 2.0：
  - `jsonrpc: "2.0"`
  - v1 **不支持 batch**（收到 batch 可返回 `invalid_request` 或 `not_supported`，建议按 `invalid_request`）。

---

## 1. 技术选型与工程结构（不影响协议）

> 1人开发优先“可维护 + 少坑”。

### 1.1 语言/框架建议
- **C# + ASP.NET Core（Minimal API）**
- JSON：`System.Text.Json`

### 1.2 项目结构建议（最小可维护）
- `DevHub.Host`（ASP.NET Core Host）
- `DevHub.Core`（模型 + Registry + DefinitionLoader + RpcRouter）
- `DevHub.Tests`（集成测试脚本或简单测试）

> 不强制拆分为多个项目；如果赶工，可先单项目，目录分层即可。

---

## 2. 文件系统与发现（Discovery）（M1 必做）

### 2.1 数据目录（Windows）
- Root：`%LOCALAPPDATA%\DevHub\`
  - `runtime\hub.json`
  - `runtime\token.txt`
  - `apps\definitions\*.json`
  - `apps\instances\*.json`（**M1 可选**：镜像便于诊断）
  - `logs\*.log`（建议）

### 2.2 启动时初始化目录
- 确保上述目录存在（`Directory.CreateDirectory`）。

### 2.3 Token 生成与写入（必须）
- 若 `runtime\token.txt` 不存在：生成随机 token 并写入。
- 若已存在：读取复用（**建议**：方便 Hub 重启后客户端无需重新发现 token；协议未禁止）。
- token 建议：
  - 32 bytes random -> Base64/Hex
- **ACL（可选）**：
  - Windows 下设置 `token.txt` 仅当前用户可读（M1 若时间不足可先不做，但要在文档/代码里标注 TODO）。

### 2.4 写入 `runtime/hub.json`（必须）
- 内容字段对齐协议：
  ```json
  {
    "protocolVersion": 1,
    "httpBaseUrl": "http://127.0.0.1:{port}",
    "wsUrl": "ws://127.0.0.1:{port}/ws",
    "startedAtUtc": "2026-01-28T10:00:00Z"
  }
  ```
- **注意**：M1 不实现 WS，但 `wsUrl` 字段允许提前写，便于 UI/SDK 统一 discovery。
- **原子写**（协议 13.4）：
  - 写 `hub.json.tmp` -> `File.Move(tmp, hub.json, overwrite:true)` 或 replace 逻辑。

### 2.5 监听地址与端口
- 仅监听 `127.0.0.1` / `localhost`
- 端口策略（二选一）：
  1. 固定端口（简单，但可能冲突）
  2. **动态端口**（推荐）：绑定端口 0，让 OS 分配，再写入 `hub.json`

---

## 3. 单实例（Single Instance）（建议 M1 就做）

### 3.1 Mutex 互斥
- Windows：
  - `Mutex` 名称建议：`Local\DevHub_{UserSid}`（或 `Global\...`）
- 若已有实例：
  - 新进程直接退出（M1 不需要“转客户端模式”）

---

## 4. HTTP `/rpc`：JSON-RPC 2.0 接入层（M1 核心）

### 4.1 `/rpc` Endpoint（必须）
- 路由：`POST /rpc`
- Content-Type：`application/json`（不强制，但建议校验，不符合可返回 JSON-RPC `invalid_request`）

### 4.2 处理顺序（重要：保证 error 能带回同一个 id）
为尽量符合 SDK 调用体验，建议在 `/rpc` handler 中按以下顺序：

1. 读取 body，并解析为 `JsonRpcRequest`
   - 若解析失败 / 非法结构：返回 `-32600 invalid_request`，`id = null`
2. 校验协议与鉴权 headers（即使失败也返回 HTTP 200）
   - `X-DevHub-Protocol != 1` -> `-32099 not_supported`
   - `Authorization` 不合法 -> `-32001 unauthorized`
   - 缺少 `X-DevHub-ClientId` 或 `X-DevHub-ClientSessionId` -> `-32602 invalid_params`
3. 调用路由器 `RpcRouter.Dispatch(method, params, context)`
4. 任何未捕获异常 -> `-32603 internal_error`

> 这样可以在鉴权失败时仍返回与请求一致的 `id`（如果 body 可解析）。

### 4.3 Header 规则（协议 5.3）
- 必须携带：
  - `Authorization: Bearer {token}`
  - `X-DevHub-Protocol: 1`
  - `X-DevHub-ClientId: ...`
  - `X-DevHub-ClientSessionId: ...`
- 建议把解析后的 client 信息放入 `RequestContext`（供后续 M2/M4 用）。

### 4.4 JSON-RPC 不支持 batch（协议 6.3）
- 若 body 是数组：直接返回 `-32600 invalid_request`（M1 固化，简单可解释）

### 4.5 RPC：`hub.ping`（协议 8.1）
- params：`{}`
- result：
  ```json
  { "ok": true, "serverTimeUtc": "..." }
  ```

---

## 5. 数据模型与校验（M1 覆盖范围）

### 5.1 scope 规则（协议 7.1，M1 可做轻校验）
- `scope`：
  - omitted 或 null => global scope
  - 非空 string => 具体 scope
- **建议在 registerInstance / listInstances 参数解析时做最小校验**：
  - 若 scope 字符串等于 `"global"`：返回 `-32602 invalid_params`（协议明确禁止）

> `scopePolicy` 严格校验属于 M3；**M1 不强行实现**，避免越界与返工。

### 5.2 在线判定（协议 13.3）
- 若 `now - lastSeenUtc <= 30s`：在线
- `hub.apps.listInstances` 默认只返回在线实例

### 5.3 lastSeen 更新时间（M1 必须覆盖的触发点）
- M1 必做：
  - `hub.apps.registerInstance`
  - `hub.apps.heartbeat`
- 预留（M2 才会用）：
  - `hub.invoke.poll`
  - `hub.invoke.respond`

---

## 6. AppDefinition（静态定义）管理（M1 必做）

### 6.1 定义加载（读取 `apps/definitions/*.json`）
- 启动加载一次（M1 不要求热更新）
- 读取失败处理策略：
  - 单个文件解析失败：记录日志并跳过（不要让 Hub 整体启动失败，除非你希望严格）
- 最小字段要求：
  - `appId` 必须存在且非空

### 6.2 RPC：`hub.apps.listDefinitions`
- params：`{}`
- result：
  ```json
  { "definitions": [ { "appId": "...", "displayName": "...", "scopePolicy": "any" } ] }
  ```
- 若某些字段缺失（例如 `displayName`）：可返回 null 或空字符串，但建议保持 DTO 稳定。

### 6.3 RPC：`hub.apps.getDefinition`
- params：
  ```json
  { "appId": "asset.indexer" }
  ```
- 若不存在：返回 `-32014 app_definition_not_found`

---

## 7. AppInstance（运行时实例）注册与查询（M1 必做）

### 7.1 内存 Registry（线程安全）
- 建议结构：
  - `ConcurrentDictionary<string, AppInstanceRecord>`
- `AppInstanceRecord` 内含：
  - instanceId, appId, scope, pid, registeredAtUtc, lastSeenUtc, endpoints, meta
- Upsert 行为（协议 8.4.1）：
  - 若 instanceId 已存在：视为更新（upsert）
  - 每次 register 更新 `lastSeenUtc`

### 7.2 RPC：`hub.apps.registerInstance`
- params（对齐协议形态）：
  ```json
  { "instance": { ... } }
  ```
- 行为：
  - 校验 `instance.instanceId/appId` 非空
  - `registeredAtUtc` 可由 Hub 赋值（若客户端未填）
  - `lastSeenUtc` 由 Hub 设置为 now
  - 返回 `{ "ok": true }`
- **注意**：协议允许 “不存在 AppDefinition 也允许 registerInstance”，M1 必须支持。

- （可选）镜像文件：
  - 写入 `apps/instances/{instanceId}.json`
  - 原子写入（tmp + replace）

### 7.3 RPC：`hub.apps.heartbeat`
- params：
  ```json
  { "instanceId": "..." }
  ```
- result（对齐协议）：
  ```json
  { "ok": true, "serverTimeUtc": "..." }
  ```
- 若 instanceId 不存在：
  - 返回 `-32010 instance_not_found`

### 7.4 RPC：`hub.apps.unregisterInstance`
- params：
  ```json
  { "instanceId": "..." }
  ```
- result：
  ```json
  { "ok": true }
  ```
- 建议行为（更好用）：**幂等**
  - 不存在也返回 ok（减少客户端状态分歧）
- （可选）删除镜像文件。

### 7.5 RPC：`hub.apps.listInstances`（对齐协议字段）
- params（对齐协议 8.4.4）：
  ```json
  {
    "appId": "asset.indexer",
    "scope": null,
    "includeAllScopes": false
  }
  ```
- 行为：
  - 若 `includeAllScopes=true`：
    - 忽略 `scope`，返回所有 scope（仍默认只返回在线）
  - 否则：
    - `scope` omitted/null => 只返回 `instance.scope == null` 的实例
    - `scope` 为字符串 => 只返回 `instance.scope == scope` 的实例
  - 永远按 `appId` 过滤（如果传了 appId）
  - 默认只返回在线实例（30s 内 lastSeen）
- result：
  ```json
  {
    "instances": [
      { "instanceId": "...", "appId": "...", "scope": null, "pid": 123, "lastSeenUtc": "..." }
    ]
  }
  ```

---

## 8. 错误码与返回策略（M1 覆盖）

### 8.1 标准错误码（协议 12.1）
- `-32600 invalid_request`
- `-32601 method_not_found`
- `-32602 invalid_params`
- `-32603 internal_error`

### 8.2 DevHub 自定义错误码（M1 至少用到）
- `-32001 unauthorized`
- `-32014 app_definition_not_found`
- `-32099 not_supported`
- `-32010 instance_not_found`

### 8.3 HTTP 状态码规则（必须）
- 永远返回 200
- 网络错误 / Hub 崩溃由连接失败体现

---

## 9. 后台清理与日志（M1 建议项，不阻塞验收）

### 9.1 实例过期清理（可选）
- 目的：防止内存无限增长
- 策略建议：
  - 每 60s 扫描一次
  - `now - lastSeenUtc > 1h` 的实例从内存移除

> 注意：**不要**用 30s 作为物理删除阈值。30s 是“在线判定”，不是“立即清理”。

### 9.2 日志（建议）
- 每个 RPC 请求记录：
  - method、clientId、clientSessionId、耗时、成功/错误码
- 记录注册/心跳/注销事件（后续 M4 event 可复用这些日志点）

---

## 10. 开发排期（1 人可执行）

> 以“按功能闭环、随时可运行”为原则。

### Day 0.5：工程脚手架 + 单实例 + 文件系统
- [ ] 创建项目（Minimal API）
- [ ] Mutex 单实例
- [ ] 初始化目录结构
- [ ] token 生成/读取（写 `token.txt`）
- [ ] 启动监听 localhost，拿到端口
- [ ] 原子写 `hub.json`

### Day 1：`/rpc` JSON-RPC + 鉴权 + `hub.ping`
- [ ] 实现 `POST /rpc`：解析 JSON-RPC request
- [ ] 校验 headers（token/protocol/clientId/sessionId）
- [ ] 实现 `hub.ping`
- [ ] 统一错误返回（200 + JSON-RPC error）

### Day 1.5：AppDefinition（list/get）
- [ ] 启动加载 definitions 目录
- [ ] `hub.apps.listDefinitions`
- [ ] `hub.apps.getDefinition` + `app_definition_not_found`

### Day 2：AppInstance Registry（register/heartbeat/unregister/list）
- [ ] 内存 registry + 线程安全
- [ ] registerInstance（upsert + lastSeen）
- [ ] heartbeat（lastSeen + serverTimeUtc）
- [ ] unregister（幂等）
- [ ] listInstances（scope/includeAllScopes + 在线过滤）

### Day 3：测试脚本 + 稳定性修补
- [ ] 集成测试脚本（curl/.http/python均可）
- [ ] 覆盖负面用例（缺 token、协议版本不对、method 不存在、params 缺失）
- [ ] 日志完善与小 bug 修复

---

## 11. M1 验收用例（最小可验收）

### 11.1 启动与发现
- [ ] 启动后生成：
  - `%LOCALAPPDATA%\DevHub\runtime\token.txt`
  - `%LOCALAPPDATA%\DevHub\runtime\hub.json`
- [ ] `hub.json` 中 `httpBaseUrl` 可访问

### 11.2 鉴权与协议版本
- [ ] 调用 `hub.ping`：
  - 带正确 token -> ok + `serverTimeUtc`
  - 缺 token 或错误 token -> `unauthorized (-32001)`
- [ ] `X-DevHub-Protocol != 1` -> `not_supported (-32099)`
- [ ] 缺少 `X-DevHub-ClientId` 或 `X-DevHub-ClientSessionId` -> `invalid_params (-32602)`

### 11.3 AppDefinition
- [ ] `apps/definitions/*.json` 中放入至少一个定义文件
- [ ] `hub.apps.listDefinitions` 返回该定义
- [ ] `hub.apps.getDefinition`：
  - 存在 -> 返回 definition
  - 不存在 -> `app_definition_not_found (-32014)`

### 11.4 AppInstance
- [ ] `hub.apps.registerInstance` 注册后：
  - `hub.apps.listInstances` 能看到在线实例
  - lastSeenUtc 为当前时间附近
- [ ] `hub.apps.heartbeat` 更新 lastSeenUtc（且返回 serverTimeUtc）
- [ ] 超过 30s 不 heartbeat：listInstances 不再返回该实例（但不要求立即从内存删除）
- [ ] `hub.apps.unregisterInstance` 后：listInstances 不再返回

---

## 12. 边界与风险提示（M1 不做但要留接口位）

- **不要在 M1 引入 scopePolicy 强校验**：那属于 M3；M1 只需把 scope 字段的过滤逻辑在 listInstances 上做正确。
- **鉴权失败时 id 返回**：建议先解析 body 再校验 headers，避免客户端拿不到对应 id（更利于 SDK）。
- **端口动态分配**：务必在真正监听成功后写 `hub.json`，否则 discovery 读到错误地址。
