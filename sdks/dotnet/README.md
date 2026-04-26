# DevHub .NET SDK

此目录包含 DevHub .NET SDK 的源代码、工作区配置和测试。详细文档请查看 [docs](../../docs/README.md) 。

## 版本查询与兼容性检查

`DevHubClient` 与 `DevHubEventsClient` 都提供以下入口：

- `GetHostVersionAsync(...)`：调用 `hub.getVersion` 并返回 Host 的直接版本字符串。
- `CheckVersionCompatibilityAsync(...)`：优先调用 `hub.getVersion`，当 Host 返回 `method_not_found` 时回退到 `Runtime.HubVersion`，并返回 `VersionCompatibilityResult`。

`VersionCompatibilityResult.Status` 的判定规则如下：

- `Incompatible`：`Major` 不同。
- `UpdateRecommended`：`Major` 相同但 `Minor` 不同。
- `Compatible`：`Major` 与 `Minor` 相同；`Patch`、预发布标签和构建元数据差异不单独提示。
- `Unknown`：版本缺失，或无法解析为兼容检查所需的语义化版本格式。

事件客户端上的版本查询与兼容性检查继续复用已鉴权 WebSocket 只读通道，因此调用前需要先执行 `AuthenticateAsync(...)`。

## 内容结构

```text
sdks/dotnet/
├── DevHub.DotNetSdk.slnx                          # .NET SDK 工作区解决方案文件
├── Directory.Build.props                          # 工作区公共构建配置
├── Directory.Packages.props                       # 统一依赖版本管理
├── src/                                           # SDK 源代码
│   ├── DevHub.Sdk/                                # SDK 核心库
│   └── DevHub.Sdk.DependencyInjection/            # 依赖注入扩展
├── tests/                                         # SDK 测试工程
│   ├── DevHub.Sdk.UnitTests/                      # SDK 单元测试
│   ├── DevHub.Sdk.DependencyInjection.UnitTests/  # 依赖注入扩展单元测试
│   ├── DevHub.Sdk.IntegrationTests/               # SDK 与 Host 的集成测试
│   └── DevHub.Sdk.ConformanceAdapter/             # 协议符合性适配器
└── tools/                                         # SDK 辅助工具
    └── DevHub.Sdk.UnityPublish/                   # Unity 发布支持工具（仅限 dotnet_sdk_for_unity 分支）
```
