# DevHub JS/TS SDK 接入指南

本文面向准备通过官方 `JS/TS SDK` 连接 DevHub Host 的调用方，覆盖双运行时入口选择、连接 Host 的最小示例与验证方式。

## 1. 前置条件

- 已按 [`../getting-started/host-quickstart.md`](../getting-started/host-quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- Node.js 调用方需具备 `Node.js 20+` 与 `npm`。
- 浏览器 / WebView 调用方需由宿主应用提供可用的运行时连接信息，并通过自定义 `runtimeResolver` 交给 SDK。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../operations/publishing/README.md`](../../operations/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

仓库内最直接的获取方式是先安装依赖并构建工作区：

```bash
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
```

如果你希望模拟“发布资产消费”，可进一步执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

然后在消费项目中安装生成的 `.tgz` 文件。正式发布后的 tarball 命名会与 [`../../operations/publishing/release-asset-layout.md`](../../operations/publishing/release-asset-layout.md) 保持一致。

## 3. 选择入口

- `@devhub/sdk-javascript`：双运行时根入口，可在 `Node.js 20+` 与浏览器 / WebView 中导入，提供客户端、事件客户端、传输抽象、错误类型、模型类型和运行时契约类型。
- `@devhub/sdk-javascript/runtime`：Node.js 专用子路径，提供 `discoverRuntime`、`resolveDataDirectory` 与 `FileSystemRuntimeResolver` 等文件系统运行时发现辅助。
- 浏览器 / WebView：连接 Host 时必须传入自定义 `runtimeResolver`，避免依赖 Node.js 文件系统发现。
- Node.js：可直接使用 `DevHubClient.fromRuntime(...)` 的默认文件系统发现，也可在需要显式控制数据目录或运行时发现时导入 `@devhub/sdk-javascript/runtime`。

## 4. 连接 Host

Node.js 20+ 最小示例：

```ts
import { DevHubClient } from "@devhub/sdk-javascript";

const client = await DevHubClient.fromRuntime({
  clientId: "quickstart-js"
});

const ping = await client.ping({ hello: "world" });
console.log(ping.ok, ping.serverTimeUtc);
```

浏览器 / WebView 最小示例：

```ts
import {
  DevHubClient,
  type RuntimeConnectionInfo,
  type RuntimeResolver
} from "@devhub/sdk-javascript";

declare global {
  interface Window {
    __DEVHUB_RUNTIME__?: RuntimeConnectionInfo;
  }
}

const runtimeResolver: RuntimeResolver = {
  async resolve() {
    const connection = window.__DEVHUB_RUNTIME__;
    if (!connection) {
      throw new Error("DevHub runtime bridge is unavailable.");
    }

    return connection;
  }
};

const client = await DevHubClient.fromRuntime(
  { clientId: "quickstart-webview" },
  { runtimeResolver }
);
```

Node.js 文件系统发现辅助：

```ts
import { DevHubClient } from "@devhub/sdk-javascript";
import {
  FileSystemRuntimeResolver,
  resolveDataDirectory
} from "@devhub/sdk-javascript/runtime";

const dataDir = resolveDataDirectory(process.env.DEVHUB_DATA_DIR);
const client = await DevHubClient.fromRuntime(
  {
    clientId: "quickstart-node-runtime",
    dataDir
  },
  {
    runtimeResolver: new FileSystemRuntimeResolver()
  }
);
```

## 5. 最小验证方式

- 直接运行上面的 `client.ping()` 示例，确认返回 `ok=true`。
- 若要验证 `JS/TS SDK` 工作区自身的测试基线，可执行：

```bash
npm --prefix sdks/javascript test
```

- 若要验证发布 tarball 是否可生成，可执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

## 6. 相关文档

- 需要完整 API、事件流、迁移说明或扩展点示例时，请阅读 [`../../../sdks/javascript/README.md`](../../../sdks/javascript/README.md)。
- 需要对照其他语言 SDK，请回到 [`README.md`](./README.md)。
- 如果你计划直接基于原始协议接入，请切换到 [`../无SDK接入指南.md`](../无SDK接入指南.md)。
