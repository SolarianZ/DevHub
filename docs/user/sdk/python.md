# DevHub Python SDK 接入指南

本文面向准备通过官方 `Python SDK` 连接 DevHub Host 的调用方，覆盖环境准备、运行时发现、常见调用方式、扩展点与最小验证方式。

## 1. 前置条件

- 已按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备 `Python 3.11+` 和 `pip`。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

仓库内最直接的获取方式是安装源代码工作区：

```bash
python -m pip install -e "./sdks/python[test]"
```

如需更贴近“发布资产消费”，可先准备 wheel 和 sdist：

```bash
python -m pip install build
python -m build --sdist --wheel --outdir temp/sdk-pack sdks/python
```

正式发布后的 distribution name 为 `devhub-sdk-python`，导入模块保持 `devhub_sdk`。

## 3. 能力概览

- 运行时发现：读取 `hub.json` 与 `token.txt`，仅支持标准数据根目录布局。
- HTTP 客户端：覆盖 `ping`、应用定义查询 / 校验 / 写入 / 删除、实例管理、`launch`、`notify`、`request`、`poll`、`respond`。
- WebSocket 事件客户端：覆盖鉴权、订阅、取消订阅、事件流读取与定义生命周期事件解析。
- 已放弃请求本地维护：`DevHubEventsClient` 提供 `get_abandoned_request_count(filter=None)` 与 `clear_abandoned_requests(filter=None)`，可按过滤器统计或清理本地已放弃请求记录。
- 共享参数构造：HTTP 与 WebSocket 对 `ping`、`get_definition`、`get_instance`、`list_instances` 复用同一套本地参数构造与防御式校验规则。
- 统一错误模型：`DevHubRpcException`，并提供 `DevHubRpcErrorCode`、`known_code`、`is_code(...)`、`reason`、`invocation_id`、`callee_error` 等辅助能力。

## 4. 运行时发现

`Python SDK` 会按以下顺序定位数据根目录：

1. `DevHubClientOptions.data_dir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据目录

SDK 固定从以下位置发现运行时信息：

- `<dataDir>/runtime/hub.json`
- `hub.json.tokenFile` 指向的令牌文件

`discover_runtime(...)` 返回的 `RuntimeConnectionInfo` 以及 `DevHubClient.runtime`、`DevHubEventsClient.runtime` 暴露的 `HubRuntime` 都是不可变 dataclass。公开运行时视图用于读取连接信息；字段赋值会触发 `FrozenInstanceError`，后续 HTTP / WebSocket 连接端点与令牌保持稳定。

不支持以下输入：

- 直接传入 `runtime` 子目录
- `hub.json` / `token.txt` 直放在数据根目录的布局

## 5. 快速开始

```python
from devhub_sdk import DevHubClient, DevHubClientOptions

client = DevHubClient.from_runtime(
    DevHubClientOptions(client_id="quickstart-python")
)

ping = client.ping({"hello": "world"})
print(ping.ok, ping.server_time_utc)
```

## 6. 常见交互场景

定义写接口由 `DevHubClient` 通过 HTTP 暴露；实例密码是独立方法参数，不进入 `AppInstanceRegistration`、`AppInstance` 或事件 payload。列表查询同样必须显式提供 `scope`；如需查询全部作用域，只在 `list_definitions` / `list_instances` 中传入 `None`。由 Host 启动的 App 可读取 `DEVHUB_LAUNCH_ID` 环境变量，并通过 `register_instance(..., launch_id=...)` 顶层参数回传启动绑定标识。

```python
from devhub_sdk import (
    AppDefinition,
    AppInstanceRegistration,
    DevHubClient,
    DevHubClientOptions,
    InvokeCapability,
    LaunchConfiguration,
)

client = DevHubClient.from_runtime(DevHubClientOptions(client_id="admin-client"))

definition = AppDefinition(
    app_id="sample.app",
    scope="",
    display_name="Sample App",
    launch=LaunchConfiguration(
        exe_path="python3",
        args_template="app.py",
    ),
)

validation = client.validate_definition(definition)
if validation.valid:
    client.upsert_definition(definition)

registered = client.register_instance(
    AppInstanceRegistration(
        instance_id="sample-inst-1",
        app_id="sample.app",
        scope="",
        pid=12345,
        invoke=InvokeCapability(poll=True, respond=True),
    ),
    password="sample-instance-secret",
)

exact_instance = client.get_instance("sample-inst-1")

client.unregister_instance(registered.instance_id, registered.instance_session_token)
client.delete_definition(definition.app_id, definition.scope)
```

`get_instance(...)` 按精确 `instance_id` 返回单个 `AppInstance` 快照；实例离线但仍保留时仍可读取，未命中则透传 `instance_not_found`。

内置 HTTP transport 与 WebSocket session 始终生成 string 类型的 JSON-RPC `id`。若自定义 transport / session 直接处理原始 JSON-RPC 信封，只应接受 string `id` 或处于 `Int64` 范围内的整数 numeric `id`；跨语言场景继续优先使用 string `id`。

`register_instance(...)` 的第一个参数固定为 `AppInstanceRegistration`。若已持有 `register_instance(...)` / `get_instance(...)` 返回的 `AppInstance`，应重新构造 `AppInstanceRegistration` 后再注册；返回的 `instance_session_token` 只用于 `heartbeat`、`unregister_instance`、`poll`、`respond` 等后续受保护调用。

## 7. WebSocket 事件流约定

```python
from devhub_sdk import APP_INSTANCE_REGISTERED, DevHubClientOptions, DevHubEventsClient

