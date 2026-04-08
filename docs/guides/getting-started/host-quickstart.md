# DevHub Host 快速上手

本文面向首次使用 DevHub Host 的外部用户，覆盖环境准备、启动方式、`hub.json` / `tokenFile` 发现、最小 `hub.ping` 验证，以及继续接入官方 SDK 或原始协议的入口。

## 1. 前置条件

- 当前仓库内最直接的上手路径依赖 `.NET 10 SDK`，用于从源码运行或发布 Host。
- 如果你准备把 Host 放到独立数据目录运行，请先决定 `DEVHUB_DATA_DIR` 的值，并确保当前用户对该目录有读写权限。
- 首个正式 GitHub Release 发布前，正式下载链接、平台压缩包名称和安装命令统一使用显式 TODO 占位：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 Host 下载资产、平台对应压缩包名称与启动命令；当前阶段不要填写未发布的版本号、下载链接或安装命令。
```

如需了解部署与运行边界，请先阅读 [`../../operations/部署与运行指南.md`](../../operations/部署与运行指南.md)。

## 2. 启动 Host

### 2.1 直接从源码运行

在仓库根目录执行：

```bash
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
```

如果你希望把运行时文件写入独立目录，可以先设置 `DEVHUB_DATA_DIR`：

```bash
export DEVHUB_DATA_DIR=/path/to/devhub
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
```

Windows PowerShell 示例：

```powershell
$env:DEVHUB_DATA_DIR = "C:\DevHub"
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
```

### 2.2 使用发布输出启动

如果你需要固定产物目录，可先发布再运行：

```bash
dotnet publish host/src/DevHub.Host/DevHub.Host.csproj -c Release -o ./artifacts/devhub
dotnet ./artifacts/devhub/DevHub.Host.dll
```

正式 GitHub Release 发布后，发布资产名称会与 [`../../operations/publishing/release-asset-layout.md`](../../operations/publishing/release-asset-layout.md) 中定义的命名规则一致。

## 3. 读取 `hub.json` 与 `tokenFile`

Host 启动成功后，会在 `<dataDir>/runtime/` 下写入 `hub.json`，并在其中提供 `tokenFile` 的绝对路径。客户端必须以 `hub.json` 为发现入口，不能硬编码端口或 WebSocket 地址。

默认数据根目录：

- Windows：`%LOCALAPPDATA%/DevHub/`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`$XDG_DATA_HOME/DevHub/`，若未设置则为 `~/.local/share/DevHub/`

标准目录结构：

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

PowerShell 读取示例：

```powershell
$dataDir = if ($env:DEVHUB_DATA_DIR) { $env:DEVHUB_DATA_DIR } else { Join-Path $env:LOCALAPPDATA "DevHub" }
$hub = Get-Content (Join-Path $dataDir "runtime/hub.json") | ConvertFrom-Json
$token = (Get-Content $hub.tokenFile -Raw).Trim()

$hub.httpBaseUrl
$hub.wsUrl
$token
```

## 4. 最小 `hub.ping` 验证

### 4.1 Bash / macOS / Linux

```bash
data_dir="${DEVHUB_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/DevHub}"
hub_json="$data_dir/runtime/hub.json"

readarray -t values < <(python - "$hub_json" <<'PY'
import json
import sys
from pathlib import Path

hub = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
print(hub["httpBaseUrl"])
print(hub["tokenFile"])
PY
)

http_base_url="${values[0]}"
token="$(tr -d '\r\n' < "${values[1]}")"

curl -sS -X POST "$http_base_url/rpc" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $token" \
  -H "X-DevHub-Protocol: 1" \
  -H "X-DevHub-ClientId: quickstart-client" \
  -H "X-DevHub-ClientSessionId: 00000000-0000-0000-0000-000000000011" \
  -d '{"jsonrpc":"2.0","id":"quickstart-ping","method":"hub.ping","params":{"echo":"world"}}'
```

### 4.2 Windows PowerShell

```powershell
$dataDir = if ($env:DEVHUB_DATA_DIR) { $env:DEVHUB_DATA_DIR } else { Join-Path $env:LOCALAPPDATA "DevHub" }
$hub = Get-Content (Join-Path $dataDir "runtime/hub.json") | ConvertFrom-Json
$token = (Get-Content $hub.tokenFile -Raw).Trim()

$headers = @{
  Authorization              = "Bearer $token"
  "X-DevHub-Protocol"        = "1"
  "X-DevHub-ClientId"        = "quickstart-client"
  "X-DevHub-ClientSessionId" = "00000000-0000-0000-0000-000000000011"
}

$body = @{
  jsonrpc = "2.0"
  id      = "quickstart-ping"
  method  = "hub.ping"
  params  = @{ echo = "world" }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri "$($hub.httpBaseUrl)/rpc" -Headers $headers -ContentType "application/json" -Body $body
```

成功时，返回结果会包含：

- `ok = true`
- `serverTimeUtc`
- `echo`

## 5. 下一步

- 如果你准备使用官方 SDK，请继续阅读 [`../sdk/README.md`](../sdk/README.md) 并选择对应语言的接入文档。
- 如果你计划直接对接协议，请阅读 [`../无SDK接入指南.md`](../无SDK接入指南.md)。
- 如果你需要排查启动失败、运行时文件缺失或 `hub.ping` 返回错误，请阅读 [`../../operations/运维排障手册.md`](../../operations/运维排障手册.md)。
