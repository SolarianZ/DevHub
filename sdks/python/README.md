# DevHub Python SDK

DevHub Python SDK 基于 `docs/Spec.md` 中的 DevHub Hub v1.x 协议实现，覆盖运行时发现、HTTP JSON-RPC 与 WebSocket 事件订阅。

## 能力范围

- 运行时发现：读取 `hub.json` 与 `token.txt`
- HTTP 客户端：`ping`、应用定义、实例管理、`launch`、`notify`、`request`、`poll`、`respond`
- WebSocket 事件客户端：鉴权、订阅、取消订阅、事件流读取
- 错误模型：统一映射为 `DevHubRpcException`

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

## 验证命令

```bash
python3 -m compileall src tests
python3 -m pytest tests/unit
python3 -m pytest tests/integration
```

## 本地 Smoke 前置说明

如果需要在仓库根目录执行黑盒 smoke：

```bash
python3 tests/test_runner.py --smoke --no-header
```

请先在另一个终端启动本地 Hub：

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

原因：仓库级 `smoke` 默认针对“已启动的本地 Hub”执行；若本地 Hub 未启动，测试可能会读取到默认运行时目录中历史残留的 `hub.json`，从而出现 `Connection refused`。
