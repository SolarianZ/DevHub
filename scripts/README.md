# DevHub Scripts

此目录包含 DevHub 工程的常用工具脚本。详细文档请查看 [docs](../docs/README.md) 。

## 内容结构

```text
scripts/
├── docs/                                    # 文档维护脚本
│   └── check_markdown_links.py              # Markdown 链接检查工具
├── release/                                 # 发布与版本同步脚本
│   ├── package_release.py                   # 发布包组装脚本
│   └── sync_versions.py                     # 版本号同步脚本
└── sdk/                                     # SDK 验证辅助脚本
    └── run_integration_full.py              # 运行完整 SDK 集成测试
```
