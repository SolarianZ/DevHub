# DevHub Python SDK 接入指南

本文面向准备通过官方 `Python SDK` 连接 DevHub Host 的调用方，覆盖环境准备、连接 Host、最小示例和验证方式。

## 1. 前置条件

- 已按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备 `Python 3.11+` 和 `pip`。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

仓库内最直接的获取方式是安装源码工作区：

```bash
python -m pip install -e "./sdks/python[test]"
```

如果你希望更贴近“发布资产消费”，可先准备 wheel 和 sdist：

```bash
python -m pip install build
python -m build --sdist --wheel --outdir temp/sdk-pack sdks/python
```

正式发布后的 distribution name 为 `devhub-sdk-python`，wheel、sdist 与 manifest 命名会与 [`../../developer/publishing/release-asset-layout.md`](../../developer/publishing/release-asset-layout.md) 保持一致；导入模块仍保持 `devhub_sdk`。

## 3. 连接 Host

`Python SDK` 会按以下顺序定位数据根目录：

1. `DevHubClientOptions.data_dir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据目录

最小示例：

```python
from devhub_sdk import DevHubClient, DevHubClientOptions

client = DevHubClient.from_runtime(
    DevHubClientOptions(client_id="quickstart-python")
)

ping = client.ping({"hello": "world"})
print(ping.ok, ping.server_time_utc)
```

## 4. 最小验证方式

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

## 5. 相关文档

- 需要完整 API、错误模型和扩展点说明时，请阅读 [`../../../sdks/python/README.md`](../../../sdks/python/README.md)。
- 需要对照其他语言 SDK，请回到 [`README.md`](./README.md)。
- 如果你计划直接基于原始协议接入，请切换到 [`../protocol/README.md`](../protocol/README.md)。
