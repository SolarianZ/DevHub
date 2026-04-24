# DevHub 开发指南

本文面向仓库开发者与本地联调场景，说明开发环境、常用命令、联调方式与最小验证要求。

## 1. 文档边界

- 所有公开行为、序列化字段、状态机、错误码与测试断言均以 [`Specification.md`](../../specification/protocol/Specification.md) 为准。
- 文档分类见 [`docs/README.md`](../../README.md)。
- 文档改动按受众进入正确目录：使用说明放入 `docs/user/`，开发维护说明放入 `docs/developer/`，规范资产放入 `docs/specification/`。
- 不得通过修改 `Specification.md` 掩盖核心协议行为的问题；凡直接涉及传输、发现、鉴权、路由、状态机、错误语义与安全边界的偏差，默认仍应优先修实现或测试。
- 本文只覆盖本仓库的开发与联调流程；部署、上线与运行期排障请分别参考 [`deployment.md`](../operations/deployment.md) 和 [`troubleshooting.md`](../operations/troubleshooting.md)。

## 2. 开发环境

- .NET SDK `10.0+`
- Python `3.11+`
- Python 依赖：`requests`
- Windows ACL 严格校验场景额外需要：`pywin32`

示例安装命令：

```bash
python3 -m pip install requests
```

Windows 需要执行 ACL 相关集成测试时，再额外安装：

```bash
python3 -m pip install pywin32
```

## 3. 仓库结构

```text
DevHub/
├── apps/                          # DevHub生态应用工作区
│   └── monitor/                   # Tauri 2 桌面 Monitor（前端 + src-tauri 原生后端）
├── docs/                          # 文档系统
│   ├── user/                      # 面向使用者与集成方的文档
│   ├── developer/                 # 面向开发者与维护者的文档
│   ├── specification/             # 权威规范、Schema 与协议示例
│   └── assets/                    # 文档静态资源
├── host/                          # Host 工作区
│   ├── DevHub.slnx                # Host 工作区解决方案文件
│   ├── src/                       # Host 生产代码
│   │   ├── DevHub.Core/           # 核心领域模型与基础服务
│   │   └── DevHub.Host/           # ASP.NET Core 宿主程序
│   └── tests/                     # 仓库级测试与验证工具
│       ├── assets/                # 跨工作区共享测试夹具
│       ├── blackbox/              # Python 黑盒测试与 runner
│       ├── conformance/           # 符合性向量、runner 与自测
│       ├── whitebox/              # .NET 白盒测试工程
│       └── tools/                 # 覆盖率配置与仓库级辅助脚本
├── sdks/                          # 多语言 SDK 工作区
│   ├── dotnet/                    # .NET SDK
│   ├── javascript/                # JavaScript / TypeScript SDK
│   └── python/                    # Python SDK
└── temp/                          # 临时测试产物与报告
```

## 4. 常用命令

```bash
find host -type d -name TestResults -prune -exec rm -rf {} +
dotnet build host/DevHub.slnx -c Release
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
dotnet test host/DevHub.slnx -c Release
npm --prefix apps/monitor run build:web
npm --prefix apps/monitor test
npm --prefix apps/monitor run test:native
npm --prefix apps/monitor run tauri:check
npm --prefix apps/monitor run verify
python3 scripts/release/package_monitor.py --release-id local-dry-run
python3 host/tests/conformance/vector_runner.py
python3 scripts/sdk/run_integration_full.py
python3 scripts/docs/check_markdown_links.py
python3 host/tests/blackbox/test_runner.py --smoke --no-header
python3 host/tests/blackbox/test_runner.py --full --no-header
python3 host/tests/tools/verify_coverage.py --root . --line-threshold 0.80 --branch-threshold 0.80
```

说明：

