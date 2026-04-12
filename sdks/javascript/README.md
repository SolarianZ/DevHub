# DevHub JS/TS SDK

DevHub JS/TS SDK 基于 `docs/spec/Spec.md` 的 Hub v1.x 协议。`@devhub/sdk` 根入口面向 `Node.js 20+` 与浏览器/WebView 双运行时，`@devhub/sdk/runtime` 子路径面向 Node.js 文件系统运行时发现能力。

## 接入导航

- [`../../docs/guides/sdk/javascript.md`](../../docs/guides/sdk/javascript.md)：面向外部调用方的 `JS/TS SDK` 接入指南。
- [`../../docs/guides/getting-started/host-quickstart.md`](../../docs/guides/getting-started/host-quickstart.md)：启动 Host、读取 `hub.json` 和 `tokenFile` 的入口。
- [`../../docs/guides/无SDK接入指南.md`](../../docs/guides/无SDK接入指南.md)：不依赖官方 SDK 的原始协议路径。

## 入口分工

- `@devhub/sdk`：浏览器安全的根入口，导出 `DevHubClient`、`DevHubEventsClient`、错误类型、模型类型、脱敏 `DevHubRuntimeView`，以及供高级接入使用的扩展 seam 类型。
- `@devhub/sdk/runtime`：Node.js 专用子路径，导出 `discoverRuntime`、`resolveDataDirectory`、`FileSystemRuntimeResolver` 和 `DATA_DIR_ENV`。
- 浏览器/WebView：根入口可直接导入，但连接 Host 时必须显式注入自定义 `runtimeResolver`。
- Node.js：可直接调用 `DevHubClient.fromRuntime(...)` / `DevHubEventsClient.fromRuntime(...)` 使用默认文件系统发现，也可按需从 `@devhub/sdk/runtime` 导入文件系统发现辅助。

## 当前状态

- 已提供工程骨架。
- 已提供基础模型、统一错误模型，以及通过 `@devhub/sdk/runtime` 暴露的 Node.js 文件系统运行时发现能力。
- 已实现 HTTP JSON-RPC 客户端封装（`ping` / `apps` / `launch` / `invoke` / `poll` / `respond` 等），其中应用定义管理已覆盖 `list/get/validate/upsert/delete`，实例注册/注销已对齐顶层 `password` 参数。
- 已实现 WebSocket 事件客户端封装（`authenticate` / `subscribe` / `unsubscribe` / 事件流），并收敛到包含 `app.definition.upserted` / `app.definition.deleted` 在内的闭集事件类型。
- 已补齐本地参数校验、成功载荷结构校验与 `invocation_failed` 错误映射辅助，并对 `echo` / `args` / `meta` / `error.data` 等 JSON 载荷执行严格校验，避免静默丢字段或重写值；`hub.invoke.notify` 会按 Spec 拒绝不受支持的 `waitTimeoutMs`；`respond.error` 与 JSON-RPC `error` 结构按 Spec 要求整数 `code` 与对象型 `data`。
- 已公开 `DevHubEventType` 与 `SUPPORTED_EVENT_TYPES`，为 TypeScript 调用方提供规范事件类型的编译期约束。
- 已补齐 JS SDK 单元测试与 Host 级集成测试，覆盖 `launch`、`invoke` 往返、超时/过期、scope 路由与事件重连场景。
- 已补齐 Host 级能力门禁错误集成测试，覆盖 `rpc_disabled`、`poll_not_enabled` 与 `respond_not_enabled` 的错误映射。
- 已公开运行时解析器契约类型、HTTP 传输与 WebSocket 会话扩展点；其中 `runtimeResolver.resolve(options)` 会收到完整归一化客户端选项，便于 fake transport、录制回放或自定义连接策略测试。
- 顶层客户端实例只公开脱敏 `runtime` 视图；bearer token 与原始连接上下文保留在运行时发现和 transport / session 的内部协作链路中。
- 当调用方未显式提供 `clientSessionId` 时，同一 JavaScript 运行时上下文中的 `DevHubClient` 与 `DevHubEventsClient` 会复用同一个默认会话身份。

