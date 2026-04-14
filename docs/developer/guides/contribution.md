# DevHub 仓库贡献与改造指南

本文面向需要参与 DevHub 仓库改造、文档维护、发布准备和代码提交的协作者，说明入口文档、基础工作流和最小验证要求。

## 1. 开始之前

- 公开协议行为、字段命名、状态转换和错误语义以 [`../../specification/protocol/Specification.md`](../../specification/protocol/Specification.md) 为唯一权威标准。
- 本地开发环境、构建和测试方式以 [`./development.md`](./development.md) 为准。
- 发布资产布局、发布流程和检查清单分别见：
  - [`../publishing/release-asset-layout.md`](../publishing/release-asset-layout.md)
  - [`../publishing/release-process.md`](../publishing/release-process.md)
  - [`../publishing/release-checklist.md`](../publishing/release-checklist.md)

## 2. 常见仓库工作流

### 2.1 代码与文档改造

1. 先确认改动是否涉及协议权威行为；若涉及，请先回到 `Specification.md` 明确边界。
2. 再根据模块职责选择正确的目录落点，例如 `host/`、`sdks/`、`docs/user/`、`docs/developer/` 或 `docs/specification/`。
3. 修改完成后，执行与 GitHub CI 一致的最小相关验证。
4. 提交前同步更新 README、导航文档和必要的维护说明。

`DevHub Monitor` 的额外开发约束：

- 工作区位于 `apps/monitor/`，前端 WebView 与 `src-tauri/` 原生后端必须保持边界清晰，前端不直接访问本地文件。
- 与 Monitor 相关的改动，至少执行 `npm --prefix apps/monitor run verify`，并同步检查 `apps/monitor/README.md`、`docs/README.md`、`docs/developer/guides/development.md` 与运维文档是否一致。
- `host/`、`sdks/javascript/` 与 `apps/monitor/` 的职责不可混用；Monitor 对 Host 的通信入口固定通过 `@devhub/sdk-javascript`。

`JS/TS SDK` 的额外开发约束：

- `@devhub/sdk-javascript` 根入口必须保持可在 `Node.js 20+` 与浏览器 / WebView 中直接导入；根入口可达模块不得重新引入顶层 `node:*`、`ws` 或其他 Node.js 专有依赖。
- `discoverRuntime`、`resolveDataDirectory`、`FileSystemRuntimeResolver` 等 Node.js 文件系统相关能力统一通过 `@devhub/sdk-javascript/runtime` 子路径暴露，不得重新挂回根入口。
- 浏览器 / WebView 场景的示例、测试与接入代码必须显式注入自定义 `runtimeResolver`；Node.js 文件系统发现示例必须使用 `@devhub/sdk-javascript/runtime`。
- 修改 `JS/TS SDK` 的公开面、包导出或运行时装载逻辑时，同步检查 `sdks/javascript/package.json`、`sdks/javascript/README.md`、`docs/user/sdk/javascript.md` 和相关测试资产是否一致。

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

若改动涉及 `apps/monitor/`，在运行该打包入口前额外执行：

```bash
npm --prefix apps/monitor run verify
```

## 3. 外部协作者常用入口

- 首次启动 Host：[`../../user/host/quickstart.md`](../../user/host/quickstart.md)
- 官方 SDK 接入：[`../../user/sdk/README.md`](../../user/sdk/README.md)
- 原始协议接入：[`../../user/protocol/README.md`](../../user/protocol/README.md)
- 发布流程与资产：[`../publishing/README.md`](../publishing/README.md)
- 桌面 Monitor 工作区：[`../../../apps/monitor/README.md`](../../../apps/monitor/README.md)
