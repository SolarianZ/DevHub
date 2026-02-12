# DevHub

DevHub 是本机单用户（per-user）守护进程，为开发工具提供统一的实例发现、调用编排与事件订阅能力。

## 1. 当前范围

- 协议基线：`docs/Spec.md`（v1.0.1，最终版）
- 里程碑范围：M1~M4（Hub 核心能力）已实现
- 当前版本：`1.0.1`（`src/DevHub.Host/DevHub.Host.csproj` / `src/DevHub.Core/DevHub.Core.csproj`）
- 不包含：SDK（.NET / JS/TS，M5 范围）

## 2. 环境要求

- .NET SDK 10.0+
- Python 3.9+
- Python 依赖：`requests`（Windows ACL 严格校验场景额外需要 `pywin32`）

## 3. 快速上手

### 3.1 启动 Hub

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

启动后会生成发现文件与令牌文件：

- `<runtimeDir>/hub.json`
- `<runtimeDir>/token.txt`

其中 `runtimeDir` 可通过 `DEVHUB_RUNTIME_DIR` 覆盖。

### 3.2 动态发现地址与 token（必须）

客户端必须通过 `hub.json` 读取 `httpBaseUrl`、`wsUrl`、`tokenFile`，禁止硬编码端口或 WS 路径。

默认运行时目录（未设置 `DEVHUB_RUNTIME_DIR` 时）：

- Windows：`%LOCALAPPDATA%/DevHub/runtime/`
- macOS：`~/Library/Application Support/DevHub/runtime/`
- Linux：`$XDG_DATA_HOME/DevHub/runtime/`（未设置时为 `~/.local/share/DevHub/runtime/`）

可用以下脚本导出当前会话变量（macOS/Linux，zsh/bash）：

```bash
eval "$(python3 - <<'PY'
import json
import os
import platform
import shlex
from pathlib import Path

def get_runtime_dir() -> Path:
    override = os.environ.get("DEVHUB_RUNTIME_DIR")
    if override:
        return Path(override)

    system = platform.system()
    home = Path.home()
    if system == "Windows":
        local = os.environ.get("LOCALAPPDATA")
        if not local:
            raise RuntimeError("LOCALAPPDATA 未设置")
        return Path(local) / "DevHub" / "runtime"
    if system == "Darwin":
        return home / "Library" / "Application Support" / "DevHub" / "runtime"

    xdg_data_home = os.environ.get("XDG_DATA_HOME")
    if xdg_data_home:
        return Path(xdg_data_home) / "DevHub" / "runtime"
    return home / ".local" / "share" / "DevHub" / "runtime"

runtime_dir = get_runtime_dir()
hub_json_path = runtime_dir / "hub.json"
hub = json.loads(hub_json_path.read_text(encoding="utf-8"))
token = Path(hub["tokenFile"]).read_text(encoding="utf-8").strip()

print(f'export DEVHUB_HTTP_BASE_URL={shlex.quote(hub["httpBaseUrl"])}')
print(f'export DEVHUB_WS_URL={shlex.quote(hub["wsUrl"])}')
print(f'export DEVHUB_TOKEN={shlex.quote(token)}')
PY
)"
```

## 4. 协议实操

### 4.1 HTTP 鉴权 + `hub.ping`

```bash
curl -sS -X POST "$DEVHUB_HTTP_BASE_URL/rpc" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DEVHUB_TOKEN" \
  -H "X-DevHub-Protocol: 1" \
  -H "X-DevHub-ClientId: QuickStartCLI" \
  -H "X-DevHub-ClientSessionId: 00000000-0000-0000-0000-000000000001" \
  -d '{
    "jsonrpc": "2.0",
    "id": "ping-1",
    "method": "hub.ping",
    "params": { "echo": "hello-devhub" }
  }'
```

期望结果（示意）：

```json
{
  "jsonrpc": "2.0",
  "id": "ping-1",
  "result": {
    "ok": true,
    "serverTimeUtc": "2026-02-12T10:00:00Z",
    "echo": "hello-devhub"
  }
}
```

### 4.2 基础调用示例：`hub.invoke.request`