- 解决方案统一使用 `.slnx`，不要引入 `.sln` 文件。
- `dotnet run` 适合本地开发与联调；仓库级发布或 dry-run 打包优先使用 [`deployment.md`](../operations/deployment.md) 中的仓库级一键打包脚本，只有在需要 Host 本地固定产物目录时才单独使用 `dotnet publish`。
- `npm --prefix apps/monitor run build:web` 用于验证 Monitor 前端构建与类型检查。
- `npm --prefix apps/monitor test` 用于执行 Monitor 前端控制器测试、日志/定义工作流测试，以及基于真实 Host fixture 的前端回归。
- `npm --prefix apps/monitor run test:native` 用于执行 Monitor 原生后端单元测试。
- `npm --prefix apps/monitor run tauri:check` 用于执行 Tauri 原生侧的非平台特定编译校验。
- `npm --prefix apps/monitor run verify` 是 Monitor 工作区与 CI 对齐的本地验证入口，会串联前端构建/测试、原生单元测试和 `tauri:check`。
- `DEVHUB_MONITOR_SDK_SOURCE` 控制 Monitor 的 JS SDK 来源；默认 `release` 使用安装好的 SDK tarball，显式设置为 `local-src` 时会直连 `sdks/javascript/src`。独立 `monitor.yml` workflow 使用 `local-src` 作为仓库内 Monitor/SDK 联调门禁，该门禁以“只安装 `apps/monitor` 依赖即可完成验证”为前提，不得依赖 `sdks/javascript/node_modules`。
- `python3 scripts/release/package_monitor.py --release-id local-dry-run` 用于执行 Monitor 本地打包校验；可通过 `--sdk-source local-src` 生成本地 SDK 联调用途的开发包。
- Monitor 设置页中的 `dataDirOverride` 与 `hostExecutablePath` 只接受绝对路径；相对路径会被前端和 Tauri command 同时拒绝。
- Monitor 壳层按单实例运行；重复启动时会唤醒已有主窗口，不会并行拉起新的桌面进程。
- `python3 host/tests/conformance/vector_runner.py` 用于运行仓库级 v1.0.1 符合性向量；默认会调度位于各 SDK `tests/` 目录下的官方 `.NET` / `JS/TS` / `Python` 适配器，也支持通过 `--adapter-manifest` 挂接第三方自研适配器，前置构建与输出说明见 [`host/tests/conformance/README.md`](../../../host/tests/conformance/README.md)。
- `python3 scripts/sdk/run_integration_full.py` 是仓库级 SDK 集成测试的 build-once / run-many 本地入口：脚本会先把 `DevHub.Host` 构建到隔离输出目录，再通过共享环境变量 `DEVHUB_SDK_HOST_ASSEMBLY` 依次运行 `.NET`、`JS/TS`、`Python` SDK 测试。执行前请先准备好 JS / Python 依赖。
- `python3 scripts/docs/check_markdown_links.py` 会扫描仓库内所有未被 `.gitignore` 忽略的 `.md` 文件，提取 `[text](path)` 形式的本地路径超链接并校验目标是否存在；发现失效路径时，会按文件分组输出原始超链接并返回非零退出码。
- `python3 host/tests/blackbox/test_runner.py --smoke --no-header` 与 `python3 host/tests/blackbox/test_runner.py --full --no-header` 默认会构建并启动隔离 Host；如需复用外部已启动的 Host 或自定义启动方式，请参考 [`host/tests/README.md`](../../../host/tests/README.md)。
- `find host -type d -name TestResults -prune -exec rm -rf {} +` 用于在重新跑 Host coverage 前清理历史产物，保持本地与 GitHub CI 的 `TestResults` 口径一致。
- `python3 host/tests/tools/verify_coverage.py --root . --line-threshold 0.80 --branch-threshold 0.80` 用于校验 Host 白盒测试覆盖率门槛；覆盖率 settings 位于 `host/tests/tools/coverage.runsettings`，脚本会优先使用 `trx` 附件中的 `coverage.cobertura.xml`，并忽略同工程下内容重复的 GUID 镜像副本。
- 更细的黑盒 / conformance / tools 分层说明见 [`host/tests/README.md`](../../../host/tests/README.md)。

## 5. SDK 工作区开发与验证

### 5.1 `.NET SDK`

常用命令：

```bash
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
dotnet pack sdks/dotnet/src/DevHub.Sdk/DevHub.Sdk.csproj -c Release -o temp/sdk-pack
```

说明：

