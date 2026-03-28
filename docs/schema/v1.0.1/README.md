# DevHub Schema 包（v1.0.1）

本目录提供 DevHub Hub v1.0.1 在当前仓库中维护的版本化 JSON Schema 资产，供第三方开发者在不阅读 SDK 源码的前提下完成：

- `hub.json` 发现文件校验
- AppDefinition / AppInstance / Invocation 数据结构校验
- JSON-RPC 请求、成功响应与错误响应的信封校验

## 1. 规范来源

- `app-definition.json`
- `app-instance.json`
- `invocation.json`
- `hub-runtime.json`

以上 4 个文件直接整理自 [`docs/Spec.md`](../../Spec.md) §5。

- `rpc-request.json`
- `rpc-response.json`
- `error-response.json`

以上 3 个文件依据 [`docs/Spec.md`](../../Spec.md) §3.1、§6.1 与 §8 整理。

## 2. 版本与 URI 约定

- Schema 标准：Draft-07
- 发布目录版本：`v1.0.1`
- `$id` 继续沿用 Spec 约定的 `/v1/` URI，不改写为 `/v1.0.1/`

这意味着仓库目录版本用于发布与引用管理，`$id` 用于表达协议大版本稳定标识，两者不冲突。

## 3. 文件清单

- [`app-definition.json`](./app-definition.json)
- [`app-instance.json`](./app-instance.json)
- [`invocation.json`](./invocation.json)
- [`hub-runtime.json`](./hub-runtime.json)
- [`rpc-request.json`](./rpc-request.json)
- [`rpc-response.json`](./rpc-response.json)
- [`error-response.json`](./error-response.json)

## 4. 使用方式

建议把本目录视为 DevHub v1.0.1 当前公开基线下的版本化协议资产。首次正式对外发布前，若 `Spec.md` 为对齐核心目标而修订，目录内容与配套文档会同步更新：

- 读取 `hub.json` 后，用 `hub-runtime.json` 做发现文件校验。
- 读取或生成应用定义时，用 `app-definition.json` 校验。
- 读取实例镜像或注册返回值时，用 `app-instance.json` 校验。
- 处理轮询项或调用上下文时，用 `invocation.json` 校验。
- 发送或接收原始 JSON-RPC 报文时，用 `rpc-request.json`、`rpc-response.json`、`error-response.json` 校验信封。

如果需要请求/响应示例，请同时参考：

- [`docs/protocol-examples/v1.0.1/README.md`](../../protocol-examples/v1.0.1/README.md)
