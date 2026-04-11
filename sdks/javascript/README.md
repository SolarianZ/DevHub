# DevHub JS/TS SDK

DevHub JS/TS SDK 基于 `docs/spec/Spec.md` 的 Hub v1.x 协议，目标运行时为 Node.js。

## 接入导航

- [`../../docs/guides/sdk/javascript.md`](../../docs/guides/sdk/javascript.md)：面向外部调用方的 `JS/TS SDK` 接入指南。
- [`../../docs/guides/getting-started/host-quickstart.md`](../../docs/guides/getting-started/host-quickstart.md)：启动 Host、读取 `hub.json` 和 `tokenFile` 的入口。
- [`../../docs/guides/无SDK接入指南.md`](../../docs/guides/无SDK接入指南.md)：不依赖官方 SDK 的原始协议路径。

## 当前状态

- 已提供工程骨架。
- 已提供基础模型、运行时发现与统一错误模型。
- 已实现 HTTP JSON-RPC 客户端封装（`ping` / `apps` / `launch` / `invoke` / `poll` / `respond` 等），其中应用定义管理已覆盖 `list/get/validate/upsert/delete`，实例注册/注销已对齐顶层 `password` 参数。
- 已实现 WebSocket 事件客户端封装（`authenticate` / `subscribe` / `unsubscribe` / 事件流），并收敛到包含 `app.definition.upserted` / `app.definition.deleted` 在内的闭集事件类型。
- 已补齐本地参数校验、成功载荷结构校验与 `invocation_failed` 错误映射辅助，并对 `echo` / `args` / `meta` / `error.data` 等 JSON 载荷执行严格校验，避免静默丢字段或重写值；`hub.invoke.notify` 会按 Spec 拒绝不受支持的 `waitTimeoutMs`；`respond.error` 与 JSON-RPC `error` 结构按 Spec 要求整数 `code` 与对象型 `data`。
- 已公开 `DevHubEventType` 与 `SUPPORTED_EVENT_TYPES`，为 TypeScript 调用方提供规范事件类型的编译期约束。
- 已补齐 JS SDK 单元测试与 Host 级集成测试，覆盖 `launch`、`invoke` 往返、超时/过期、scope 路由与事件重连场景。
- 已补齐 Host 级能力门禁错误集成测试，覆盖 `rpc_disabled`、`poll_not_enabled` 与 `respond_not_enabled` 的错误映射。
- 运行时发现采用数据根目录语义：按 `options.dataDir`、`DEVHUB_DATA_DIR`、平台默认数据目录的顺序解析数据根，并固定读取 `<dataDir>/runtime/hub.json`。
- 已公开运行时解析器、HTTP 传输与 WebSocket 会话扩展点；其中 `runtimeResolver.resolve(options)` 会收到完整归一化客户端选项，便于 fake transport、录制回放或自定义连接策略测试。

> SDK 已内置 `ws` 回退实现，因此在 Node.js 18/19 等未提供全局 `WebSocket` 的环境中也可直接使用事件客户端。

## 能力范围

- 运行时发现：读取 `hub.json` 与 `token.txt`。
- HTTP JSON-RPC：`ping`、应用定义查询/校验/写入/删除、带顶层 `password` 的实例管理、`launch`、`invoke`、`poll`、`respond`。
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
- 这一模式的目标是隔离运行时状态，而不是承诺默认无条件支持同机并行。当前 JS 集成测试默认会在临时输出目录构建 Host，但该构建过程仍会触发 `host/src/DevHub.Host` 的源码树构建并使用共享中间产物；若与其他 `.NET build/test` 或其他 SDK 集成测试同时进行，仍可能出现文件锁冲突。
- 如果需要并行执行多套 SDK 集成测试，请先串行准备好 Host 程序，再通过环境变量 `DEVHUB_JS_SDK_HOST_ASSEMBLY` 指向固定的已构建 `DevHub.Host.dll`，避免多个测试进程同时触发 Host 构建。
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

```ts
import { DevHubClient, DevHubEventsClient } from "@devhub/sdk";

const client = await DevHubClient.fromRuntime({ clientId: "demo" });
const ping = await client.ping({ value: 1 });
console.log(ping.serverTimeUtc, ping.echo);

const eventsClient = await DevHubEventsClient.fromRuntime({ clientId: "demo-events" });
await eventsClient.authenticate();
const subscriptionId = await eventsClient.subscribe();

for await (const evt of eventsClient.readEvents()) {
  console.log("event", evt.type, evt.payload);
}
```

如需显式指定数据根目录，可传入 `dataDir`：

```ts
const client = await DevHubClient.fromRuntime({
  clientId: "demo",
  dataDir: "/path/to/DevHub"
});
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

如果需要接入自定义运行时发现、fake transport、录制/回放测试或自定义 WebSocket 会话，可以通过顶层公开导出的扩展点注入：

```ts
import {
  DevHubClient,
  FileSystemRuntimeResolver,
  JsonRpcHttpTransport
} from "@devhub/sdk";

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
  FileSystemRuntimeResolver,
  JsonRpcWsSession
} from "@devhub/sdk";

const eventsClient = await DevHubEventsClient.fromRuntime(
  { clientId: "example-events-client" },
  {
    runtimeResolver: new FileSystemRuntimeResolver(),
    sessionFactory: (options) => new JsonRpcWsSession(options)
  }
);
```

如需自定义运行时发现策略，可自行实现 `RuntimeResolver`；`resolve(options)` 会收到归一化后的客户端选项，默认实现仍只按 Spec 使用 `options.dataDir`、`DEVHUB_DATA_DIR` 和平台默认数据目录。
