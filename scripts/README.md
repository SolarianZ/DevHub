# DevHub Scripts

此目录包含 DevHub 工程的常用工具脚本。详细文档请查看 [docs](../docs/README.md) 。
所有脚本都支持 `--help` 参数，传递该参数时，将忽略其他所有参数，只输出脚本用途和参数摘要，不执行其他逻辑。

## 内容结构

```text
scripts/
├── docs/                                    # 文档维护脚本
│   └── check_markdown_links.py              # Markdown 链接检查工具
├── release/                                 # 发布与版本同步脚本
│   ├── package_release.py                   # Host、SDK 与可选 Monitor 的发布级编排入口
│   ├── package_host.py                      # Host 验证与双变体 ZIP 打包入口
│   ├── package_dotnet_sdk.py                # .NET SDK 验证与打包入口
│   ├── package_js_sdk.py                    # JS/TS SDK 验证与打包入口
│   ├── package_py_sdk.py                    # Python SDK 验证与打包入口
│   ├── package_monitor.py                   # Monitor 单平台验证与打包入口
│   ├── package_models.py                    # 发布脚本共享数据模型
│   ├── package_shared.py                    # 发布脚本共享 CLI / 日志 / 路径 / JSON 辅助
│   ├── resolve_release_metadata.py          # 统一解析 preview/main/stable 发布元数据
│   └── sync_versions.py                     # 版本号同步脚本
└── sdk/                                     # SDK 验证辅助脚本
    ├── publish_unity_dotnet_sdk.py          # Unity 版 .NET SDK 专用发布脚本
    └── run_integration_full.py              # 运行完整 SDK 集成测试
```

## 发布脚本约定

- `scripts/release/package_release.py` 负责 release 级元数据、组件打包编排、manifest、release notes 和最终完整性检查。
- `scripts/release/package_host.py`、`package_dotnet_sdk.py`、`package_js_sdk.py`、`package_py_sdk.py`、`package_monitor.py` 负责各自产物域的独立验证或打包。
- `scripts/release/resolve_release_metadata.py` 负责把 `preview`、`main` 与 `v*` ref 解析为统一的 channel、release id、release tag 与 release name。
- 所有 package 脚本都支持 `--release-id` 和 `--output-root` 参数。
- 具备“只验证不产物化”语义的脚本提供 `--verify-only` 参数，用于执行对应工作流同级别的本地校验而不写出完整产物。
