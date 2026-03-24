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

不支持以下输入：

- 直接传入 `runtime` 子目录
- `hub.json` / `token.txt` 直放在数据根目录的布局

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

公开事件类型模型使用 `DevHubEventType` 闭集，并同步导出 `SUPPORTED_EVENT_TYPES`、`ALL_EVENT_TYPES` 与 `ensure_supported_event_type(...)`，便于在调用侧提前完成订阅入参校验。

## 集成测试隔离模式

`python3 -m pytest tests/integration` 运行的是 Python SDK 自己的集成测试。这组测试会为每个用例自启独立临时 Host，并为该 Host 分配独立临时 `DEVHUB_DATA_DIR`；测试结束后会关闭 Host 并回收其派生进程树。

这意味着：

- SDK 集成测试不会连接开发机默认数据根目录下的常驻 Hub。
- 同一台机器可以并行运行 Python / JavaScript / .NET SDK 的集成测试，因为每套测试都使用各自独立的临时 Host 与数据根目录。
- 如果你要验证 SDK 集成测试，请直接运行 `pytest tests/integration`，不要先手工启动本地 Hub。

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

原因：仓库级 `smoke` 默认针对“已启动的本地 Hub”执行；这和上面的 SDK 集成测试模式不同。若本地 Hub 未启动，测试可能会读取到默认数据根目录中的历史残留 `hub.json`，从而出现 `Connection refused`。