- `.NET SDK` 集成测试会先准备隔离 Host 运行副本，再为每个测试用例分配独立临时 `DEVHUB_DATA_DIR`。
- 默认情况下，测试夹具会把 Host 构建到自己的隔离输出目录，再从该隔离产物启动临时 Host，不直接复用源代码树下的默认构建输出。
- 如果需要关闭这一步默认构建，或希望与其他语言 SDK 统一复用同一份 Host 产物，请优先设置 `DEVHUB_SDK_HOST_ASSEMBLY`；如需只覆盖 `.NET SDK`，可改用 `DEVHUB_DOTNET_SDK_HOST_ASSEMBLY`。

### 5.2 `JS/TS SDK`

常用命令：

```bash
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run typecheck
npm --prefix sdks/javascript run build
npm --prefix sdks/javascript test
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

说明：

- `JS/TS SDK` 根入口必须保持 `Node.js 20+` 与浏览器 / WebView 双运行时可导入；Node.js 文件系统发现能力统一通过 `@devhub/sdk-javascript/runtime` 暴露。
- `JsonRpcWsSession` / `DevHubEventsClient` 在缺少全局 `WebSocket` 时允许以运行时懒加载方式回退到 Node 专用 `ws`；该回退不得让根入口源码在类型检查、浏览器构建或 `DEVHUB_MONITOR_SDK_SOURCE=local-src` 的源码消费阶段静态解析 `ws` / `@types/ws`。
- `npm --prefix sdks/javascript test` 中的集成测试会自行构建并启动临时 DevHub Host，为当前测试文件分配独立临时 `dataDir`，不会连接开发机默认数据目录下的常驻 Hub。
- 如果需要关闭 Host 的默认临时构建，或希望与其他语言 SDK 并行复用同一份 Host 产物，请优先设置 `DEVHUB_SDK_HOST_ASSEMBLY`；如需只覆盖 `JS/TS SDK`，可改用 `DEVHUB_JS_SDK_HOST_ASSEMBLY`。

### 5.3 `Python SDK`

常用命令：

```bash
python3 -m pip install -e "./sdks/python[test]"
python3 -m pytest sdks/python/tests
python3 -m pip install build
python3 -m build --sdist --wheel --outdir temp/sdk-pack sdks/python
```

说明：

- `Python SDK` 集成测试会先准备隔离 Host 运行副本，再为每个用例分配独立临时 `DEVHUB_DATA_DIR`；如需验证这组测试，请直接运行 `pytest`，不要先手工启动本地 Hub。
- 如果需要关闭 Host 的默认临时构建，或希望与其他语言 SDK 并行复用同一份 Host 产物，请优先设置 `DEVHUB_SDK_HOST_ASSEMBLY`；如需只覆盖 `Python SDK`，可改用 `DEVHUB_PYTHON_SDK_HOST_ASSEMBLY`。

### 5.4 跨语言 SDK 验证

- `python3 scripts/sdk/run_integration_full.py` 会先把 `DevHub.Host` 构建到隔离输出目录，再通过共享环境变量 `DEVHUB_SDK_HOST_ASSEMBLY` 依次运行 `.NET`、`JS/TS`、`Python` SDK 测试。
- 如需观察 SDK 集成测试或 Host fixture 启动阶段的实时等待状态，可设置 `DEVHUB_TEST_LIVE_STATUS=true`；该变量接受 `1/0`、`true/false`、`yes/no`、`on/off`。

## 6. 推荐开发流程

1. 先阅读 [`Specification.md`](../../specification/protocol/Specification.md) 中对应章节，确认改动是否影响公开契约。
2. 执行 `dotnet build host/DevHub.slnx -c Release`，确保当前工作区基础可构建。
3. 使用 `dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release` 启动本地 Hub。
4. 从数据根目录下的 `<dataDir>/runtime/hub.json` 动态读取 `httpBaseUrl`、`wsUrl` 与 `tokenFile`，禁止硬编码端口或地址。
5. 完成改动后，至少执行单元测试与 smoke 集成测试；若涉及官方 SDK 集成测试夹具、SDK 维护脚本或多语言一致性，再补充 `python3 scripts/sdk/run_integration_full.py` 或最小相关 SDK 测试。

涉及 `apps/monitor/` 的改动时，额外执行：

```bash
npm --prefix apps/monitor run verify
```

若改动同时涉及 `apps/monitor/` 与 `sdks/javascript/` 的联调链路，再额外以 `DEVHUB_MONITOR_SDK_SOURCE=local-src` 执行最小相关 Monitor 构建或验证，并确认该命令在只安装 `apps/monitor` 依赖的前提下仍能通过。

## 7. 本地联调

### 7.1 启动 Host

```bash
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
```

启动后，Hub 会在 `<dataDir>/runtime/` 下写出发现文件 `hub.json` 与访问令牌 `token.txt`。数据根目录的默认位置与覆盖方式见 [`deployment.md`](../operations/deployment.md)。

### 7.2 发现地址与 token

客户端必须以 `hub.json` 为权威来源读取下列字段：

- `httpBaseUrl`
- `wsUrl`
- `tokenFile`

如需查看当前数据根目录、发现文件和 token 的最小诊断脚本，请参考 [`troubleshooting.md`](../operations/troubleshooting.md) 中“3.1 读取 `hub.json` 与 token”一节。

### 7.3 HTTP 鉴权与 `hub.ping`

以下示例中的 `DEVHUB_HTTP_BASE_URL` 与 `DEVHUB_TOKEN` 需要替换为 7.2 中从 `hub.json` 与 `tokenFile` 读取出的实际值，或先导出为同名环境变量。

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

期望结果示意：

```json
{
  "jsonrpc": "2.0",
  "id": "ping-1",
  "result": {
    "ok": true,
    "serverTimeUtc": "<server-time-utc>",
    "echo": "hello-devhub"
  }
}
```

### 7.4 `hub.invoke.request` 请求体示例

前提：调用按精确 `appId + target.scope` 路由。若已有在线匹配实例且支持 `poll/respond`，Hub 会直接路由；若不存在在线匹配实例，但存在精确命中的可启动 Definition 且 `options.autoLaunch = true`，Hub 会先尝试 auto-launch；只有仍无法建立路由时才会返回 `-32010 instance_not_found`。
下例显式请求 Global 作用域；`hub.invoke.request` 必须显式提供字符串 `target.scope`。如果需要跨全部作用域枚举，请改用 `hub.apps.listDefinitions` 或 `hub.apps.listInstances` 并显式传入 `scope: null`。

```json
{
  "jsonrpc": "2.0",
  "id": "req-1",
  "method": "hub.invoke.request",
  "params": {
    "appId": "test.app",
    "target": {
      "scope": ""
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

### 7.5 WebSocket 鉴权与订阅

首条消息必须是带 `id` 的 `hub.ws.authenticate` 请求：

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

认证成功后，可发送 `hub.events.subscribe` 订阅事件。

## 8. 最小验证要求

涉及协议、宿主、公开接口或运行时行为的改动，在提交前至少执行：

```bash
dotnet test host/DevHub.slnx -c Release
python3 host/tests/blackbox/test_runner.py --smoke --no-header
```

其中 `host/tests/blackbox/test_runner.py` 默认会构建并自启隔离 Host；只有显式传入 `--use-existing-host --no-build-host` 时，才会复用外部已启动的 Host。

如需完整黑盒回归，再执行：

```bash
python3 host/tests/blackbox/test_runner.py --full --no-header
```

## 9. 相关文档

- [`docs/README.md`](../../README.md)
- [`docs/specification/protocol/Specification.md`](../../specification/protocol/Specification.md)
- [`docs/developer/operations/deployment.md`](../operations/deployment.md)
- [`docs/developer/operations/troubleshooting.md`](../operations/troubleshooting.md)
- [`docs/user/protocol/README.md`](../../user/protocol/README.md)
- [`docs/specification/schema/v1.0.1/README.md`](../../specification/schema/v1.0.1/README.md)
- [`docs/specification/protocol-examples/v1.0.1/README.md`](../../specification/protocol-examples/v1.0.1/README.md)
- [`docs/developer/architecture/system-overview.md`](../architecture/system-overview.md)
- [`../../../apps/monitor/README.md`](../../../apps/monitor/README.md)
- [`../../../host/tests/README.md`](../../../host/tests/README.md)
- [`../../../host/tests/conformance/README.md`](../../../host/tests/conformance/README.md)
