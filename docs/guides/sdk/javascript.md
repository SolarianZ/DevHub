# DevHub JS/TS SDK 接入指南

本文面向准备通过官方 `JS/TS SDK` 连接 DevHub Host 的调用方，覆盖环境准备、连接 Host、最小示例和验证方式。

## 1. 前置条件

- 已按 [`../getting-started/host-quickstart.md`](../getting-started/host-quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- 本地具备 `Node.js 18+` 和 `npm`。
- 首个正式 GitHub Release 发布前，公开安装入口统一使用显式 TODO 占位：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 DevHub JS/TS SDK 的发布资产名称、版本号与安装命令；当前阶段不要填写未发布的版本号、下载链接或仓库外安装命令。
```

## 2. 获取 SDK

当前阶段，最直接的仓库内方式是先安装依赖并构建工作区：

```bash
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
```

如果你希望模拟“发布资产消费”，可进一步执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

然后在消费项目中安装生成的 `.tgz` 文件。正式发布后的 tarball 命名会与 [`../../operations/publishing/release-asset-layout.md`](../../operations/publishing/release-asset-layout.md) 保持一致。

## 3. 连接 Host

`JS/TS SDK` 会按以下顺序定位数据根目录：

1. `options.dataDir`
2. 环境变量 `DEVHUB_DATA_DIR`
3. 平台默认数据目录

最小示例：

```ts
import { DevHubClient } from "@devhub/sdk";

const client = await DevHubClient.fromRuntime({
  clientId: "quickstart-js"
});

const ping = await client.ping({ hello: "world" });
console.log(ping.ok, ping.serverTimeUtc);
```

## 4. 最小验证方式

- 直接运行上面的 `client.ping()` 示例，确认返回 `ok=true`。
- 若要验证 `JS/TS SDK` 工作区自身的测试基线，可执行：

```bash
npm --prefix sdks/javascript test
```

- 若要验证发布 tarball 是否可生成，可执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

## 5. 后续路径

- 需要完整 API、事件流和扩展点说明时，请阅读 [`../../../sdks/javascript/README.md`](../../../sdks/javascript/README.md)。
- 需要对照其他语言 SDK，请回到 [`README.md`](./README.md)。
- 如果你计划直接基于原始协议接入，请切换到 [`../无SDK接入指南.md`](../无SDK接入指南.md)。