> 根入口优先使用当前运行时提供的标准 Web API；Node.js 路径仅在缺少原生 `WebSocket` 时按需动态加载 `ws` 回退实现。

## 能力范围

- Node.js 文件系统运行时发现：读取 `hub.json` 与 `token.txt`。
- HTTP JSON-RPC：`ping`、应用定义查询/校验/写入/删除、带顶层 `password` 的实例管理、`launch`、`notify`、`request`、`poll`、`respond`。
- WebSocket 事件：鉴权、订阅、取消订阅、事件流读取，以及定义生命周期事件解析。
- 统一错误模型：`DevHubRpcError`、`reason` / `invocationId` / `calleeError` 辅助属性。
- 本地参数校验：在请求发出前校验关键字段与默认值约束。

## 开发命令

```bash
npm install
npm run typecheck
npm run build
npm test
```

## 集成测试隔离模式

- `npm test` 中的集成测试会自行构建并启动临时 DevHub Host，为当前测试文件分配独立临时 `dataDir`，固定通过 `<dataDir>/runtime/hub.json` 发现连接信息。
- 集成测试不会连接开发机默认数据目录下的常驻 Hub；测试结束后会关闭自己启动的临时 Host，回收 Host 进程树，并删除对应临时目录。
- 默认情况下，JS 集成测试会把 Host 构建到自己的临时输出目录，再从该隔离产物启动 Host；这一模式的目标是隔离运行时状态，并避免直接复用源码树下的 Host 可执行输出。
- 如果需要关闭这一步默认构建，或希望并行执行多套 SDK 集成测试，请先串行准备好 Host 程序，再通过共享环境变量 `DEVHUB_SDK_HOST_ASSEMBLY` 指向固定的已构建 `DevHub.Host.dll`。如需仅覆盖 JS SDK，也可以改用 `DEVHUB_JS_SDK_HOST_ASSEMBLY`；当两者同时存在时，后者优先。
- 仓库级 smoke 验证或手工联调仍可连接本机 Hub，此时请显式传入 `dataDir` 或设置 `DEVHUB_DATA_DIR`，不要把这种运行方式与 SDK 集成测试混用。

## 已验证能力

- Runtime discovery：读取 `hub.json`、解析 `tokenFile`、应用 `dataDir` / `DEVHUB_DATA_DIR` 覆盖，并固定使用 `<dataDir>/runtime/hub.json`。
- AppDefinition 管理：`get` / `validate` / `upsert` / `delete`、`definition_invalid` 结构化错误、`app_definition_not_found` 删除失败分支。
- AppInstance 密码语义：`registerInstance` / `unregisterInstance` 的顶层 `password` 参数、密码不匹配拒绝分支，以及公开模型 / 事件不泄漏密码。
- HTTP flows：`ping`、应用定义查询、实例注册/心跳/注销、`launch`、`notify`、`request`、`poll`、`respond`。
- Launch semantics：`started`、`starting`、`already_running` 状态与去重/在线实例分支。
- Invocation semantics：默认选项、`delivery_conflict`、`invocation_timeout`、`invocation_expired`、`invocation_failed`。
- Capability gates：`rpc_disabled`、`poll_not_enabled`、`respond_not_enabled` 错误映射。
- Scope routing：默认 Global、显式空字符串 scope、字面量 `global` 与命名 scope。
- Events flows：WS 鉴权、订阅/取消订阅、`app.definition.upserted` / `app.definition.deleted` 等事件解析、仅接受响应或 `hub.event` 入站消息、断线后重新认证并重新订阅。

## 快速示例

Node.js 20+ 默认文件系统发现：

