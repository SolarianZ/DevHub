# DevHub Python 集成测试说明（M1~M4 四模式）

- 测试以 `docs/Spec.md` 为最高优先级规范。
- 测试只覆盖应当前里程碑（参考当前Git分支）的内容，不应覆盖未来里程碑的内容。

## 前置要求

- .NET SDK 10.0+
- Python 3.9+
- Python 依赖：`pip install requests`
- Windows ACL 语义校验依赖：`pip install pywin32`

## 运行方式

### 1) 启动 DevHub Host

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

### 2) 运行 Python 集成测试

#### 可选：为隔离 Hub 用例配置启动夹具

`test_launch_discovery.py` 中涉及原子写入与自定义数据根目录的用例，会通过统一测试夹具启动隔离 Hub 进程。默认情况下，夹具会回退到仓库内的 Host 启动命令；如需改由外部 harness 或自定义包装脚本负责拉起进程，可通过下列参数或同名环境变量注入：

- `--isolated-hub-command` / `DEVHUB_TEST_HUB_COMMAND`：隔离 Hub 启动命令，支持 shell 字符串或 JSON 数组。
- `--isolated-hub-cwd` / `DEVHUB_TEST_HUB_CWD`：隔离 Hub 启动命令的工作目录。
- `--isolated-hub-env-json` / `DEVHUB_TEST_HUB_ENV_JSON`：额外环境变量覆盖，值为 JSON 对象。

说明：Windows 上若临时目录同时出现 8.3 短路径与长路径表示，启动与发现夹具会按“同一文件位置”而非字符串字面值进行比较，避免 `DEVHUB_DATA_DIR` 用例出现误报。

如必须通过实现专用环境变量启动隔离实例，应在上述夹具配置中注入，而不是在具体测试用例中写死。并行或隔离场景下，必须为每个 Host 分配独立 `DEVHUB_DATA_DIR`。

#### Default 模式

```bash
python3 src/tests/test_runner.py
```

示例：

```bash
python3 src/tests/test_runner.py --isolated-hub-command "dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release --no-build --no-launch-profile"
```

#### Fast 模式

在默认模式基础上，跳过超时测试，更快反馈。

```bash
python3 src/tests/test_runner.py --fast
```

#### Full 模式

严格覆盖功能，在默认模式基础上，增加压力测试等，含耗时场景。

```bash
python3 src/tests/test_runner.py --full
```

#### Smoke 模式

跨平台最小冒烟回归，覆盖发现/鉴权/WS/Request 主链路，推荐用于 CI 三平台快速门禁。

```bash
python3 src/tests/test_runner.py --smoke
```

## 报告输出

- 文本报告：`temp/test_results.txt`
- JSON 报告：`temp/test_results.json`
- 日志文件：`temp/test_log.txt`

报告中会标注：

- `mode`: `default` / `smoke` / `fast` / `full`
- `coverage`: 覆盖级别描述
