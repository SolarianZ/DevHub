# DevHub 部署与运行指南

本文面向本地安装、发布、启动与上线后校验场景，说明 DevHub 的运行约束、目录布局、发布方式与基础运维检查项。

## 1. 部署原则

- DevHub 是本机单用户守护进程，默认按“同一 OS 用户 + 同一数据根目录一个实例”运行；不同数据根目录可并行启动。
- Hub 仅应监听回环地址（`127.0.0.1`、`localhost`，实现也可额外使用 `::1`）。
- 客户端必须从 `hub.json` 动态发现 `httpBaseUrl` 与 `wsUrl`，禁止假设固定端口或固定 WebSocket 路径。
- Hub 启动后会生成新的会话 token；重启或重新部署后，客户端需要重新读取 `token.txt`。

## 2. 数据根目录与配置

### 2.1 默认数据根目录

默认数据根目录遵循系统约定：

- Windows：`%LOCALAPPDATA%/DevHub/`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`$XDG_DATA_HOME/DevHub/`（未设置时为 `~/.local/share/DevHub/`）

默认目录结构如下：

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

补充说明：

- `prev_hub.json` 不是客户端发现入口；客户端始终只应读取 `<dataDir>/runtime/hub.json`。
- Host 正常退出时，会将当前 `hub.json` 迁移为 `<dataDir>/runtime/prev_hub.json`（覆盖已有文件），用于保留上一次会话的运行时快照。
- Host 运行期间会只读占用 `hub.json`：允许其他进程读取，但会拒绝其他进程覆盖当前发现文件。

### 2.2 环境变量覆盖

- `DEVHUB_DATA_DIR`：覆盖整个数据根目录；`runtime/`、`apps/` 与 `logs/` 都从该根目录固定派生。

说明：

- `DEVHUB_DATA_DIR` 仅覆盖数据根目录，不应被当作固定端口或固定地址的替代手段。
- 如果使用环境变量覆盖路径，应在启动 Hub 前设置，并确保目录对当前用户可读写。
- 并行部署、并行测试或多 Host 联调时，必须为每个 Host 分配独立 `DEVHUB_DATA_DIR`。

### 2.3 关键文件

`hub.json` 是客户端发现入口，至少应关注：

- `protocolVersion`
- `httpBaseUrl`
- `wsUrl`
- `tokenFile`
- `runtimeTuning`

`token.txt` 保存 bearer token，文件权限应限制为当前用户可访问。

## 3. 发布

建议使用 Release 配置发布 Host：

```bash
dotnet publish host/src/DevHub.Host/DevHub.Host.csproj -c Release -o ./artifacts/devhub
```

发布目录中会包含 `DevHub.Host.dll` 及其依赖文件。若只做本地开发联调，也可直接使用 `dotnet run`；正式交付或固定产物验证时，优先使用 `dotnet publish` 的输出目录。

## 4. 启动

### 4.1 使用默认目录启动

```bash
dotnet ./artifacts/devhub/DevHub.Host.dll
```

### 4.2 使用显式目录启动

```bash
export DEVHUB_DATA_DIR=/path/to/devhub
dotnet ./artifacts/devhub/DevHub.Host.dll
```

Windows PowerShell 示例：

```powershell
$env:DEVHUB_DATA_DIR = "C:\DevHub"
dotnet .\artifacts\devhub\DevHub.Host.dll
```

启动成功后，应看到 `<dataDir>/runtime/` 中生成 `hub.json` 与 `token.txt`，实例镜像写入 `<dataDir>/apps/instances/`，并且 `<dataDir>/logs/` 开始写入 `devhub-*.log`。

### 4.3 使用 DevHub Monitor 作为本地桌面入口

本仓库提供位于 [`../../../apps/monitor/README.md`](../../../apps/monitor/README.md) 的官方桌面 Monitor 工作区，用于本地扫描 Host、展示实例/定义、维护设置并读取日志。

本地运行命令：

```bash
npm --prefix sdks/javascript ci
npm --prefix apps/monitor ci
npm --prefix apps/monitor run tauri:dev
```

运行约束：

- Monitor 通过原生后端解析有效 `DEVHUB_DATA_DIR`，并只在真实 `hub.ping` 成功后切入状态页。
- Host 日志固定读取 `<DEVHUB_DATA_DIR>/logs/`。
- Monitor 自身日志写入其本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`，当前目录会显示在 Monitor 设置页的“Monitor 日志目录”字段中。
- 通过 Monitor 启动 Host 时，会把当前有效 `DEVHUB_DATA_DIR` 传递给子进程，保持发现目录与 Host 写盘目录一致。

## 5. 上线后校验

### 5.1 校验发现文件

启动后先检查 `hub.json`：

- `httpBaseUrl` 不应包含末尾斜杠。
- `wsUrl` 应为绝对 WebSocket URL，且不应包含末尾斜杠。
- `httpBaseUrl` 与 `wsUrl` 应指向回环地址。
- `tokenFile` 应为绝对路径，且文件存在。

### 5.2 最小健康检查

以下示例中的 `DEVHUB_HTTP_BASE_URL` 与 `DEVHUB_TOKEN` 需要替换为 5.1 中检查到的实际值，或先导出为同名环境变量。

```bash
curl -sS -X POST "$DEVHUB_HTTP_BASE_URL/rpc" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DEVHUB_TOKEN" \
  -H "X-DevHub-Protocol: 1" \
  -H "X-DevHub-ClientId: OpsDiag" \
  -H "X-DevHub-ClientSessionId: 00000000-0000-0000-0000-000000000010" \
  -d '{"jsonrpc":"2.0","id":"ops-ping-1","method":"hub.ping","params":{}}'
```

如需先从数据根目录定位并读取 `<dataDir>/runtime/hub.json` 与 token，请直接使用 [`运维排障手册`](./运维排障手册.md) 中“3.1 读取 `hub.json` 与 token”一节的最小诊断脚本。

### 5.3 WebSocket 校验

如果部署后需要验证事件链路，连接 `hub.json.wsUrl` 后，第一条消息必须发送 `hub.ws.authenticate`；在认证成功前发送其他请求或通知，连接会被拒绝。

## 6. 运行约束

- Hub 重启后，内存态 pending invocation 与 request waiter 不保留。
- 调用投递语义为 at-least-once，租约过期可能导致重投递。
- WebSocket 事件当前不支持重放，断线窗口内事件可能丢失。

相关架构背景与模块边界请参考 [`system-overview.md`](../architecture/system-overview.md)。

## 7. 运行与回滚建议

- 发布后若发生重启、替换或回滚，客户端都应重新读取最新的 `hub.json` 与 `token.txt`。
- 若需并行运行多个 Host，请为每个实例指定不同的 `DEVHUB_DATA_DIR`，避免落入同一单实例槽位。
- 若出现鉴权、协议版本、实例发现或调用超时问题，请优先参考 [`运维排障手册`](./运维排障手册.md)。
- 若需要使用桌面 GUI 观察当前 Host、实例、定义或日志，请参考 [`../../../apps/monitor/README.md`](../../../apps/monitor/README.md)。
- 若需要执行部署后的最小回归验证，可运行：

```bash
python3 host/tests/blackbox/test_runner.py --smoke --no-header
```
