# DevHub Conformance 说明

本目录承载 DevHub Hub v1.0.1 的跨语言符合性向量与运行器，用于验证 `.NET`、`JS/TS`、`Python` 三侧对同一协议向量是否给出语义一致的结果。

## 1. 目标

conformance 的目标不是替代单元测试，而是从“第三方消费者可观察到的公开契约”出发，验证：

- Discovery、Auth、AppDef、AppInstance
- Invocation notify / request
- WebSocket events
- 标准 JSON-RPC 错误与 DevHub 自定义错误

这些向量与 [`docs/Spec.md`](../../docs/Spec.md) §10.1 / §10.2 对齐，是仓库内公开的最小符合性基线。

## 2. 目录结构

```text
host/tests/conformance/
├── README.md
├── vector_runner.py
├── vector_setup.py
├── raw_protocol_helper.py
├── adapters/
│   ├── devhub_conformance_js.mjs
│   └── devhub_conformance_py.py
└── v1.0.1/
    └── *.json
```

说明：

- `v1.0.1/*.json`：签名向量文件
- `vector_runner.py`：统一运行器
- `raw_protocol_helper.py`：中立原始协议编排 helper
- `adapters/`：语言适配器

## 3. 运行前提

运行前需要先准备三侧执行环境：

```bash
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
python -m pip install -e "./sdks/python[test]" requests
```

其中：

- `.NET` 侧需要先产出 conformance 适配器 DLL
- `JS/TS` 侧需要先产出 `sdks/javascript/dist/index.js`
- `Python` 侧需要安装测试依赖与 `requests`

## 4. 常用命令

### 4.1 运行默认 suite

```bash
python host/tests/conformance/vector_runner.py
```

### 4.2 指定 suite 名称

```bash
python host/tests/conformance/vector_runner.py --suite v1.0.1
```

### 4.3 只运行单条向量

```bash
python host/tests/conformance/vector_runner.py --vector-id auth.valid_credentials_ping_success
```

### 4.4 指定向量目录

```bash
python host/tests/conformance/vector_runner.py --directory host/tests/conformance/v1.0.1
```

## 5. 输出解释

通过时会输出：

```text
PASS  auth.valid_credentials_ping_success
...
SUMMARY  total=52 passed=52 failed=0
```

失败时运行器会输出统一失败信息，至少包含：

- `vectorId`
- `sdk`
- `expected`
- `actual`
- `diffFields`
- `message`

并会把失败快照写入：

```text
temp/conformance_snapshots/<timestamp>/<vector-id>/<sdk>/
```

常见文件包括：

- `failure.json`
- `resolved-vector.json`
- `suite-host.log`
- `adapter.stdout.txt`
- `adapter.stderr.txt`

## 6. 与第三方自测的关系

如果第三方正在自研客户端、适配器或 Host 兼容层，可以把本目录视为“公开自测入口”：

- Schema 负责描述静态结构
- 原始协议示例负责说明报文形态
- conformance 负责验证实现是否满足 Spec §10 的最小基线

换句话说，第三方无需阅读仓库内 SDK 源码，也可以仅依赖：

- [`docs/Spec.md`](../../docs/Spec.md)
- [`docs/schema/v1.0.1/README.md`](../../docs/schema/v1.0.1/README.md)
- [`docs/protocol-examples/v1.0.1/README.md`](../../docs/protocol-examples/v1.0.1/README.md)
- 本 README

来完成接入与自测。
