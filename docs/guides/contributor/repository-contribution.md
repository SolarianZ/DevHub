# DevHub 仓库贡献与改造指南

本文面向需要参与 DevHub 仓库改造、文档维护、发布准备和代码提交的协作者，说明入口文档、基础工作流和最小验证要求。

## 1. 开始之前

- 公开协议行为、字段命名、状态转换和错误语义以 [`../../spec/Spec.md`](../../spec/Spec.md) 为唯一权威标准。
- 当前阶段任务、边界和阶段性验收要求以 [`../../milestones/DevHub_M6任务文档.md`](../../milestones/DevHub_M6任务文档.md) 为准。
- 本地开发环境、构建和测试方式以 [`../开发指南.md`](../开发指南.md) 为准。
- 发布资产布局、发布流程和检查清单分别见：
  - [`../../operations/publishing/release-asset-layout.md`](../../operations/publishing/release-asset-layout.md)
  - [`../../operations/publishing/release-process.md`](../../operations/publishing/release-process.md)
  - [`../../operations/publishing/release-checklist.md`](../../operations/publishing/release-checklist.md)

## 2. 常见仓库工作流

### 2.1 代码与文档改造

1. 先确认改动是否涉及协议权威行为；若涉及，请先回到 `Spec.md` 明确边界。
2. 再根据模块职责选择正确的目录落点，例如 `host/`、`sdks/`、`docs/guides/` 或 `docs/operations/`。
3. 修改完成后，执行与 GitHub CI 一致的最小相关验证。
4. 提交前同步更新 README、导航文档和必要的维护说明。

### 2.2 版本维护约定

- 仓库发布版本统一以 `eng/Version.props` 为唯一来源。
- Host 与 `.NET SDK` 通过 MSBuild 导入该文件消费版本属性。
- `JS/TS SDK` 与 `Python SDK` 包元数据通过 `python3 scripts/release/sync_versions.py` 与该文件保持同步。
- 修改版本号后，先运行同步脚本，再执行发布 dry-run 或相关 CI 验证。

### 2.3 发布准备与本地打包

本地发布准备统一通过仓库级脚本完成：

```bash
python scripts/release/package_release.py --release-id local-dry-run --channel local
```

该入口会串联：

- Host 白盒测试与最小 smoke 验证
- `.NET SDK`、`JS/TS SDK`、`Python SDK` 测试
- Host 多平台发布包、三套 SDK 包、manifest 与发布说明生成
- 资产完整性检查

## 3. 根目录治理入口

- [`../../../CONTRIBUTING.md`](../../../CONTRIBUTING.md)：贡献方式、PR 要求和提交前检查。
- [`../../../CHANGELOG.md`](../../../CHANGELOG.md)：变更记录和发布通道说明。
- [`../../../SECURITY.md`](../../../SECURITY.md)：安全问题和支持入口。

## 4. 外部协作者常用入口

- 首次启动 Host：[`../getting-started/host-quickstart.md`](../getting-started/host-quickstart.md)
- 官方 SDK 接入：[`../sdk/README.md`](../sdk/README.md)
- 无 SDK 接入：[`../无SDK接入指南.md`](../无SDK接入指南.md)
- 发布流程与资产：[`../../operations/publishing/README.md`](../../operations/publishing/README.md)
