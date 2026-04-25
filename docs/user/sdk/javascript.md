# DevHub JS/TS SDK 接入指南

本文面向准备通过官方 `JS/TS SDK` 连接 DevHub Host 的调用方，覆盖双运行时入口选择、运行时发现、常见调用方式、扩展点与最小验证方式。

## 1. 前置条件

- 已按 [`../host/quickstart.md`](../host/quickstart.md) 启动 Host，并确认 `hub.json` 与 `tokenFile` 可读。
- Node.js 调用方需具备 `Node.js 20+` 与 `npm`。
- 浏览器 / WebView 调用方需由宿主应用提供可用的运行时连接信息，并通过自定义 `runtimeResolver` 交给 SDK。
- 浏览器 / WebView 直连 Host 时，宿主应用还需提供 `tokenFile` 中的 Bearer Token，并确保前端可以直接访问 `hub.json.httpBaseUrl` 指向的回环地址。
- 若当前分发渠道尚未提供正式安装资产，请按 [`../../developer/publishing/README.md`](../../developer/publishing/README.md) 中的 `TODO(devhub-release)` 占位规范书写安装说明。

## 2. 获取 SDK

仓库内最直接的获取方式是先安装依赖并构建工作区：

```bash
npm --prefix sdks/javascript ci
npm --prefix sdks/javascript run build
```

如需模拟“发布资产消费”，可进一步执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

`DEVHUB_MONITOR_SDK_SOURCE=local-src` 是仓库内 `apps/monitor/` 与 `sdks/javascript/` 的源码联调机制，不属于外部调用方安装或消费 `JS/TS SDK` 的正式方式。面向发布包的调用方应优先使用已构建的 SDK 资产或 release tarball。

## 3. 入口分工

- `@devhub/sdk-javascript`：浏览器安全的根入口，导出 `DevHubClient`、`DevHubEventsClient`、错误类型、模型类型、脱敏 `runtime` 视图，以及供高级接入使用的扩展 seam 类型。
- `@devhub/sdk-javascript/runtime`：Node.js 专用子路径，导出 `discoverRuntime`、`resolveDataDirectory`、`FileSystemRuntimeResolver` 和 `DATA_DIR_ENV`。
- 浏览器 / WebView：根入口可直接导入，但连接 Host 时必须显式注入自定义 `runtimeResolver`；官方支持路径是前端直接访问 Host，而不是通过原生层代理 `/rpc`。
- Node.js：可直接调用 `DevHubClient.fromRuntime(...)` / `DevHubEventsClient.fromRuntime(...)` 使用默认文件系统发现，也可按需从 `@devhub/sdk-javascript/runtime` 导入文件系统发现辅助。
- `DevHubEventsClient` / `JsonRpcWsSession` 会优先使用全局 `WebSocket`；仅当 Node 运行时缺少全局实现时，才会在运行时懒加载 `ws` 作为回退。该回退不会改变根入口的浏览器安全定位，也不应成为浏览器 / WebView 构建阶段的静态依赖。
- 使用官方发布包时，Node 侧 `ws` 由 SDK 包依赖提供；若以仓库源码直接消费 SDK 且运行环境没有全局 `WebSocket`，则需要自行提供兼容实现或安装 `ws`。

Node.js 文件系统相关的运行时值导入路径为 `@devhub/sdk-javascript/runtime`。

## 4. 能力概览

- HTTP JSON-RPC：覆盖 `ping`、应用定义管理、实例管理、`launch`、`notify`、`request`、`poll`、`respond`。
- WebSocket 事件：覆盖鉴权、订阅、取消订阅与 `hub.event` 事件流。
- 统一错误模型：`DevHubRpcError` 用于 JSON-RPC `error` 响应；`DevHubConnectionError` 用于超时、传输故障、非 `200` HTTP、非法响应和事件流终止等连接级失败。
- 本地参数校验：对 `echo`、`args`、`meta`、`error.data` 等 JSON 载荷执行严格校验。
- 闭集事件类型：公开 `DevHubEventType` 与 `SUPPORTED_EVENT_TYPES`，为 TypeScript 调用方提供编译期约束。
- 运行时上下文：当调用方未显式提供 `clientSessionId` 时，同一 JavaScript 运行时上下文中的 `DevHubClient` 与 `DevHubEventsClient` 会复用同一个默认会话身份。

## 5. 连接 Host

### 5.1 Node.js 20+ 默认文件系统发现

