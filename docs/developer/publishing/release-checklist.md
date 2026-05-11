# DevHub 发布前检查与发布后核验

本文定义 DevHub 在本地 dry-run、preview 发布、main 快照预发布和稳定版发布前后的最小检查项。

## 1. 发布前检查

### 1.1 文档与治理

- `README.md`、`docs/README.md`、Host 上手、SDK 接入和原始协议接入文档导航可用。
- 所有尚未正式发布的版本号、下载链接和安装命令均使用 `TODO(devhub-release)` 占位，没有伪造的发布信息。

### 1.2 代码与验证

若本次改动包含版本号调整，先执行：

```bash
python3 scripts/release/sync_versions.py
```

确认 `eng/Version.props`、`sdks/javascript/package.json`、`sdks/javascript/package-lock.json` 与 `sdks/python/pyproject.toml` 已同步后，再执行统一打包入口：

```bash
python scripts/release/package_release.py --release-id local-dry-run --channel local
```

该命令会通过 release 级编排入口串联并门禁以下步骤：

- `dotnet build host/DevHub.slnx -c Release`
- `dotnet test host/DevHub.slnx -c Release`
- 仓库级 Host smoke 验证
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `npm --prefix sdks/javascript ci`
- `npm --prefix sdks/javascript run build`
- `npm --prefix sdks/javascript test`
- `python -m pip install -e "./sdks/python[test]" requests`
- `python -m pytest sdks/python/tests`
- Host 双变体 / SDK 资产完整性检查、manifest 与 release notes 生成

如需按产物域执行工作流同级别的局部验证，可使用以下组件脚本入口：

```bash
python scripts/release/package_host.py --release-id host-local-check --verify-only
python scripts/release/package_dotnet_sdk.py --release-id dotnet-local-check --verify-only
python scripts/release/package_js_sdk.py --release-id js-local-check --verify-only
python scripts/release/package_py_sdk.py --release-id py-local-check --verify-only
python scripts/release/package_monitor.py --release-id monitor-local-check --verify-only
```

所有 package 脚本都支持 `--help`、`--release-id` 和 `--output-root`；命令行中出现 `--help` 时，脚本只输出能力与参数摘要，不执行验证或打包。

若需要本地核验 preview 或 main 快照预发布的完整 Monitor App 汇总路径，可使用对应渠道：

```bash
python scripts/release/package_release.py --release-id preview-local-dry-run --channel preview
```

若通过 GitHub Actions 执行远端发布：

- 自动发布由 `ci.yml` 的 `publish-release` job 触发；只有发布意图路径所需门禁全部通过，并且 core / Monitor release 资产已在当前 `ci` run 内准备完成，才会调用可复用发布工作流。
- `monitor-validation` 的权威校验入口为 `python3 scripts/release/package_monitor.py --release-id monitor-ci --verify-only`；该 job 负责 Node/Python/Rust/Tauri 依赖准备与诊断上传。
- `workflow_dispatch` 只允许填写 `preview`、`main` 或 `v*` tag 作为 `target_ref`，且目标提交必须已有成功的 `ci` push run；手动发布流程会直接下载该 run 产出的 workflow artifact，不会为同一提交重新执行 Host / SDK / Monitor 的同级验证。若要通过 GitHub UI / CLI 手动触发，`release.yml` 必须存在于仓库默认分支。

### 1.3 资产检查

发布前至少确认：

- `artifacts/release/<release-id>/host/` 下对每个默认 RID 都同时包含 `devhub-host-<rid>.zip` 与 `devhub-host-<rid>-single-file.zip`。
- `artifacts/release/<release-id>/host/` 下不存在 `trimmed` 或其他第三种 Host 变体。
- `artifacts/release/<release-id>/sdk/` 下包含 `.NET`、`JS/TS`、`Python` 三套 SDK 资产，其中 `.NET SDK` 同时包含主包与 DI companion package 的 `nupkg` / `snupkg`。
- preview 与 main 快照预发布在 `artifacts/release/<release-id>/monitor/<targetPlatform>/` 下包含 Monitor bundle、`release-manifest.json`、`release-notes.md` 与 `checks/validation-summary.json`。
- `release-manifest.json` 已为每条 Host 资产写入 `variant = multi-file | single-file`。
- preview 与 main 快照预发布的 `release-manifest.json` 已写入 `monitorPackages`，并为最终发布的 Monitor 分发包写入 `category = monitor-app`、目标平台、Monitor 版本与 JS SDK 版本。
- `release-notes.md` 已把同一 RID 的 Host multi-file / single-file 资产分开展示。
- preview 与 main 快照预发布的 `release-notes.md` 已展示 Monitor Packages 表格。
- `checks/validation-summary.json` 记录了本次验证结果。

## 2. 发布通道专项确认

### 2.1 Preview

- 确认本次目标是刷新当前 preview 通道的最新资产，而不是生成历史快照。
- 确认 release tag 使用 `preview-latest`。
- 确认 `preview` 分支对应提交已经通过 `ci`，且用于发布的 workflow artifact 来自该成功 run。
- 确认 Monitor App 的 Linux、Windows、macOS bundle 已包含在 GitHub Release 资产中。

### 2.2 Main 快照预发布

- 确认 release id / tag 使用 `main-YYYYMMDDTHHMMSSZ-<sha7>` 规则。
- 确认 release notes 中包含本次提交 SHA，便于回溯。
- 确认 `main` 分支对应提交已经通过 `ci`，且用于发布的 workflow artifact 来自该成功 run。
- 确认 Monitor App 的 Linux、Windows、macOS bundle 已包含在 GitHub Release 资产中。

### 2.3 稳定版

- 确认稳定版 tag 已准备好，例如 `v1.0.1`。
- 确认该 tag 对应提交已经通过 `ci`，再进入发布或重跑发布；用于发布的 workflow artifact 应来自该成功 run。
- 确认面向外部用户的安装说明已与本次发布资产对应；如仍保留 TODO 占位，需明确具体占位项及对应发布资产。

## 3. 发布后核验

发布完成后至少执行以下核验：

- 打开 GitHub Release 页面，确认 Host、SDK 与 Monitor 分发包的资产名称、数量与 `release-manifest.json` 的 `assets[]` 一致，并确认根目录 `release-manifest.json`、`release-notes.md` 作为发布辅助文件存在。
- 下载同一 RID 的 multi-file 与 single-file Host ZIP 各一份，确认：
  - multi-file 版解压后包含 `DevHub.Host.dll`、`DevHub.Core.dll` 与依赖侧车文件，且目录整体可直接用于运行。
  - single-file 版解压后包含平台启动文件与必要配置侧车文件，不以多文件 DLL 图形式暴露 Host 主体。
- 抽查 `.NET SDK` 主包、`.NET SDK` DI companion package、`JS/TS SDK`、`Python SDK` 至少各一个资产，确认文件可读且名称与版本一致。
- preview 与 main 快照预发布抽查至少一个 Monitor App bundle，确认目标平台与 `release-manifest.json` 中的条目一致。
- 核对 `release-notes.md` 中的通道、tag 和提交 SHA 与本次发布相符。
- 若为 preview 或 main 预发布，确认 release 被标记为 prerelease。

## 4. 问题排查入口

- 发布流程：[`release-process.md`](./release-process.md)
- 资产布局：[`release-asset-layout.md`](./release-asset-layout.md)
- Host 启动与 smoke 诊断：[`../operations/troubleshooting.md`](../operations/troubleshooting.md)
