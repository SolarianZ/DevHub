# DevHub 发布流程

本文定义 DevHub 对外发布的三条路径：`preview` 通道、`main` 快照预发布和稳定版发布，并说明每条路径在 GitHub Release 中的呈现方式。

## 1. 发布通道

### 1.1 Preview release

- 触发条件：向 `preview` 分支推送匹配发布范围的改动后，`ci.yml` 会先完成全部门禁，再在末尾调用可复用发布工作流自动发布。
- GitHub Release：固定使用 `preview-latest` 这一条 preview release，并在每次成功发布时刷新到最新提交。
- 用途：为外部试用、联调或预览验证提供“当前预览通道的最新资产”。
- 资产性质：`prerelease = true`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=preview` 触发，并在发布前校验目标提交已经有成功的 `ci`；GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

### 1.2 Main 快照预发布

- 触发条件：向 `main` 分支推送匹配发布范围的改动后，`ci.yml` 会先完成全部门禁，再在末尾调用可复用发布工作流自动发布。
- GitHub Release：为当前提交创建唯一可追溯的 prerelease，tag 命名规则为 `main-YYYYMMDDTHHMMSSZ-<sha7>`。
- 用途：保留主线每次命中发布范围且通过门禁的推送对应的可回溯快照资产。
- 资产性质：`prerelease = true`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=main` 触发，并在发布前校验目标提交已经有成功的 `ci`；GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

### 1.3 稳定版发布

- 触发条件：推送语义化 `v*` tag 后，`ci.yml` 会先完成全部门禁，再在末尾调用可复用发布工作流自动发布。
- GitHub Release：使用稳定版 tag 作为 release tag。
- 用途：承载面向外部用户的稳定版分发资产。
- 资产性质：`prerelease = false`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=<v*>` 触发，并在发布前校验目标提交已经有成功的 `ci`；GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

## 2. 发布关键验证

发布前必须通过以下最小验证集：

- `dotnet build host/DevHub.slnx -c Release`
- `dotnet test host/DevHub.slnx -c Release`
- 仓库级 Host smoke 验证
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `npm --prefix sdks/javascript test`
- `python -m pytest sdks/python/tests`
- 本地一键打包脚本中的资产完整性检查

统一执行入口见 [`release-checklist.md`](./release-checklist.md) 和仓库脚本 `scripts/release/package_release.py`。

## 3. GitHub Release 资产

每次发布都上传以下资产类型：

- Host 多平台压缩包：`devhub-host-win-x64.zip`、`devhub-host-linux-x64.zip`、`devhub-host-osx-arm64.zip`
- `.NET SDK`：`DevHub.Sdk.DotNet.<version>.nupkg`、`DevHub.Sdk.DotNet.<version>.snupkg`、`DevHub.Sdk.DotNet.DependencyInjection.<version>.nupkg` 与 `DevHub.Sdk.DotNet.DependencyInjection.<version>.snupkg`
- `JS/TS SDK`：`devhub-sdk-javascript-<version>.tgz`
- `Python SDK`：`devhub_sdk_python-<version>.tar.gz` 与 `devhub_sdk_python-<version>-py3-none-any.whl`
- 资产清单：`release-manifest.json`
- 发布说明：`release-notes.md`

详细目录结构和 manifest 字段定义见 [`release-asset-layout.md`](./release-asset-layout.md)。

## 4. 本地一键打包与 CI 的关系

- 仓库发布版本统一以 `eng/Version.props` 为唯一来源；Host 与 `.NET SDK` 直接消费该文件，`JS/TS SDK` 与 `Python SDK` 包元数据通过 `python3 scripts/release/sync_versions.py` 与之保持同步。
- `scripts/release/package_release.py` 会在打包开始前执行版本一致性校验，发现 `package.json`、`package-lock.json` 或 `pyproject.toml` 与 `eng/Version.props` 漂移时直接失败。
- 本地维护者统一通过 `python scripts/release/package_release.py --release-id <id> --channel <channel>` 生成完整发布候选资产。
- `.github/workflows/ci.yml` 在 `preview` / `main` / `v*` tag 的 `push` 场景下，如果工作流被触发且 `build-and-test`、`sdk-dotnet-tests`、`sdk-ts-tests`、`monitor-tests`、`sdk-python-tests`、`sdk-conformance`、`integration-full-gate`、`cross-platform-smoke` 全部通过，会调用 `.github/workflows/release-reusable.yml`，复用同一套打包与发布逻辑完成自动发布。
- `.github/workflows/release.yml` 只保留 `workflow_dispatch` 手动重跑入口，负责把 `target_ref` 归一化后再调用 `.github/workflows/release-reusable.yml`；调用前会校验目标提交已经通过 `ci`。若需要在 GitHub UI / CLI 中手动触发，还必须保证该 workflow 文件存在于仓库默认分支。
- `.github/workflows/release-reusable.yml` 集中承载发布通道解析、preview 防陈旧保护、资产打包与 GitHub Release 发布，避免自动发布与手动重跑重复维护两套脚本。
- 当前发布流程只生成并上传 GitHub Release 资产，不会同步把 `.NET SDK` 发布到 NuGet、把 `JS/TS SDK` 发布到 npm，或把 `Python SDK` 发布到 PyPI。

## 5. 发布说明与 TODO 占位

- 正式版本号、下载链接和安装命令尚未对外冻结时，用户文档必须使用 `TODO(devhub-release)` 占位。
- `release-notes.md` 负责描述本次发布对应的通道、提交、资产和验证摘要，不取代面向用户的安装文档。

## 6. 维护者入口

- 发布前检查与发布后核验：[`release-checklist.md`](./release-checklist.md)
- 发布资产布局：[`release-asset-layout.md`](./release-asset-layout.md)
- 仓库贡献与改造入口：[`../guides/contribution.md`](../guides/contribution.md)
