# DevHub Conformance 说明

本目录承载 DevHub Hub v1.0.1 的跨语言符合性向量与运行器。

- 对仓库开发者，它是官方 `.NET` / `JS/TS` / `Python` 适配器的统一回归入口。
- 对第三方接入方，它也是可公开挂接“自研 adapter”的官方自测入口。

## 1. 目标

conformance 的目标不是替代单元测试，而是从“第三方消费者可观察到的公开契约”出发，验证：

- Discovery、Auth、AppDef、AppInstance
- Invocation notify / request
- WebSocket events
- 标准 JSON-RPC 错误与 DevHub 自定义错误

这些向量与 [`docs/Spec.md`](../../docs/Spec.md) §10.1 / §10.2 对齐，是当前仓库公开发布的最小符合性基线。

## 2. 目录结构

```text
host/tests/conformance/
├── README.md
├── __init__.py
├── test_conformance_runner.py
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
- `adapters/`：仓库内官方适配器

## 3. 官方回归模式

默认执行：

```bash
python host/tests/conformance/vector_runner.py
```

该模式会同时运行仓库内三套官方适配器，因此需要先准备三侧环境：

```bash
dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
python -m pip install -e "./sdks/python[test]" requests
```

如需先验证 runner 自身的 manifest / 过滤 / 失败输出逻辑，可执行：

```bash
python -m unittest discover -s host/tests/conformance -p "test_conformance_runner.py"
```

常用变体：

- 只跑单条向量：`python host/tests/conformance/vector_runner.py --vector-id auth.valid_credentials_ping_success`
- 指定 suite：`python host/tests/conformance/vector_runner.py --suite v1.0.1`
- 指定目录：`python host/tests/conformance/vector_runner.py --directory host/tests/conformance/v1.0.1`
- 只跑指定官方适配器：`python host/tests/conformance/vector_runner.py --official-sdk python`

## 4. 第三方自测模式

如果第三方正在自研客户端、适配器或 Host 兼容层，可以通过 `--adapter-manifest` 把自己的 adapter 挂到官方 runner 上：

```bash
python host/tests/conformance/vector_runner.py --adapter-manifest path/to/devhub.adapter.json
```

当传入 `--adapter-manifest` 且未显式要求官方适配器时，runner 只执行 manifest 中声明的外部 adapter，不再要求先构建仓库内 `.NET` / `JS/TS` / `Python` 官方适配器。

如果想把第三方实现与官方适配器一起对照跑，可以额外传入：

```bash
python host/tests/conformance/vector_runner.py \
  --adapter-manifest path/to/devhub.adapter.json \
  --include-official-adapters \
  --official-sdk python
```

## 5. Manifest 契约

manifest 使用 JSON，当前只支持 `manifestVersion = 1`：

```json
{
  "manifestVersion": 1,
  "adapters": [
    {
      "name": "acme-client",
      "command": ["python", "tools/devhub_conformance_adapter.py"],
      "cwd": ".",
      "env": {
        "ACME_MODE": "release"
      }
    }
  ]
}
```

字段说明：

- `adapters[].name`：适配器名称，作为输出与失败快照中的稳定标识，必须全局唯一。
- `adapters[].command`：启动命令数组；runner 会在末尾自动追加 `execution-context.json` 路径。
- `adapters[].cwd`：可选工作目录；相对路径按 manifest 所在目录解析，缺省为 manifest 所在目录。
- `adapters[].env`：可选环境变量覆盖，仅对当前 adapter 进程生效。

runner 还会额外注入环境变量：

- `DEVHUB_CONFORMANCE_CONTEXT`：与追加到命令末尾的 `execution-context.json` 路径一致。

## 6. Adapter 输入输出契约

### 6.1 输入

adapter 启动后会收到一个 `execution-context.json` 路径。该文件至少包含：

```json
{
  "vector": { "...": "已解析占位符后的向量内容" },
  "dataDir": "/abs/path/to/vector-data-dir",
  "environmentDataDir": "/abs/path/to/vector-data-dir-or-null"
}
```

约束：

- `vector` 中的 `${HOST_*}` / `${VECTOR_*}` 占位符在 runner 调用 adapter 前都已解析完成。
- adapter 必须把 `dataDir/runtime/hub.json` 当作运行时发现入口，禁止硬编码端口或 URL。

### 6.2 输出

adapter 必须向标准输出打印一条 JSON 对象；runner 会读取最后一条可解析 JSON 作为结果。推荐输出形态：

```json
{
  "phase": "discovery",
  "outcome": "success",
  "actual": {
    "...": "..."
  },
  "error": null
}
```

补充约束：

- `sdk` 字段可省略；runner 会统一覆盖为 manifest 中的 `name`。
- 如 adapter 需要表达自身失败，应输出 `error` 对象；若直接非零退出且未输出合法 JSON，runner 会按进程失败处理。
- Invocation / Events / WS 场景可继续沿用仓库内官方适配器的输出字段约定，例如 `operation`、`phase=sdk-invocation`、`phase=sdk-events`、`phase=ws`。

## 7. 第三方最小闭环

如果你的目标是“无需阅读仓库内 SDK 源码，完成接入与自测”，最小步骤是：

1. 阅读 [`docs/Spec.md`](../../docs/Spec.md)、[`docs/schema/v1.0.1/README.md`](../../docs/schema/v1.0.1/README.md) 与 [`docs/protocol-examples/v1.0.1/README.md`](../../docs/protocol-examples/v1.0.1/README.md)。
2. 用自己的技术栈实现一个 adapter，读取 `execution-context.json` 并执行对应向量。
3. 编写 adapter manifest。
4. 运行 `python host/tests/conformance/vector_runner.py --adapter-manifest <manifest>`。

这样即可在不依赖仓库内 SDK 源码的前提下，直接复用官方向量与 runner 完成自测。

## 8. 输出解释

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
