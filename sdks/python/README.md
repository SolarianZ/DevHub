# DevHub Python SDK

此目录包含 DevHub Python SDK 的源代码、工作区配置和测试。详细文档请查看 [docs](../../docs/README.md) 。

## 内容结构

```text
sdks/python/
├── pyproject.toml                           # 构建、依赖与包元数据定义
├── src/                                     # SDK 源代码
│   └── devhub_sdk/                          # SDK 核心包
└── tests/                                   # SDK 测试与测试资产
    ├── unit/                                # 单元测试
    ├── integration/                         # 集成测试
    ├── conformance/                         # 协议符合性测试
    └── assets/                              # 测试静态资源
```

## 版本能力

SDK 对外公开包版本常量 `SDK_VERSION` 与 `__version__`，可用于记录调用侧使用的 Python SDK 版本。

SDK 提供 `create_instance_id(app_id, scope="")`，用于生成满足 canonical `instanceId` 语法且长度不超过 256 的实例标识。生成值包含 `appId`、`scope` 语义和随机后缀，适合在当前 Hub 注册表中作为全局实例身份使用。
`register_instance(...)` 不会为不同 `appId + scope` 静默复用同一 `instanceId`；同一 Hub 注册表中的 `instanceId` 表示全局实例身份。

同步 `DevHubClient` 与异步 `DevHubEventsClient` 都提供以下版本相关 API：

- `get_host_version()`：调用 `hub.getVersion` 并返回当前 Host 版本字符串。
- `check_version_compatibility()`：返回 `VersionCompatibilityResult`，包含 `sdk_version`、`host_version` 与 `status`。

`VersionCompatibilityStatus` 的取值与判定规则如下：

- `compatible`：`major` 与 `minor` 相同。
- `update_recommended`：`major` 相同但 `minor` 不同。
- `incompatible`：`major` 不同。
- `unknown`：无法得到可比较的 Host 版本。

`check_version_compatibility()` 优先使用 `hub.getVersion` 的返回值；当 Host 返回 `method_not_found` 时，读取 `runtime.hub_version` 作为回退来源。`patch`、预发布标签和构建元数据差异不会单独触发提示。
