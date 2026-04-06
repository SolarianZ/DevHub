# 官方 SDK 接入入口

本分组用于承载官方 SDK 接入文档，面向通过 `.NET`、`JS/TS` 或 `Python` 连接 DevHub Host 的调用方。

## 语言路径

- [`.NET SDK`](../../../sdks/dotnet/README.md)：`.NET` 工作区、运行时发现、HTTP / WebSocket 用法与验证命令。
- [`JS/TS SDK`](../../../sdks/javascript/README.md)：Node.js 工作区、运行时发现、事件流与验证命令。
- [`Python SDK`](../../../sdks/python/README.md)：Python 工作区、运行时发现、事件流与验证命令。
- [`无 SDK 接入指南`](../无SDK接入指南.md)：不依赖仓库内 SDK 时的协议接入路径。

## 正式发布前占位

当正式 GitHub Release 中的 SDK 发布资产尚未确定时，语言相关安装说明统一使用显式 TODO 占位：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 <SDK 名称> 的发布资产名称、版本号与安装命令；当前阶段不要填写未发布的版本号、下载链接或仓库外安装命令。
```

当前各语言 README 中保留的仓库内命令仅用于本地开发、测试或本地打包验证，不代表正式发布安装入口。
