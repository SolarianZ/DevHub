# DevHub 发布流程

本文定义 DevHub 对外发布的三条路径：`preview` 通道、`main` 快照预发布和稳定版发布，并说明每条路径在 GitHub Release 中的呈现方式。

## 1. 发布通道

### 1.1 Preview release

- 触发条件：向 `preview` 分支推送匹配发布范围的改动后，`ci.yml` 会先进入发布意图路径，执行发布级门禁、准备 core release 资产与 Monitor 三平台资产，再调用可复用发布工作流自动发布。
- GitHub Release：固定使用 `preview-latest` 这一条 preview release，并在每次成功发布时刷新到最新提交。
- 用途：为外部试用、联调或预览验证提供“当前预览通道的最新资产”。
- 资产性质：`prerelease = true`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=preview` 触发；工作流会先校验目标提交存在成功的 `ci` push run，再下载该 run 产出的 release 资产完成发布。GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

### 1.2 Main 快照预发布

- 触发条件：向 `main` 分支推送匹配发布范围的改动后，`ci.yml` 会先进入发布意图路径，执行发布级门禁、准备 core release 资产与 Monitor 三平台资产，再调用可复用发布工作流自动发布。
- GitHub Release：为当前提交创建唯一可追溯的 prerelease，tag 命名规则为 `main-YYYYMMDDTHHMMSSZ-<sha7>`。
- 用途：保留主线每次命中发布范围且通过门禁的推送对应的可回溯快照资产。
- 资产性质：`prerelease = true`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=main` 触发；工作流会先校验目标提交存在成功的 `ci` push run，再下载该 run 产出的 release 资产完成发布。GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

### 1.3 稳定版发布

- 触发条件：推送语义化 `v*` tag 后，`ci.yml` 会先进入发布意图路径，执行 stable 发布级门禁、准备 core release 资产，再调用可复用发布工作流自动发布。
- GitHub Release：使用稳定版 tag 作为 release tag。
- 用途：承载面向外部用户的稳定版分发资产。
- 资产性质：`prerelease = false`。
- 受控重跑：如需手动重跑，通过 `release.yml` 的 `workflow_dispatch` 以 `target_ref=<v*>` 触发；工作流会先校验目标提交存在成功的 `ci` push run，再下载该 run 产出的 release 资产完成发布。GitHub 的手动运行入口要求该 workflow 文件存在于仓库默认分支。

### 1.4 验证专用路径

- `pull_request` 与非 `preview` / `main` / `v*` 的普通分支 push 进入验证专用路径。
- 路由层先计算改动范围，再决定是否运行 Host 主验证、`.NET` / `JS/TS` / `Python` SDK、Monitor 与 conformance 等 job。
- `integration-full-gate`、`cross-platform-smoke`、release 资产准备与发布链路只在发布意图 ref 上执行。

## 2. 发布关键验证

发布意图路径在准备 release 资产前必须通过以下最小验证集：

- `dotnet build host/DevHub.slnx -c Release`
- `dotnet test host/DevHub.slnx -c Release`
- 仓库级 Host smoke 验证
- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `npm --prefix sdks/javascript test`
- `python -m pytest sdks/python/tests`
- Monitor 权威校验入口 `python3 scripts/release/package_monitor.py --release-id monitor-ci --verify-only`
- 发布资产准备阶段的 Host 双变体、manifest、release notes 与 SDK 资产完整性检查

发布级统一执行入口见 [`release-checklist.md`](./release-checklist.md) 和仓库脚本 `scripts/release/package_release.py`。按产物域单独验证或打包时，使用 `scripts/release/package_host.py`、`package_dotnet_sdk.py`、`package_js_sdk.py`、`package_py_sdk.py` 与 `package_monitor.py`。

## 3. GitHub Release 资产

每次发布都上传以下资产类型：

- Host 双变体平台压缩包：每个默认 RID 同时上传 `devhub-host-<rid>.zip` 与 `devhub-host-<rid>-single-file.zip`，覆盖 `win-x64`、`linux-x64` 与 `osx-arm64`
- `.NET SDK`：`DevHub.Sdk.DotNet.<version>.nupkg`、`DevHub.Sdk.DotNet.<version>.snupkg`、`DevHub.Sdk.DotNet.DependencyInjection.<version>.nupkg` 与 `DevHub.Sdk.DotNet.DependencyInjection.<version>.snupkg`
- `JS/TS SDK`：`devhub-sdk-javascript-<version>.tgz`
- `Python SDK`：`devhub_sdk_python-<version>.tar.gz` 与 `devhub_sdk_python-<version>-py3-none-any.whl`
- Monitor App：preview 与 main 快照预发布上传 Linux、Windows、macOS 平台的 Tauri bundle 资产
- 资产清单：`release-manifest.json`
- 发布说明：`release-notes.md`

