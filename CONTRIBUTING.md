# Contributing to DevHub

欢迎通过 Issue、文档改进、测试补充和代码提交参与 DevHub。

## 贡献入口

- 仓库改造与维护导航：[`docs/guides/contributor/README.md`](docs/guides/contributor/README.md)
- 本地开发环境与验证：[`docs/guides/开发指南.md`](docs/guides/开发指南.md)
- 发布流程与检查清单：[`docs/operations/publishing/README.md`](docs/operations/publishing/README.md)
- 权威协议规范：[`docs/spec/Spec.md`](docs/spec/Spec.md)

## 基本要求

- 所有公开行为、字段命名、状态转换、错误语义和序列化契约必须与 `docs/spec/Spec.md` 保持一致。
- 修改代码后，按 GitHub CI 相关范围执行本地验证；至少覆盖受影响的构建、测试和必要的 smoke 或打包自检。
- 文档导航、README 和维护说明应与仓库当前状态保持一致。
- 当前阶段的发布准备相关改动，优先通过 `python scripts/release/package_release.py --release-id local-dry-run --channel local` 进行本地闭环验证。
- 仓库发布版本统一以 `eng/Version.props` 为源；调整版本号后，同步执行 `python3 scripts/release/sync_versions.py` 更新 JS / Python 包元数据。

## Pull Request 建议

- 一个 PR 只聚焦一个明确关注点。
- 在 PR 描述中注明关联的里程碑、Spec 章节或发布文档。
- 列出已执行的验证命令。
- 若改动涉及运行时路径、令牌、协议契约、序列化字段或公开接口行为，请在说明中明确指出。

## 提交前检查

- 代码：确认根因已解决，没有引入临时补丁式实现。
- 测试：确认最小相关测试通过。
- 文档：确认对外导航、指南和治理文件没有失联。
- 发布相关：若改动影响打包或发布流程，补跑本地 release dry-run。