```ts
import { DevHubClient, DevHubEventsClient } from "@devhub/sdk-javascript";

const client = await DevHubClient.fromRuntime({ clientId: "quickstart-js" });
const ping = await client.ping({ hello: "world" });
console.log(ping.ok, ping.serverTimeUtc);

const eventsClient = await DevHubEventsClient.fromRuntime({ clientId: "quickstart-js-events" });
await eventsClient.authenticate();
```

### 5.2 浏览器 / WebView 自定义 `runtimeResolver`

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

浏览器 / WebView 直连要点：

- `runtimeResolver` 返回的 `RuntimeConnectionInfo` 必须来自宿主应用对 `hub.json` / `tokenFile` 的安全读取结果，而不是前端自行扫描本地文件。
- 前端首次调用 `/rpc` 时，浏览器通常会先发送 `OPTIONS /rpc` 预检；Host 会回显当前请求 `Origin`，并声明 `POST`、`OPTIONS` 以及 `Authorization`、`Content-Type`、`X-DevHub-Protocol`、`X-DevHub-ClientId`、`X-DevHub-ClientSessionId` 可用于后续正式请求，随后前端再发起正式 `POST /rpc`。
- 实际 RPC 请求仍然必须携带 Bearer Token 与全部协议头；若响应是 JSON-RPC `error`，浏览器 / WebView 仍可读取原始错误载荷。
- 原生层职责是提供运行时连接信息与令牌，不需要为前端再包一层 HTTP transport 代理。

### 5.3 Node.js 显式使用运行时发现辅助

```ts
import { DevHubClient } from "@devhub/sdk-javascript";
import {
  FileSystemRuntimeResolver,
  discoverRuntime,
  resolveDataDirectory
} from "@devhub/sdk-javascript/runtime";

const dataDir = resolveDataDirectory(process.env.DEVHUB_DATA_DIR);
const connection = await discoverRuntime(dataDir);
console.log(connection.rpcEndpoint, connection.websocketEndpoint);

const client = await DevHubClient.fromRuntime(
  { clientId: "quickstart-node-runtime", dataDir },
  { runtimeResolver: new FileSystemRuntimeResolver() }
);
```

## 6. 常见交互场景

### 6.1 事件流读取契约

```ts
import { APP_INSTANCE_REGISTERED } from "@devhub/sdk-javascript";

await eventsClient.authenticate();
const iterator = eventsClient.readEvents()[Symbol.asyncIterator]();
const subscriptionId = await eventsClient.subscribe([APP_INSTANCE_REGISTERED]);

try {
  const first = await iterator.next();
  if (!first.done) {
    console.log(subscriptionId, first.value.type, first.value.payload);
  }
} finally {
  await iterator.return?.();
}
```

- 每个 `DevHubEventsClient` 实例同一时刻只允许一个活动中的 `readEvents()` 读取器；若业务需要多个消费者，应在调用方内部自行扇出。
- 底层 WebSocket 终止或重新认证失败后，当前活动读取器仍可排空终止前已经进入缓冲的事件；后续新的 `readEvents()` 调用会在重新认证成功前直接失败。
- 上述“直接失败”对外表现为 `DevHubConnectionError`；连接正常终止时 `kind === "session_terminated"`，若事件流或响应包本身不合法，则返回 `kind === "invalid_response"`。
- 重新执行 `authenticate()` 只会建立新的事件流代次，不会恢复旧订阅；恢复事件交付时需要再次调用 `subscribe()`。

### 6.2 定义与实例管理

定义写接口只在 `DevHubClient` 上提供；实例密码是独立方法参数，不进入 `AppInstanceRegistration`、`AppInstance` 或事件 payload。列表查询同样必须显式提供 `scope`；如需查询全部作用域，只在 `listDefinitions` / `listInstances` 中传入 `null`。

```ts
const definition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App",
  launch: {
    exePath: "python3",
    argsTemplate: "app.py"
  }
};

const validation = await client.validateDefinition(definition);
if (validation.valid) {
  await client.upsertDefinition(definition);
}

const registered = await client.registerInstance({
  instanceId: "sample-inst-1",
  appId: "sample.app",
  scope: "",
  pid: process.pid,
  invoke: { poll: true, respond: true }
}, "sample-instance-secret");

await client.unregisterInstance(registered.instanceId, registered.instanceSessionToken);
await client.deleteDefinition({
  appId: definition.appId,
  scope: definition.scope
});
```

