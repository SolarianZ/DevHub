# DevHub 仓库级测试说明

- 仓库级测试以 `docs/spec/Spec.md` 为最高优先级规范。
- 这部分资产覆盖当前仓库维护的公开契约与验证入口，不预先固化仓库中尚未落地的额外能力。

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
├── conformance/                    # 符合性向量、runner 与自测
│   ├── __init__.py
│   ├── test_conformance_runner.py
│   ├── vector_runner.py
│   ├── vector_setup.py
│   ├── raw_protocol_helper.py
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
- 官方 conformance 适配器按语言归属放在各自 SDK 的 `tests/` 目录下，`host/tests/conformance` 只保留仓库级向量、runner 与自测。
- `whitebox/` 承载 Host 工作区的 .NET 白盒测试工程，避免继续与 `host/src/` 生产工程混放。
- `tools/` 只放验证入口和辅助脚本，不混入黑盒或 conformance 用例。

## 前置要求

- .NET SDK 10.0+
- Python 3.11+
- Python 依赖：`pip install requests`
- Windows ACL 语义校验依赖：`pip install pywin32`

## 运行方式

### 1) 运行黑盒测试

`host/tests/blackbox/test_runner.py` 默认会先构建一次仓库内 `DevHub.Host`，再启动一个 runner 级隔离 Host，并把整个黑盒测试过程固定到该隔离 `DEVHUB_DATA_DIR`；测试完成后会自动结束这个临时 Host。

如需关闭默认预构建，可使用：

- `--no-build-host`
- `DEVHUB_TEST_BUILD_HOST=0`

如需改为复用外部已启动的 Host，可使用：

- `--use-existing-host`
- `DEVHUB_TEST_USE_EXISTING_HOST=1`

此时 runner 不再自启 Host，而是直接连接当前 `DEVHUB_DATA_DIR` 或平台默认数据目录对应的运行时。

#### 可选：为隔离 Hub 用例配置启动夹具

`host/tests/blackbox/test_launch_discovery.py` 中涉及原子写入与自定义数据根目录的用例，会通过统一测试夹具启动额外的隔离 Hub 进程。默认情况下，夹具会沿用仓库内 Host 的默认启动方式；如需改由外部 harness 或自定义包装脚本负责拉起进程，可通过下列参数或同名环境变量注入：

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

复用外部 Host：

```bash
dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release --no-build --no-launch-profile
python3 host/tests/blackbox/test_runner.py --use-existing-host --no-build-host
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

### 2) 运行 conformance

仓库级符合性向量：

```bash
python host/tests/conformance/vector_runner.py
```

conformance runner 自测：

```bash
python -m unittest discover -s host/tests/conformance -p "test_conformance_runner.py"
```

### 3) 覆盖率配置与校验

```bash
find host -type d -name TestResults -prune -exec rm -rf {} +
dotnet test host/DevHub.slnx -c Release --no-build --collect:"XPlat Code Coverage" --settings host/tests/tools/coverage.runsettings --logger "trx;LogFileName=unit-tests.trx" --verbosity normal
python host/tests/tools/verify_coverage.py --root . --line-threshold 0.80 --branch-threshold 0.80
```

补充说明：

- Host 传输层、parser 与 RPC handler 的新增白盒测试，断言必须以 [`docs/spec/Spec.md`](../../docs/spec/Spec.md) 定义的公开 JSON-RPC 结果、错误码、错误数据和可观察状态为依据，不依赖私有 helper 调用顺序或日志文本。
- `find host -type d -name TestResults -prune -exec rm -rf {} +` 与 GitHub CI 保持一致，用于清理历史 `TestResults`，避免旧的 coverage 报告混入当前校验。
- 启用 `trx` logger 时，Coverlet 会同时生成 `TestResults/_*/In/**/coverage.cobertura.xml` 附件副本和 GUID 目录下的镜像副本。
- `host/tests/tools/verify_coverage.py` 会优先使用 `trx` 附件副本参与阈值计算，并忽略同一测试工程下内容完全相同的 GUID 镜像副本；这样既保留 `trx`/诊断附件所需文件，又避免重复报告干扰 coverage 口径。

## 报告输出

- 文本报告：`temp/test_results.txt`
- JSON 报告：`temp/test_results.json`
- 日志文件：`temp/test_log.txt`

报告中会标注：

- `mode`: `default` / `smoke` / `fast` / `full`
- `coverage`: 覆盖级别描述
