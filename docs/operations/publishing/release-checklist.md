# DevHub 发布前检查与发布后核验

本文定义 DevHub 在本地 dry-run、preview 发布、main 快照预发布和稳定版发布前后的最小检查项。

## 1. 发布前检查

### 1.1 文档与治理

- `README.md`、`docs/README.md`、Host 上手、SDK 接入和无 SDK 接入文档导航可用。
- 所有尚未正式发布的版本号、下载链接和安装命令均使用 `TODO(devhub-release)` 占位，没有伪造的发布信息。
- `CONTRIBUTING.md`、`CHANGELOG.md`、`SECURITY.md` 可从仓库首页或文档入口发现。

### 1.2 代码与验证

执行统一打包入口：

```bash
python scripts/release/package_release.py --release-id local-dry-run --channel local
```

该命令会串联并门禁以下步骤：

- `dotnet build host/DevHub.slnx -c Release`
- `dotnet test host/DevHub.slnx -c Release`
- 仓库级 Host smoke 验证
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `npm --prefix sdks/javascript ci`
- `npm --prefix sdks/javascript run build`
- `npm --prefix sdks/javascript test`
- `python -m pip install -e "./sdks/python[test]" requests`
- `python -m pytest sdks/python/tests`
- Host / SDK 资产完整性检查、manifest 与 release notes 生成

### 1.3 资产检查

发布前至少确认：

- `artifacts/release/<release-id>/host/` 下包含三个 Host ZIP 资产。
- `artifacts/release/<release-id>/sdk/` 下包含 `.NET`、`JS/TS`、`Python` 三套 SDK 资产。
- `release-manifest.json` 和 `release-notes.md` 已生成。
- `checks/validation-summary.json` 记录了本次验证结果。

## 2. 发布通道专项确认

### 2.1 Preview

- 确认本次目标是刷新当前 preview 通道的最新资产，而不是生成历史快照。
- 确认 release tag 使用 `preview-latest`。

### 2.2 Main 快照预发布

- 确认 release id / tag 使用 `main-<utc-date>-<sha7>` 规则。
- 确认 release notes 中包含本次提交 SHA，便于回溯。

### 2.3 稳定版

- 确认稳定版 tag 已准备好，例如 `v1.0.1`。
- 确认本次发布不再保留面向外部用户的 TODO 安装占位，或明确哪些占位仍待后续收口。

## 3. 发布后核验

发布完成后至少执行以下核验：

- 打开 GitHub Release 页面，确认资产名称、数量与 `release-manifest.json` 一致。
- 下载任意一个 Host ZIP，确认压缩包内存在对应平台的 `DevHub.Host` 启动文件。
- 抽查 `.NET SDK`、`JS/TS SDK`、`Python SDK` 至少各一个资产，确认文件可读且名称与版本一致。
- 核对 `release-notes.md` 中的通道、tag 和提交 SHA 与本次发布相符。
- 若为 preview 或 main 预发布，确认 release 被标记为 prerelease。

## 4. 问题排查入口

- 发布流程：[`release-process.md`](./release-process.md)
- 资产布局：[`release-asset-layout.md`](./release-asset-layout.md)
- Host 启动与 smoke 诊断：[`../运维排障手册.md`](../运维排障手册.md)
