# 官方 SDK 接入入口

本分组面向通过官方 `.NET`、`JS/TS` 或 `Python` SDK 连接 DevHub Host 的调用方。

## 语言路径

- [`.NET SDK 接入指南`](./dotnet.md)：环境准备、运行时发现、常见调用场景、扩展点与最小验证方式。
- [`JS/TS SDK 接入指南`](./javascript.md)：双运行时入口、运行时发现、常见调用场景、扩展点与最小验证方式。
- [`Python SDK 接入指南`](./python.md)：环境准备、运行时发现、常见调用场景、扩展点与最小验证方式。
- [`.NET SDK README`](../../../sdks/dotnet/README.md)：`.NET SDK` 工作区概述、内容结构与重要注意事项。
- [`JS/TS SDK README`](../../../sdks/javascript/README.md)：`JS/TS SDK` 工作区概述、内容结构与重要注意事项。
- [`Python SDK README`](../../../sdks/python/README.md)：`Python SDK` 工作区概述、内容结构与重要注意事项。
- [`Host 快速上手`](../host/quickstart.md)：启动 Host、读取 `hub.json` 与最小连通性验证。
- [`原始协议接入指南`](../protocol/README.md)：不依赖官方 SDK 的原始协议接入路径。
- [`开发指南`](../../developer/guides/development.md)：工作区构建、测试、本地打包与仓库级验证入口。
- [`使用文档总入口`](../README.md)：返回使用文档入口。

## 发布资产占位

当语言相关安装资产尚未在当前分发渠道提供时，请统一沿用 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范。

各语言 README 只保留工作区概述、内容结构与重要注意事项；完整接入说明见对应语言指南。仓库内命令仅用于本地开发、测试或本地打包验证，不代表正式发布安装入口。

## 接入闭环

1. 先按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.ping` 成功。
2. 再按对应语言指南准备环境、读取 `<dataDir>/runtime/hub.json` 并建立客户端连接。
3. 需要查看工作区结构与语言入口注意事项时，再回看对应语言的工作区 README。
4. 需要查看工作区构建、测试、本地打包或集成测试隔离规则时，进入 [`../../developer/guides/development.md`](../../developer/guides/development.md)。
5. 如果最终决定不依赖官方 SDK，可切换到 [`../protocol/README.md`](../protocol/README.md)。