### 6.3 调用与响应对象形状

```ts
await client.request({
  appId: "sample.app",
  method: "sample.request",
  target: {
    scope: ""
  }
});

await client.respond({
  instanceId: "sample-inst-1",
  instanceSessionToken: "sample-session-token",
  invocationId: "invk-1",
  value: {
    ok: true
  }
});
```

- `InvokeRequest.target` 与 SDK 解析得到的 `Invocation.target` 都是必填字段，调用方不需要再为缺省 `target` 编写分支。
- `AppDefinition.launch` 只要存在，就必须显式提供 `launch.exePath`。
- `RespondRequest` 只接受“携带 `value`”或“携带 `error`”两种互斥形状之一，不能同时省略，也不能同时提供。

### 6.4 错误处理约定

```ts
import {
  DevHubConnectionError,
  DevHubRpcError,
} from "@devhub/sdk-javascript";

try {
  await client.ping();
} catch (error) {
  if (error instanceof DevHubConnectionError) {
    console.error("connection failure", error.kind, error.status, error.responseBody);
    return;
  }

  if (error instanceof DevHubRpcError) {
    console.error("rpc failure", error.code, error.reason, error.invocationId);
    return;
  }

  throw error;
}
```

- `DevHubRpcError` 表示 Host 已成功返回 JSON-RPC `error` 对象；调用方可继续读取 `code`、`knownCode`、`reason`、`invocationId`、`calleeError` 与 `tryGetDataProperty(...)`。
- `DevHubConnectionError` 表示请求尚未进入有效业务结果阶段，或连接/会话已经失效。当前公开的 `kind` 包括：`timeout`、`transport`、`http_status`、`invalid_response`、`session_terminated`。
- `timeout` 表示请求超时；`transport` 表示底层 `fetch`/WebSocket/网络栈失败；`http_status` 表示收到非 `200` HTTP 响应，并可结合 `status`、`statusText`、`responseBody` 诊断。
- `invalid_response` 表示收到的 HTTP JSON-RPC 包、WebSocket 响应或事件通知不符合协议形状；此类错误通常意味着上游实现或中间链路返回了非法载荷。
- `session_terminated` 表示事件流或 WebSocket 会话已经终止；对 `DevHubEventsClient` 而言，需要重新执行 `authenticate()`，并重新调用 `subscribe()` 恢复事件消费。

## 7. 高级扩展

默认情况下，推荐使用 `DevHubClient.fromRuntime(...)` 与 `DevHubEventsClient.fromRuntime(...)`。

如果需要接入自定义运行时发现、fake transport、录制 / 回放测试或自定义 WebSocket 会话，可以通过公开导出的扩展点注入：

```ts
import {
  DevHubClient,
  JsonRpcHttpTransport
} from "@devhub/sdk-javascript";
import { FileSystemRuntimeResolver } from "@devhub/sdk-javascript/runtime";

const client = await DevHubClient.fromRuntime(
  { clientId: "example-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    transportFactory: (options, connection) => new JsonRpcHttpTransport(options, connection)
  }
);
```

```ts
import {
  DevHubEventsClient,
  JsonRpcWsSession
} from "@devhub/sdk-javascript";
import { FileSystemRuntimeResolver } from "@devhub/sdk-javascript/runtime";

const eventsClient = await DevHubEventsClient.fromRuntime(
  { clientId: "example-events-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    sessionFactory: (options) => new JsonRpcWsSession(options)
  }
);
```

## 8. 最小验证方式

- 直接运行上面的 `client.ping()` 示例，确认返回 `ok=true`。
- 若要验证 `JS/TS SDK` 工作区自身的测试基线，可执行：

```bash
npm --prefix sdks/javascript test
```

- 若要验证发布 tarball 是否可生成，可执行：

```bash
npm --prefix sdks/javascript pack --pack-destination temp/sdk-pack
```

- 若要查看工作区构建、集成测试隔离或仓库级联调要求，请阅读 [`../../developer/guides/development.md`](../../developer/guides/development.md)。

## 9. 相关文档

- [`./README.md`](./README.md)
- [`../../../sdks/javascript/README.md`](../../../sdks/javascript/README.md)
- [`../host/quickstart.md`](../host/quickstart.md)
- [`../protocol/README.md`](../protocol/README.md)
- [`../../developer/guides/development.md`](../../developer/guides/development.md)
- [`../../developer/publishing/README.md`](../../developer/publishing/README.md)