```ts
import { DevHubClient, DevHubEventsClient } from "@devhub/sdk";

const client = await DevHubClient.fromRuntime({ clientId: "demo" });
const ping = await client.ping({ value: 1 });
console.log(ping.serverTimeUtc, ping.echo);

const eventsClient = await DevHubEventsClient.fromRuntime({ clientId: "demo-events" });
await eventsClient.authenticate();
console.log(client.runtime.pid, client.options.clientSessionId === eventsClient.options.clientSessionId);
const subscriptionId = await eventsClient.subscribe();

for await (const evt of eventsClient.readEvents()) {
  console.log("event", evt.type, evt.payload);
}
```

浏览器 / WebView 自定义 `runtimeResolver`：

```ts
import {
  DevHubClient,
  type RuntimeConnectionInfo,
  type RuntimeResolver
} from "@devhub/sdk";

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
  { clientId: "webview-demo" },
  { runtimeResolver }
);
```

Node.js 显式使用 `@devhub/sdk/runtime`：

```ts
import { DevHubClient } from "@devhub/sdk";
import {
  FileSystemRuntimeResolver,
  discoverRuntime,
  resolveDataDirectory
} from "@devhub/sdk/runtime";

const dataDir = resolveDataDirectory(process.env.DEVHUB_DATA_DIR);
const connection = await discoverRuntime(dataDir);
console.log(connection.rpcEndpoint, connection.websocketEndpoint);

const client = await DevHubClient.fromRuntime(
  { clientId: "node-runtime-demo", dataDir },
  { runtimeResolver: new FileSystemRuntimeResolver() }
);
```

## 从旧根入口迁移 runtime 值导入

根入口继续保留高级运行时契约类型导出，Node.js 文件系统运行时值从 `@devhub/sdk/runtime` 获取；客户端实例上的 `runtime` 仅提供脱敏诊断视图，不再公开 bearer token、端点或 `tokenFile`：

```ts
// 迁移前
import {
  FileSystemRuntimeResolver,
  discoverRuntime,
  resolveDataDirectory
} from "@devhub/sdk";

// 当前入口
import {
  FileSystemRuntimeResolver,
  discoverRuntime,
  resolveDataDirectory
} from "@devhub/sdk/runtime";
```

## 应用定义与安全实例管理

定义写接口只在 `DevHubClient` 上提供；实例密码是独立方法参数，不进入 `AppInstanceRegistration`、`AppInstance` 或事件 payload。

```ts
const definition = {
  appId: "sample.app",
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

const instance = await client.registerInstance({
  instanceId: "sample-inst-1",
  appId: "sample.app",
  pid: process.pid,
  invoke: { poll: true, respond: true }
}, "sample-instance-secret");

await client.unregisterInstance(instance.instanceId, "sample-instance-secret");
await client.deleteDefinition(definition.appId);
```

## 高级扩展

默认情况下，推荐继续使用 `DevHubClient.fromRuntime(...)` 与 `DevHubEventsClient.fromRuntime(...)`。

如果需要接入自定义运行时发现、fake transport、录制/回放测试或自定义 WebSocket 会话，可以通过公开导出的扩展点注入：

```ts
import {
  DevHubClient,
  JsonRpcHttpTransport
} from "@devhub/sdk";
import { FileSystemRuntimeResolver } from "@devhub/sdk/runtime";

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
} from "@devhub/sdk";
import { FileSystemRuntimeResolver } from "@devhub/sdk/runtime";

const eventsClient = await DevHubEventsClient.fromRuntime(
  { clientId: "example-events-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    sessionFactory: (options) => new JsonRpcWsSession(options)
  }
);
```

如需自定义运行时发现策略，可自行实现 `RuntimeResolver`；`resolve(options)` 会收到归一化后的客户端选项，默认 Node.js 实现仍只按 Spec 使用 `options.dataDir`、`DEVHUB_DATA_DIR` 和平台默认数据目录。
