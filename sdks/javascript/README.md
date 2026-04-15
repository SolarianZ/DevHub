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
