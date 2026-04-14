# 官方 SDK 接入入口

本分组用于承载官方 SDK 接入文档，面向通过 `.NET`、`JS/TS` 或 `Python` 连接 DevHub Host 的调用方。

## 语言路径

- [`.NET SDK 接入指南`](./dotnet.md)：环境准备、连接 Host、最小 `PingAsync()` 示例与验证方式。
- [`JS/TS SDK 接入指南`](./javascript.md)：`Node.js 20+` 与浏览器 / WebView 双运行时入口、运行时发现与最小 `client.ping()` 示例。
- [`Python SDK 接入指南`](./python.md)：Python 环境准备、运行时发现、最小 `client.ping()` 示例与验证方式。
- [`.NET SDK README`](../../../sdks/dotnet/README.md)：`.NET SDK` 工作区、完整 API、测试与本地打包说明。
- [`JS/TS SDK README`](../../../sdks/javascript/README.md)：`JS/TS SDK` 工作区、完整 API、测试与本地打包说明。
- [`Python SDK README`](../../../sdks/python/README.md)：`Python SDK` 工作区、完整 API、测试与本地打包说明。
- [`Host 快速上手`](../host/quickstart.md)：启动 Host、读取 `hub.json` 和最小验证入口。
- [`原始协议接入指南`](../protocol/README.md)：不依赖仓库内 SDK 时的协议接入路径。
- [`使用文档总入口`](../README.md)：返回使用文档分类入口。

## 发布资产占位

当语言相关安装资产尚未在当前分发渠道提供时，请统一沿用 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范。

各语言 README 中保留的仓库内命令仅用于本地开发、测试或本地打包验证，不代表正式发布安装入口。

## 接入闭环

推荐按以下顺序完成接入：

1. 先按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host 并确认 `hub.ping` 成功。
2. 再按对应语言文档准备环境、创建客户端并读取 `<dataDir>/runtime/hub.json`；其中 `JS/TS SDK` 根入口面向双运行时，Node.js 文件系统发现辅助位于 `@devhub/sdk-javascript/runtime`。
3. 需要深入了解 API、测试命令或扩展点时，再进入对应工作区 README。
4. 如果最终决定不依赖官方 SDK，可切换到 [`../protocol/README.md`](../protocol/README.md)。
