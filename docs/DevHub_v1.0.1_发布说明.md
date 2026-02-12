# DevHub v1.0.1 发布说明

发布日期：2026-02-12  
适用分支：`m4`  
协议基线：`docs/Spec.md`（v1.0.1）

## 1. 发布范围

本次发布覆盖 M1~M4 的 Hub 能力，不包含 SDK（M5）：

- M1：HTTP JSON-RPC、发现文件、token 鉴权、定义与实例管理
- M2：调用编排（`notify/request/poll/respond`）、离线队列、启动协调
- M3：Scope 语义严格化（默认 global、显式 scope 不回退）
- M4：WS 首包鉴权、事件订阅与推送

本次发布不引入新的协议版本，`protocolVersion` 仍为 `1`。

## 2. 升级影响

### 2.1 客户端行为影响

- WS 首条消息必须是 `hub.ws.authenticate`，且必须包含 `id`。
- HTTP 通知（无 `id` 请求）按规范返回 HTTP `200` + 空响应体。
- 客户端必须每次启动后重新读取 `hub.json`，不得缓存端口。
- token 按 Hub 会话轮换；Hub 重启后旧 token 必须视为失效。

### 2.2 运维影响

- 启动后必须确认 `<runtimeDir>/hub.json` 与 `<runtimeDir>/token.txt` 正常生成。
- 故障定位建议优先使用 `DEVHUB_LOG_DIR` 对应日志目录。

## 3. 兼容策略

兼容策略遵循 `docs/Spec.md` §9.2：

- 允许：向响应新增可选字段、增加新错误码、增加新 RPC 方法。
- 禁止：修改既有字段类型/语义、移除既有字段、收紧已有输入验证。

v1.0.1 在协议层不引入破坏性变更，维持 v1.x 兼容口径。

## 4. 回滚说明

### 4.1 回滚触发条件（建议）

- 升级后出现持续性 `unauthorized` / `not_supported` / `internal_error` 异常峰值。
- 核心链路（`hub.ping`、`hub.invoke.request`、`hub.events.subscribe`）不可用且无法在观察窗口内恢复。

### 4.2 回滚步骤

1. 停止当前 v1.0.1 Hub 进程。  
2. 部署上一稳定版本制品并启动。  
3. 重新读取新 Hub 生成的 `hub.json` 与 `token.txt`（不要复用旧地址/token 缓存）。  
4. 执行健康检查（见 4.3）。  
5. 记录回滚原因、时间点、影响范围与后续修复计划。

### 4.3 回滚后健康检查

- `hub.json` 可解析，且包含 `httpBaseUrl/wsUrl/tokenFile`。
- `hub.ping` 返回 `{ "ok": true }`。
- WS 鉴权与一次订阅成功。
- `python3 tests/test_runner.py --smoke --no-header` 通过。

## 5. 发布后验证清单

- [ ] Host 启动后成功生成 `hub.json/token.txt`
- [ ] HTTP `hub.ping` 鉴权成功
- [ ] `hub.invoke.request` 主链路可用（或返回可预期业务错误码）
- [ ] WS `hub.ws.authenticate` + `hub.events.subscribe` 成功
- [ ] `dotnet build src/DevHub.slnx -c Release` 成功
- [ ] `python3 tests/test_runner.py --smoke --no-header` 成功
- [ ] 关键日志字段（method/requestId/clientId）可用于追踪

如需详细排障步骤，请参考 `docs/运维排障手册.md`。
