# DevHub 运维排障手册

本文面向 DevHub Host 与 Monitor 的运行期诊断、恢复与最小验证场景。

- 协议基线：[`docs/specification/protocol/Specification.md`](../../specification/protocol/Specification.md)
- 部署、启动与回滚关注点：[`docs/developer/operations/deployment.md`](./deployment.md)

## 1. 日志与数据根目录定位

### 1.1 关键环境变量

- `DEVHUB_DATA_DIR`：数据根目录，固定派生 `runtime/`、`apps/` 与 `logs/`

### 1.2 默认目录约定

- Windows：`%LOCALAPPDATA%/DevHub/`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`$XDG_DATA_HOME/DevHub/`（未设置时 `~/.local/share/DevHub/`）

标准目录布局：

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

说明：

- 单实例粒度为“同一 OS 用户 + 同一数据根目录”；不同 `DEVHUB_DATA_DIR` 可并行运行。
- 并行排障、并行测试或多 Host 联调时，必须为每个 Host 使用独立数据根目录。
- `prev_hub.json` 仅表示上一次正常退出前保留的发现快照；排障时若 `hub.json` 与 `prev_hub.json` 同时存在，应优先以 `hub.json` 为当前实例权威入口。

### 1.3 Monitor 日志

- Host 日志位于 `<DEVHUB_DATA_DIR>/logs/`。
- `DevHub Monitor` 会把自身结构化日志写入 Monitor 本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`。
- 桌面应用帮助页提供 Host / Monitor 日志目录的直接打开入口。
- Monitor 关键日志至少覆盖扫描状态切换、Host 启动尝试、连接/断连、设置保存和定义管理。
- Host 兼容性判定、设置恢复告警与重新扫描动作均以后端结构化日志为准；排障时可结合 `host_incompatible`、`settings_recovered` 等上下文定位原因。
- Monitor 设置页中的 `DEVHUB_DATA_DIR` 覆盖值与 Host 可执行文件路径必须为绝对路径；排障时若发现保存失败，应先排查是否填入了相对路径。

## 2. 故障分诊流程

1. 确认 Hub 进程是否已启动，且 `hub.json` 存在并可解析。  
2. 从 `hub.json` 读取 `httpBaseUrl/wsUrl/tokenFile`，确认客户端未硬编码地址。  
3. 使用 `hub.ping` 验证最小链路。  
4. 若调用失败，按错误码定位（鉴权/协议/参数/路由/启动/超时）。  
5. 对照日志中的 `method/requestId/clientId` 进行关联分析并执行恢复动作。  

## 3. 最小诊断命令

### 3.1 读取 `hub.json` 与 token

```bash
python3 - <<'PY'
import json
import os
import platform
from pathlib import Path

def data_dir():
    override = os.environ.get("DEVHUB_DATA_DIR")
    if override:
        return Path(override)
    if platform.system() == "Windows":
        return Path(os.environ["LOCALAPPDATA"]) / "DevHub"
    if platform.system() == "Darwin":
        return Path.home() / "Library" / "Application Support" / "DevHub"
    xdg = os.environ.get("XDG_DATA_HOME")
    if xdg:
        return Path(xdg) / "DevHub"
    return Path.home() / ".local" / "share" / "DevHub"

runtime = data_dir() / "runtime"
hub = json.loads((runtime / "hub.json").read_text(encoding="utf-8"))
token = Path(hub["tokenFile"]).read_text(encoding="utf-8").strip()
print("httpBaseUrl:", hub["httpBaseUrl"])
print("wsUrl:", hub["wsUrl"])
print("token(len):", len(token))
PY
```

### 3.2 最小健康检查（`hub.ping`）

以下示例中的 `DEVHUB_HTTP_BASE_URL` 与 `DEVHUB_TOKEN` 需要替换为 3.1 中读取出的实际值，或先导出为同名环境变量。

```bash
curl -sS -X POST "$DEVHUB_HTTP_BASE_URL/rpc" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $DEVHUB_TOKEN" \
  -H "X-DevHub-Protocol: 1" \
  -H "X-DevHub-ClientId: OpsDiag" \
  -H "X-DevHub-ClientSessionId: 00000000-0000-0000-0000-000000000010" \
  -d '{"jsonrpc":"2.0","id":"ops-ping-1","method":"hub.ping","params":{}}'
```

### 3.3 查看最近日志

```bash
LOG_DIR="$(python3 - <<'PY'
import os
import platform
from pathlib import Path

data_dir = os.environ.get("DEVHUB_DATA_DIR")
if data_dir:
    print(Path(data_dir) / "logs")
elif platform.system() == "Windows":
    print(Path(os.environ["LOCALAPPDATA"]) / "DevHub" / "logs")
elif platform.system() == "Darwin":
    print(Path.home() / "Library" / "Application Support" / "DevHub" / "logs")
