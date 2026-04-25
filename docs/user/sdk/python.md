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
- 共享参数构造：HTTP 与 WebSocket 对 `ping`、`get_definition`、`list_instances` 复用同一套本地参数构造与防御式校验规则。
- 统一错误模型：`DevHubRpcException`，并提供 `DevHubRpcErrorCode`、`known_code`、`is_code(...)`、`reason`、`invocation_id`、`callee_error` 等辅助能力。

## 4. 运行时发现

`Python SDK` 会按以下顺序定位数据根目录：

1. `DevHubClientOptions.data_dir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据目录

SDK 固定从以下位置发现运行时信息：

- `<dataDir>/runtime/hub.json`
- `hub.json.tokenFile` 指向的令牌文件

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

定义写接口由 `DevHubClient` 通过 HTTP 暴露；实例密码是独立方法参数，不进入 `AppInstanceRegistration`、`AppInstance` 或事件 payload。列表查询同样必须显式提供 `scope`；如需查询全部作用域，只在 `list_definitions` / `list_instances` 中传入 `None`。

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

instance = client.register_instance(
    AppInstanceRegistration(
        instance_id="sample-inst-1",
        app_id="sample.app",
        scope="",
        pid=12345,
        invoke=InvokeCapability(poll=True, respond=True),
    ),
    password="sample-instance-secret",
)

client.unregister_instance(instance.instance_id, "sample-instance-secret")
client.delete_definition(definition.app_id, definition.scope)
```

## 7. 高级扩展

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

## 8. 最小验证方式

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

## 9. 相关文档

- [`./README.md`](./README.md)
- [`../../../sdks/python/README.md`](../../../sdks/python/README.md)
- [`../host/quickstart.md`](../host/quickstart.md)
- [`../protocol/README.md`](../protocol/README.md)
- [`../../developer/guides/development.md`](../../developer/guides/development.md)
- [`../../developer/publishing/README.md`](../../developer/publishing/README.md)
