# DevHub

DevHub 是一个本机单用户（per-user）守护进程，为开发工具提供统一的实例发现、调用编排与事件订阅能力。

当前仓库范围覆盖：
- Hub 核心实现（M1~M4）
- 白盒单元测试（xUnit）
- 黑盒集成测试（Python）

不包含：
- SDK（.NET / JS/TS，属于 M5）

## 当前状态

- 协议基线：`docs/Spec.md`（v1.0.1，最终版）
- 里程碑状态：M1~M4 已实现
- 运行模式：本机回环地址 + token 鉴权 + JSON-RPC 2.0（HTTP/WS）

## 仓库结构

- `docs/`：规范与里程碑文档
- `src/DevHub.Core/`：核心领域与服务
- `src/DevHub.Host/`：ASP.NET Core Host
- `src/DevHub.Tests/`：白盒测试
- `src/DevHub.Host.Tests/`：Host/WS 生命周期测试
- `tests/`：黑盒集成测试

## 环境要求

- .NET SDK 10.0+
- Python 3.9+
- Python 依赖：`requests`

## 快速启动

1. 启动 Host：

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

2. 运行白盒测试：

```bash
dotnet test src/DevHub.slnx -c Release
```

3. 运行黑盒测试（完整模式）：

```bash
python3 tests/test_runner.py --full --no-header
```

## 打包发布

本地发布包构建：

```bash
dotnet publish src/DevHub.Host/DevHub.Host.csproj -c Release -o ./artifacts/devhub
```

仓库已提供：
- CI：`.github/workflows/ci.yml`
- 发布打包流程：`.github/workflows/release.yml`

## 运行时目录

默认按系统约定生成运行时目录，亦可通过环境变量覆盖：
- `DEVHUB_RUNTIME_DIR`
- `DEVHUB_APPDEFS_DIR`
- `DEVHUB_LOG_DIR`

Hub 发现文件位于 `runtime/hub.json`，客户端必须通过该文件读取 `httpBaseUrl/wsUrl/tokenFile`，不得硬编码端口。

## 兼容性说明

- `error.message`、错误码、scope 语义、调用状态机以 `docs/Spec.md` 为准
- M1~M4 对外协议面保持稳定
- v1 已知限制见 `docs/DevHub协议与开发规划.md` 的“v1 已知限制”章节
