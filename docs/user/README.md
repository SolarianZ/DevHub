# DevHub 使用文档

本分类面向使用 DevHub Host、官方 SDK 或原始协议的调用方，聚焦“如何启动、如何连接、如何验证最小链路”。

## 起点导航

- [`host/README.md`](./host/README.md)：首次使用 Host 的入口导航与最小上手路径。
- [`host/quickstart.md`](./host/quickstart.md)：启动 Host、读取 `hub.json` / `tokenFile` 与最小 `hub.ping` 验证。
- [`sdk/README.md`](./sdk/README.md)：官方 `.NET`、`JS/TS`、`Python` SDK 接入入口。
- [`protocol/README.md`](./protocol/README.md)：不依赖官方 SDK、直接基于公开协议接入的路径。
- [`../../apps/monitor/README.md`](../../apps/monitor/README.md)：官方桌面 Monitor 的工作区与本地运行入口。

## 推荐阅读顺序

1. 先从 [`host/quickstart.md`](./host/quickstart.md) 启动 Host，并确认 `hub.ping` 成功。
2. 需要官方 SDK 时，进入 [`sdk/README.md`](./sdk/README.md) 选择语言。
3. 需要自研客户端或对照原始报文时，进入 [`protocol/README.md`](./protocol/README.md)。
4. 需要权威协议、Schema 或原始示例时，回到 [`../specification/README.md`](../specification/README.md)。

## 使用边界

- 运行、排障、发布与仓库维护文档位于 [`../developer/README.md`](../developer/README.md)。
- 协议事实、字段定义、错误语义与版本化资产位于 [`../specification/README.md`](../specification/README.md)。