events_client = await DevHubEventsClient.from_runtime(
    DevHubClientOptions(client_id="events-client")
)

await events_client.authenticate()
subscription_id = await events_client.subscribe([APP_INSTANCE_REGISTERED])

reader = events_client.read_events()
try:
    event = await anext(reader)
    print(event.type, event.payload)
finally:
    await reader.aclose()

await events_client.unsubscribe(subscription_id)
```

`DevHubEventsClient` 同一时刻只允许一个活动中的 `read_events()` 读取器。若业务需要多个消费者，应在调用方内部对读取到的事件做扇出。

底层 WebSocket 终止时，当前活动读取器只排空终止前已经进入本地缓冲的事件，然后结束。后续新的读取前需要重新执行 `authenticate()`，并重新执行 `subscribe()` 恢复订阅；旧订阅不会自动恢复。

## 8. 已放弃请求维护

```python
from devhub_sdk import AbandonedRequestFilter

total = events_client.get_abandoned_request_count()

app_scoped = events_client.get_abandoned_request_count(
    AbandonedRequestFilter(
        app_id="sample.app",
        method="hub.apps.getDefinition",
    )
)

removed = events_client.clear_abandoned_requests(
    AbandonedRequestFilter(older_than_seconds=120)
)
```

- `get_abandoned_request_count(filter=None)` 返回当前匹配过滤条件的已放弃请求数量。
- `clear_abandoned_requests(filter=None)` 只移除匹配条件的本地记录，并返回本次实际移除数量。
- `AbandonedRequestFilter` 支持 `older_than_seconds`、`app_id`、`method` 三个可选字段；同时提供多个字段时按逻辑与匹配。
- 两个接口都只读取或修改当前 `DevHubEventsClient` 关联 WebSocket 会话中的本地 tombstone 记录，不发送 JSON-RPC 请求，不隐式重连，也不改变当前认证或订阅状态。
- `app_id` 匹配采用最佳努力规则：只有请求进入已放弃状态时能稳定识别 `app_id` 的记录才会命中 `app_id` 过滤条件。
- 某条记录被手动清理后，如果服务端随后返回同一 `id` 的迟到响应，该响应会回到既有 unknown `response id` 故障语义，而不是继续被忽略。

## 9. 高级扩展

默认情况下，推荐使用 `DevHubClient.from_runtime(...)` 与 `DevHubEventsClient.from_runtime(...)`。

如果需要接入自定义运行时发现、fake transport、录制 / 回放测试或自定义 WebSocket 会话，请统一通过 `from_runtime(..., dependencies=...)` 注入公开扩展点：

```python
from devhub_sdk import DevHubClient, DevHubClientDependencies, DevHubClientOptions

client = DevHubClient.from_runtime(
    DevHubClientOptions(client_id="example-client"),
    DevHubClientDependencies(
        runtime_resolver=my_runtime_resolver,
        transport_factory=my_http_transport_factory,
    ),
)
```

```python
from devhub_sdk import DevHubClientOptions, DevHubEventsClient, DevHubEventsClientDependencies

events_client = await DevHubEventsClient.from_runtime(
    DevHubClientOptions(client_id="example-client"),
    DevHubEventsClientDependencies(
        runtime_resolver=my_runtime_resolver,
        session_factory=my_ws_session_factory,
    ),
)
```

其中：

- `runtime_resolver` 负责把 `DevHubClientOptions` 解析成 `RuntimeConnectionInfo`。
- `transport_factory` 负责基于 `options + connection_info` 创建 HTTP transport。
- `session_factory` 负责基于 `options + connection_info` 创建 WebSocket session。
- `DevHubEventsClient` 负责把原始 `hub.event.params` 解析为 `DevHubEvent`，并复用与 HTTP 客户端相同的参数 builder。

自定义 `JsonRpcWsSession` / `WebSocketJsonRpcSession` 实现需要提供 `get_abandoned_request_count(filter=None)` 与 `clear_abandoned_requests(filter=None)` 两个同步本地维护接口。这组接口属于当前公开 session 合同的一部分。

## 10. 最小验证方式

- 直接运行上面的 `client.ping(...)` 示例，确认返回 `ok=True`。
- 若要验证 `Python SDK` 工作区自身的测试基线，可执行：

```bash
python -m pytest sdks/python/tests
```

- 若要验证 wheel / sdist 是否可生成，可执行：

```bash
python -m pip install build
python -m build --sdist --wheel --outdir temp/sdk-pack sdks/python
```

- 若要查看工作区安装、集成测试隔离或仓库级联调要求，请阅读 [`../../developer/guides/development.md`](../../developer/guides/development.md)。

## 11. 相关文档

- [`./README.md`](./README.md)
- [`../../../sdks/python/README.md`](../../../sdks/python/README.md)
- [`../host/quickstart.md`](../host/quickstart.md)
- [`../protocol/README.md`](../protocol/README.md)
- [`../../developer/guides/development.md`](../../developer/guides/development.md)
- [`../../developer/publishing/README.md`](../../developer/publishing/README.md)