else:
    xdg = os.environ.get("XDG_DATA_HOME")
    data_home = Path(xdg) if xdg else Path.home() / ".local" / "share"
    print(data_home / "DevHub" / "logs")
PY
)"
tail -n 200 "$LOG_DIR"/devhub-*.log
```

### 3.4 执行 Monitor 本地验证

```bash
npm --prefix apps/monitor run verify
```

该命令会串联：

- 前端构建与类型检查
- 前端控制器 / 日志 / 定义工作流测试，以及真实 Host 驱动的前端回归
- 原生后端单元测试
- `tauri:check` 非平台特定编译校验

## 4. 常见故障与恢复步骤

| 故障                                   | 症状                                            | 核查点                                                                                                   | 恢复动作                                              |
| -------------------------------------- | ----------------------------------------------- | -------------------------------------------------------------------------------------------------------- | ----------------------------------------------------- |
| `hub.json` 缺失                        | 客户端无法发现地址，连接失败                    | Hub 是否启动；`DEVHUB_DATA_DIR` 是否正确；`<dataDir>/runtime/` 是否可写                                  | 重启 Hub；确认数据根目录权限；重新读取 `hub.json`     |
| token 失效（`-32001 unauthorized`）    | HTTP/WS 鉴权失败                                | `Authorization` 是否 `Bearer`；token 是否来自当前 `hub.json.tokenFile`                                   | 重新读取 token；Hub 重启后刷新客户端缓存              |
| 协议版本错误（`-32099 not_supported`） | 返回 `not_supported`                            | `X-DevHub-Protocol` 是否为 `"1"`；WS `protocolVersion` 是否为 `1`                                        | 修正协议版本字段后重试                                |
| 缺失请求头（`-32600 invalid_request`） | 返回 `missing_header`                           | `X-DevHub-ClientId`、`X-DevHub-ClientSessionId` 是否存在且合法                                           | 补全请求头，`clientSessionId` 使用 UUID               |
| WS 鉴权前调用断连                      | 首包非 `hub.ws.authenticate` 后连接被关闭       | 首条 WS 报文是否为带 `id` 的 `hub.ws.authenticate`                                                       | 调整客户端顺序：先鉴权，再调用/订阅                   |
| `-32010 instance_not_found`            | 调用找不到目标实例                              | 实例是否注册并在线；`target.scope` 是否匹配；`queueIfOffline` 配置                                       | 先注册实例或修正路由参数；必要时开启 `queueIfOffline` |
| `-32020 launch_failed`                 | `autoLaunch` 场景拉起失败                       | `AppDefinition.launch.exePath` 是否存在；系统权限/路径是否正确                                           | 修复定义文件与可执行路径；重试 `hub.apps.launch`      |
| `-32012 invocation_timeout`            | request 等待超时                                | `waitTimeoutMs` 是否过短；被调方是否及时 `poll/respond`                                                  | 调大 `waitTimeoutMs`；排查被调方处理时延              |
| `-32011 invocation_expired`            | 调用已过期或已取消                              | `ttlMs` 是否过短；系统是否长时间阻塞                                                                     | 调大 `ttlMs`；减少调用链路耗时并重试                  |
| Monitor 显示 Host 版本不受支持        | 主页停留在发现态，并显示 `重新扫描/前往设置`    | `hub.ping` 是否成功；`runtime.protocolVersion` 是否为 `1`；`hub.getVersion` 返回的 Host 版本与 Monitor 内置 `JS SDK` 是否被判定为 `incompatible`；若 `hub.getVersion` 缺失则检查 `runtime.hubVersion` 回退值 | 升级 Host，或切换到兼容的数据根目录后重新扫描；若主页仅显示 `建议升级 Host` 或 `Host 兼容性未知` 横幅，则继续通过帮助页诊断信息排查 |
| Monitor 顶部出现设置恢复告警          | 启动后使用默认设置，顶部显示备份文件路径        | `settings.json` 是否损坏；同目录下是否生成 `settings.json.corrupt-*.bak`；Monitor 日志是否记录恢复上下文 | 修复或重建设置内容后重新保存；必要时比对备份文件恢复  |

## 5. 标准恢复动作（通用）

1. 重新发现：刷新 `hub.json`，同步最新地址。
2. 重新鉴权：按最新 `token.txt` 重建请求头/WS 认证参数。
3. 复测最小链路：`hub.ping` -> 业务调用 -> WS 订阅。
4. 必要时重启 Hub，并再次执行 smoke 校验：
   `python3 host/tests/blackbox/test_runner.py --smoke --no-header`

## 6. 升级路径与相关说明

- 协议兼容边界请参考 [`docs/specification/protocol/Specification.md`](../../specification/protocol/Specification.md) §9.2。
- 发布、启动与回滚校验请参考 [`docs/developer/operations/deployment.md`](./deployment.md)。
- 桌面 Monitor 工作区与本地运行命令请参考 [`../../../apps/monitor/README.md`](../../../apps/monitor/README.md)。