> 前提：目标 `appId` 有在线实例可 `poll/respond`；否则会返回 `-32010 instance_not_found`。

```json
{
  "jsonrpc": "2.0",
  "id": "req-1",
  "method": "hub.invoke.request",
  "params": {
    "appId": "test.app",
    "target": {
      "scope": null
    },
    "method": "test.ping",
    "args": {
      "from": "quickstart"
    },
    "options": {
      "ttlMs": 300000,
      "waitTimeoutMs": 120000,
      "queueIfOffline": true,
      "autoLaunch": true
    }
  }
}
```

### 4.3 WS 鉴权与订阅

1) 首条消息必须是 `hub.ws.authenticate`，且必须为请求（包含 `id`）：

```json
{
  "jsonrpc": "2.0",
  "id": "ws-auth-1",
  "method": "hub.ws.authenticate",
  "params": {
    "token": "<token-from-hub-json.tokenFile>",
    "protocolVersion": 1,
    "clientId": "QuickStartWS",
    "clientSessionId": "00000000-0000-0000-0000-000000000002"
  }
}
```

2) 认证成功后再订阅事件：

```json
{
  "jsonrpc": "2.0",
  "id": "sub-1",
  "method": "hub.events.subscribe",
  "params": {
    "types": ["invocation.completed", "invocation.failed"]
  }
}
```

3) 取消订阅：

```json
{
  "jsonrpc": "2.0",
  "id": "sub-2",
  "method": "hub.events.unsubscribe",
  "params": {
    "subscriptionId": "sub-..."
  }
}
```

## 5. 常见错误速查

| code | message | 典型场景 | 优先核查 |
| --- | --- | --- | --- |
| `-32001` | `unauthorized` | token 缺失/错误；WS 鉴权失败 | token 是否来自当前 `hub.json.tokenFile`，是否使用 `Bearer` |
| `-32099` | `not_supported` | `X-DevHub-Protocol` 缺失或不为 `1`；WS 协议版本不匹配 | 请求头/WS `protocolVersion` |
| `-32600` | `invalid_request` | JSON-RPC 信封非法、`id:null`、batch root array | 请求结构是否符合 JSON-RPC 2.0 |
| `-32602` | `invalid_params` | 参数类型/约束错误 | `params` 必须为对象；字段类型与约束 |
| `-32010` | `instance_not_found` | 目标实例不存在，或离线且不允许排队 | 实例是否已注册/在线，`queueIfOffline` 设置 |
| `-32020` | `launch_failed` | 启动配置缺失或拉起失败 | `AppDefinition.launch`、`exePath`、进程启动日志 |
| `-32012` | `invocation_timeout` | `waitTimeoutMs` 超时 | 调整 `waitTimeoutMs`，检查被调方是否及时 `poll/respond` |
| `-32011` | `invocation_expired` | `ttlMs` 到期或调用已取消 | 调整 `ttlMs`，排查长耗时链路 |

更多诊断流程见：`docs/运维排障手册.md`。

## 6. 测试与质量命令

```bash
dotnet test src/DevHub.slnx -c Release
python3 tests/test_runner.py --full --no-header
```

发布前推荐至少执行：

```bash
dotnet build src/DevHub.slnx -c Release
python3 tests/test_runner.py --smoke --no-header
```

## 7. 打包发布

本地构建发布包：

```bash
dotnet publish src/DevHub.Host/DevHub.Host.csproj -c Release -o ./artifacts/devhub
```

仓库工作流：

- CI：`.github/workflows/ci.yml`
- 发布打包：`.github/workflows/release.yml`

## 8. v1 已知限制

- Hub 重启后，内存态 pending invocation 与 request waiter 不保留。
- 调用投递语义为 at-least-once，租约过期可能导致重投递。
- WS 事件不支持重放，断线窗口内事件可能丢失。

详见：`docs/DevHub协议与开发规划.md` 的“15. v1 已知限制与 v2 展望”章节。

## 9. 参考文档

- 协议规范：`docs/Spec.md`
- 发布说明：`docs/DevHub_v1.0.1_发布说明.md`
- 运维排障：`docs/运维排障手册.md`
