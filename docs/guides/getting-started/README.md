# DevHub Host 上手入口

本分组用于承载首次使用 DevHub Host 的文档，面向需要完成环境准备、启动 Host、读取运行时发现文件并做最小调用验证的外部用户。

## 当前入口

- [`host-quickstart.md`](./host-quickstart.md)：Host 快速上手、运行时发现、最小 `hub.ping` 验证与后续路径导航。
- [`../../operations/部署与运行指南.md`](../../operations/部署与运行指南.md)：部署、发布、运行时数据目录和上线后检查项。
- [`../../../apps/monitor/README.md`](../../../apps/monitor/README.md)：桌面 GUI 方式查看 Host 状态、定义、实例与日志。
- [`../../spec/Spec.md`](../../spec/Spec.md)：`hub.json`、`tokenFile` 与公开协议行为的权威来源。
- [`../sdk/README.md`](../sdk/README.md)：官方 SDK 接入入口。
- [`../无SDK接入指南.md`](../无SDK接入指南.md)：不依赖官方 SDK 的原始协议接入路径。
- [`../../README.md`](../../README.md)：文档总入口。

## 发布资产占位

当 Host 下载入口、平台资产名称或安装命令尚未在当前分发渠道提供时，请统一沿用 [`../../operations/publishing/README.md`](../../operations/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范。

后续所有 Host 上手文档都以本分组为稳定入口，并与发布文档中的资产命名、manifest 和检查清单保持同步。
