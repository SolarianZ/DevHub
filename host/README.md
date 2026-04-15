# DevHub Host

此目录包含 DevHub Host 的源代码、工作区配置和测试。详细文档请查看 [docs](../docs/README.md) 。

## 内容结构

```text
host/
├── DevHub.slnx                              # Host 工作区解决方案文件
├── Directory.Build.props                    # Host 工作区公共构建配置
├── src/                                     # Host 源代码
│   ├── DevHub.Core/                         # 核心领域模型与基础服务
│   └── DevHub.Host/                         # 基于 ASP.NET Core 的宿主程序
└── tests/                                   # Host 测试与辅助资源
    ├── whitebox/                            # Host 白盒测试工程
    ├── blackbox/                            # Python 黑盒测试与统一 runner
    ├── conformance/                         # 协议符合性向量、runner 与自测
    └── tools/                               # 覆盖率配置与仓库级辅助脚本
```