Host 两类 ZIP 都保持 framework-dependent。multi-file 版用于标准目录发布；single-file 版启用 `EnableCompressionInSingleFile=true`，但不引入 trimmed 或 self-contained 分发模式。

稳定版 `v*` 发布上传 Host 与三套 SDK 资产。preview 与 main 快照预发布的 `release-manifest.json` 和 `release-notes.md` 同时列出 Monitor App 资产、目标平台、Monitor 版本和 JS SDK 版本。

详细目录结构和 manifest 字段定义见 [`release-asset-layout.md`](./release-asset-layout.md)。

## 4. 本地一键打包与 CI 的关系

- 仓库发布版本统一以 `eng/Version.props` 为唯一来源；Host 与 `.NET SDK` 直接消费该文件，`JS/TS SDK` 与 `Python SDK` 包元数据通过 `python3 scripts/release/sync_versions.py` 与之保持同步。
- `scripts/release/package_release.py` 会在打包开始前执行版本一致性校验，发现 `package.json`、`package-lock.json` 或 `pyproject.toml` 与 `eng/Version.props` 漂移时直接失败。
- 本地维护者通过 `python scripts/release/package_release.py --release-id <id> --channel <channel>` 生成完整发布候选资产；该入口会在同一进程内依次调用 Host、`.NET SDK`、`JS/TS SDK`、`Python SDK` 和按渠道决定是否纳入的 Monitor 组件打包函数，为每个默认 Host RID 同时生成 multi-file 与 single-file compression 两类 ZIP，不生成 trimmed Host 资产。`preview` 与 `main-snapshot` 渠道还会在当前机器生成 Monitor App 资产，并写入 `monitor/<targetPlatform>/`。
- 仓库提供以下组件级入口，用于单域验证或局部打包：`python scripts/release/package_host.py --release-id <id>`、`python scripts/release/package_dotnet_sdk.py --release-id <id>`、`python scripts/release/package_js_sdk.py --release-id <id>`、`python scripts/release/package_py_sdk.py --release-id <id>`、`python scripts/release/package_monitor.py --release-id <id>`。
- 所有 package 脚本共用 `--help`、`--release-id` 和 `--output-root` 参数约定；命令行中出现 `--help` 时直接输出能力和参数摘要，不执行验证、目录删除或打包逻辑。具备“只验证不产物化”语义的脚本支持 `--verify-only`。
- `.github/workflows/ci.yml` 在入口先解析 ref 与改动范围。普通分支 / PR 只运行按范围裁剪的验证路径；`preview` / `main` / `v*` tag push 进入发布意图路径。该路径在门禁通过后使用 `package_release.py --validated-externally` 与 `package_monitor.py --validated-externally` 准备可复用 release 资产，并将其上传为 workflow artifact。Monitor release artifact 必须保留隐藏文件，保证 Linux AppImage bundle 在跨 job 下载后仍与平台 `release-manifest.json` 保持一致。
- `.github/workflows/release.yml` 只保留 `workflow_dispatch` 手动发布入口，负责把 `target_ref` 归一化后再调用 `.github/workflows/release-reusable.yml`；调用前会校验目标提交已经存在成功的 `ci` push run。若需要在 GitHub UI / CLI 中手动触发，还必须保证该 workflow 文件存在于仓库默认分支。
- `.github/workflows/release-reusable.yml` 集中承载发布通道解析、preview 防陈旧保护、成功 `ci` run 查询、workflow artifact 下载、release manifest 汇总与 GitHub Release 发布；该 workflow 不会为同一提交重新执行 Host / SDK / Monitor 的同级验证。
- 当前发布流程只生成并上传 GitHub Release 资产，不会同步把 `.NET SDK` 发布到 NuGet、把 `JS/TS SDK` 发布到 npm，或把 `Python SDK` 发布到 PyPI。
- `apps/monitor/` 使用 `python scripts/release/package_monitor.py --release-id <id>` 生成单平台 Monitor bundle、manifest、release notes 与验证摘要。该脚本固定使用当前仓库 `sdks/javascript` 源码，并在打包前执行 `npm run sync:version-metadata`，保证 `--validated-externally` 路径在干净工作区内也具备完整输入。

## 5. 发布说明与 TODO 占位

- 正式版本号、下载链接和安装命令尚未对外冻结时，用户文档必须使用 `TODO(devhub-release)` 占位。
- `release-notes.md` 负责描述本次发布对应的通道、提交、资产和验证摘要；Host 同一 RID 的 multi-file 与 single-file 资产在该文件中以独立条目呈现，不取代面向用户的安装文档。

## 6. 维护者入口

- 发布前检查与发布后核验：[`release-checklist.md`](./release-checklist.md)
- 发布资产布局：[`release-asset-layout.md`](./release-asset-layout.md)
- 仓库贡献与改造入口：[`../guides/contribution.md`](../guides/contribution.md)
