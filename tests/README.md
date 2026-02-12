# DevHub Python 集成测试说明（M1~M4 四模式）

- 测试以 `docs/Spec.md` 为最高优先级规范。
- 测试只覆盖应当前里程碑（参考当前Git分支）的内容，不应覆盖未来里程碑的内容。

## 前置要求

- .NET SDK 10.0+
- Python 3.9+
- Python 依赖：`pip install requests`
- Windows ACL 严格校验依赖：`pip install pywin32`

## 运行方式

### 1) 启动 DevHub Host

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

### 2) 运行 Python 集成测试

#### Default 模式

```bash
python3 tests/test_runner.py
```

#### Fast 模式

在默认模式基础上，跳过超时测试，更快反馈。

```bash
python3 tests/test_runner.py --fast
```

#### Full 模式

严格覆盖功能，在默认模式基础上，增加压力测试等，含耗时场景。

```bash
python3 tests/test_runner.py --full
```

#### Smoke 模式

跨平台最小冒烟回归，覆盖发现/鉴权/WS/Request 主链路，推荐用于 CI 三平台快速门禁。

```bash
python3 tests/test_runner.py --smoke
```

## 报告输出

- 文本报告：`temp/test_results.txt`
- JSON 报告：`temp/test_results.json`
- 日志文件：`temp/test_log.txt`

报告中会标注：

- `mode`: `default` / `smoke` / `fast` / `full`
- `coverage`: 覆盖级别描述
