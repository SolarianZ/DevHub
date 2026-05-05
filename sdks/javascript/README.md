# DevHub JS/TS SDK

此目录包含 DevHub JavaScript SDK 的源代码、工作区配置和测试。详细文档请查看 [docs](../../docs/README.md) 。

## 内容结构

```text
sdks/javascript/
├── package.json                             # 包定义、导出路径与脚本入口
├── tsconfig.json                            # TypeScript 开发配置
├── tsconfig.build.json                      # TypeScript 构建配置
├── vitest.config.ts                         # 测试运行配置
├── src/                                     # SDK 源代码与导出入口
└── tests/                                   # SDK 测试与测试资产
    ├── unit/                                # 单元测试
    ├── integration/                         # 集成测试
    ├── conformance/                         # 协议符合性测试
    ├── types/                               # 类型测试
    └── assets/                              # 测试静态资源
```

## 版本查询与兼容性检查

SDK 根入口导出 `SDK_VERSION` 常量，值由 `package.json` 自动同步生成，可在运行时直接读取。`DevHubClient` 与 `DevHubEventsClient` 都提供 `getHostVersion()` 和 `checkVersionCompatibility()`：

```ts
import {
  DevHubClient,
  SDK_VERSION
} from "@devhub/sdk-javascript";

const client = await DevHubClient.fromRuntime({
  clientId: "sample.version-check"
});

const hostVersion = await client.getHostVersion();
const compatibility = await client.checkVersionCompatibility();

console.log({
  sdkVersion: SDK_VERSION,
  hostVersion,
  status: compatibility.status
});
```

`checkVersionCompatibility()` 的状态规则如下：

- `incompatible`：`major` 不同。
- `updateRecommended`：`major` 相同但 `minor` 不同。
- `compatible`：`major` 与 `minor` 相同，`patch`、预发布标签和构建元数据差异不会单独提示。
- `unknown`：`hub.getVersion` 不可用且 `runtime.hubVersion` 缺失或无法解析，或 SDK / Host 版本字符串无法完成比较。

`DevHubEventsClient` 的两个版本接口复用已鉴权 WebSocket 只读 RPC 通道，调用前需要先执行 `authenticate()`。

## 实例注册标识

`registerInstance()` 使用的 `instanceId` 是当前 Hub 注册表内的全局实例身份。该值必须满足公开 `instanceId` 语法，长度不超过 256 个字符。

生成实例 ID 时建议包含 `appId`、`scope` 与随机或进程级后缀，例如 `sample.app.global.550e8400e29b41d4a716446655440000`。同一个 `instanceId` 只适合同一 `appId + scope` 的重注册使用，避免复用到其他 App 或 Scope。
