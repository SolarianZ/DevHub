# DevHub Python SDK

DevHub Python SDK 基于 `docs/Spec.md` 中的 DevHub Hub v1.x 协议实现，覆盖运行时发现、HTTP JSON-RPC 与 WebSocket 事件订阅。

## 能力范围

- 运行时发现：读取 `hub.json` 与 `token.txt`，仅支持标准数据根目录布局（`<dataDir>/runtime/hub.json`）
- HTTP 客户端：`ping`、应用定义、实例管理、`launch`、`notify`、`request`、`poll`、`respond`
- WebSocket 事件客户端：鉴权、订阅、取消订阅、事件流读取
- 调用参数语义：可区分“省略 `args`”与“显式传入 `None`（序列化为 `null`）”
- 本地 JSON 校验：在发送前严格校验 `echo`、`meta`、`args`、`value`、`error.data`，拒绝 `NaN`、回调、循环引用等非法 JSON 结构
- 错误模型：统一映射为 `DevHubRpcException`，并提供 `DevHubRpcErrorCode`、`known_code`、`is_code(...)`、`reason`、`invocation_id`、`callee_error` 等辅助能力

## 安装

```bash
python3 -m pip install -e '.[test]'
```

## 快速示例

```python
from devhub_sdk import DevHubClient, DevHubClientOptions

client = DevHubClient.from_runtime(DevHubClientOptions(client_id="example-client"))
ping = client.ping({"hello": "world"})
print(ping.server_time_utc, ping.echo)
```

## 高级扩展

默认情况下，推荐继续使用 `DevHubClient.from_runtime(...)` 与 `DevHubEventsClient.from_runtime(...)`。

`data_dir` 参数与环境变量 `DEVHUB_DATA_DIR` 只接受数据根目录，SDK 固定从以下位置发现运行时信息：

- `<dataDir>/runtime/hub.json`
- `<dataDir>/runtime/token.txt`

不再支持：

- 直接传入 `runtime` 子目录
- 旧版 `DEVHUB_RUNTIME_DIR`
- `hub.json` / `token.txt` 直放在根目录的 legacy 布局

如果需要接入自定义运行时发现、fake transport、录制/回放测试或自定义 WebSocket 会话，也可以直接构造客户端并注入顶层公开导出的扩展抽象：

```python
from devhub_sdk import DevHubClient, DevHubClientOptions, JsonRpcHttpTransport, RuntimeResolver

client = DevHubClient(
    DevHubClientOptions(client_id="example-client"),
    runtime_resolver=my_runtime_resolver,  # RuntimeResolver
    transport=my_http_transport,           # JsonRpcHttpTransport
)
```

```python
from devhub_sdk import DevHubEventsClient, DevHubClientOptions, JsonRpcWsSession, RuntimeResolver

events_client = DevHubEventsClient(
    DevHubClientOptions(client_id="example-client"),
    runtime_resolver=my_runtime_resolver,  # RuntimeResolver
    session=my_ws_session,                 # JsonRpcWsSession
)
```

## 验证命令

```bash
python3 -m compileall src tests
python3 -m pytest tests/unit
python3 -m pytest tests/integration
```

## 本地 Smoke 前置说明

如果需要在仓库根目录执行针对 DevHub Host 的仓库级黑盒 smoke：

```bash
python3 src/tests/test_runner.py --smoke --no-header
```

请先在另一个终端启动本地 Hub：

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

原因：仓库级 `smoke` 默认针对“已启动的本地 Hub”执行；若本地 Hub 未启动，测试可能会读取到默认数据根目录中的历史残留 `hub.json`，从而出现 `Connection refused`。
