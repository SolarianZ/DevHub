# 官方 SDK 接入入口

本分组用于承载官方 SDK 接入文档，面向通过 `.NET`、`JS/TS` 或 `Python` 连接 DevHub Host 的调用方。

## 语言路径

- [`.NET SDK 接入指南`](./dotnet.md)：环境准备、连接 Host、最小 `PingAsync()` 示例与验证方式。
- [`JS/TS SDK 接入指南`](./javascript.md)：Node.js 环境准备、运行时发现、最小 `client.ping()` 示例与验证方式。
- [`Python SDK 接入指南`](./python.md)：Python 环境准备、运行时发现、最小 `client.ping()` 示例与验证方式。
- [`.NET SDK README`](../../../sdks/dotnet/README.md)：`.NET SDK` 工作区、完整 API、测试与本地打包说明。
- [`JS/TS SDK README`](../../../sdks/javascript/README.md)：`JS/TS SDK` 工作区、完整 API、测试与本地打包说明。
- [`Python SDK README`](../../../sdks/python/README.md)：`Python SDK` 工作区、完整 API、测试与本地打包说明。
- [`Host 快速上手`](../getting-started/host-quickstart.md)：启动 Host、读取 `hub.json` 和最小验证入口。
- [`无 SDK 接入指南`](../无SDK接入指南.md)：不依赖仓库内 SDK 时的协议接入路径。

## 正式发布前占位

当正式 GitHub Release 中的 SDK 发布资产尚未确定时，语言相关安装说明统一使用显式 TODO 占位：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 <SDK 名称> 的发布资产名称、版本号与安装命令；当前阶段不要填写未发布的版本号、下载链接或仓库外安装命令。
```

当前各语言 README 中保留的仓库内命令仅用于本地开发、测试或本地打包验证，不代表正式发布安装入口。

## 接入闭环

推荐按以下顺序完成接入：

1. 先按 [`../getting-started/host-quickstart.md`](../getting-started/host-quickstart.md) 启动 Host 并确认 `hub.ping` 成功。
2. 再按对应语言文档准备环境、创建客户端并读取 `<dataDir>/runtime/hub.json`。
3. 需要深入了解 API、测试命令或扩展点时，再进入对应工作区 README。
4. 如果最终决定不依赖官方 SDK，可切换到 [`../无SDK接入指南.md`](../无SDK接入指南.md)。
