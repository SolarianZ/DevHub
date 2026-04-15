# DevHub .NET SDK

此目录包含 DevHub .NET SDK 的源代码、工作区配置和测试。详细文档请查看 [docs](../../docs/README.md) 。

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
