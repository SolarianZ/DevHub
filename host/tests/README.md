# DevHub 仓库级测试说明

- 仓库级测试以 `docs/spec/Spec.md` 为最高优先级规范。
- 这部分资产只覆盖当前分支所属里程碑，不应提前绑定未来里程碑行为。

## 目录分层

```text
host/tests/
├── README.md
├── __init__.py
├── assets/                         # 仓库级共享测试夹具
├── blackbox/                       # Python 黑盒测试与统一 runner
│   ├── __init__.py
│   ├── test_base.py
│   ├── test_runner.py
│   └── test_*.py
├── conformance/                    # 符合性向量、adapter、runner 与自测
│   ├── __init__.py
│   ├── test_conformance_runner.py
│   ├── vector_runner.py
│   ├── vector_setup.py
│   ├── raw_protocol_helper.py
│   ├── adapters/
│   └── v1.0.1/
├── whitebox/                       # .NET 白盒测试工程
│   ├── DevHub.Tests/
│   └── DevHub.Host.Tests/
└── tools/                          # 覆盖率配置与仓库级辅助脚本
    ├── coverage.runsettings
    ├── verify_coverage.py
    ├── check_text_encoding.py
    └── build_run_hub.sh
```

补充说明：

- `assets/` 保留跨工作区共享夹具，例如 `launch_noop.py` 会同时被黑盒测试与 SDK 集成测试使用。
- `blackbox/` 只承载面向公开行为的仓库级黑盒测试；具体用例不应再依赖 `host/tests/` 顶层旧布局。
- `conformance/` 负责跨语言协议符合性，不替代白盒测试或黑盒业务回归。
- `whitebox/` 承载 Host 工作区的 .NET 白盒测试工程，避免继续与 `host/src/` 生产工程混放。
- `tools/` 只放验证入口和辅助脚本，不混入黑盒或 conformance 用例。

## 前置要求

- .NET SDK 10.0+
- Python 3.11+
- Python 依赖：`pip install requests`
- Windows ACL 语义校验依赖：`pip install pywin32`

## 运行方式

### 1) 启动 DevHub Host

```bash
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release
```

### 2) 运行黑盒测试

#### 可选：为隔离 Hub 用例配置启动夹具

`host/tests/blackbox/test_launch_discovery.py` 中涉及原子写入与自定义数据根目录的用例，会通过统一测试夹具启动隔离 Hub 进程。默认情况下，夹具会回退到仓库内的 Host 启动命令；如需改由外部 harness 或自定义包装脚本负责拉起进程，可通过下列参数或同名环境变量注入：

- `--isolated-hub-command` / `DEVHUB_TEST_HUB_COMMAND`：隔离 Hub 启动命令，支持 shell 字符串或 JSON 数组。
- `--isolated-hub-cwd` / `DEVHUB_TEST_HUB_CWD`：隔离 Hub 启动命令的工作目录。
- `--isolated-hub-env-json` / `DEVHUB_TEST_HUB_ENV_JSON`：额外环境变量覆盖，值为 JSON 对象。

说明：Windows 上若临时目录同时出现 8.3 短路径与长路径表示，启动与发现夹具会按“同一文件位置”而非字符串字面值进行比较，避免 `DEVHUB_DATA_DIR` 用例出现误报。

#### Default

```bash
python3 host/tests/blackbox/test_runner.py
```

示例：

```bash
python3 host/tests/blackbox/test_runner.py --isolated-hub-command "dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release --no-build --no-launch-profile"
```

#### Fast

```bash
python3 host/tests/blackbox/test_runner.py --fast
```

#### Full

```bash
python3 host/tests/blackbox/test_runner.py --full
```

#### Smoke

```bash
python3 host/tests/blackbox/test_runner.py --smoke
```

### 3) 运行 conformance

仓库级符合性向量：

```bash
python host/tests/conformance/vector_runner.py
```

conformance runner 自测：

```bash
python -m unittest discover -s host/tests/conformance -p "test_conformance_runner.py"
```

### 4) 覆盖率配置与校验

```bash
dotnet test host/DevHub.slnx -c Release --collect:"XPlat Code Coverage" --settings host/tests/tools/coverage.runsettings
python host/tests/tools/verify_coverage.py --root . --line-threshold 0.80 --branch-threshold 0.80
```

## 报告输出

- 文本报告：`temp/test_results.txt`
- JSON 报告：`temp/test_results.json`
- 日志文件：`temp/test_log.txt`

报告中会标注：

- `mode`: `default` / `smoke` / `fast` / `full`
- `coverage`: 覆盖级别描述
