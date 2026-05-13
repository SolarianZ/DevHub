# DevHub 发布资产布局与命名

本文定义本地一键打包与 GitHub Release 共用的资产目录结构、命名规则和 manifest 结构。

## 1. 输出目录

本地打包统一输出到：

```text
artifacts/release/<release-id>/
├── host/
│   ├── devhub-host-win-x64.zip
│   ├── devhub-host-win-x64-single-file.zip
│   ├── devhub-host-linux-x64.zip
│   ├── devhub-host-linux-x64-single-file.zip
│   ├── devhub-host-osx-arm64.zip
│   └── devhub-host-osx-arm64-single-file.zip
├── sdk/
│   ├── dotnet/
│   │   ├── DevHub.Sdk.DotNet.<version>.nupkg
│   │   └── DevHub.Sdk.DotNet.<version>.snupkg
│   │   ├── DevHub.Sdk.DotNet.DependencyInjection.<version>.nupkg
│   │   └── DevHub.Sdk.DotNet.DependencyInjection.<version>.snupkg
│   ├── javascript/
│   │   └── devhub-sdk-javascript-<version>.tgz
│   └── python/
│       ├── devhub_sdk_python-<version>.tar.gz
│       └── devhub_sdk_python-<version>-py3-none-any.whl
├── monitor/
│   └── <targetPlatform>/
│       ├── bundle/
│       │   └── ...
│       ├── checks/
│       │   └── validation-summary.json
│       ├── release-manifest.json
│       └── release-notes.md
├── checks/
│   ├── validation-summary.json
│   ├── smoke-host.stdout.log
│   ├── smoke-host.stderr.log
│   └── smoke-runner.log
├── release-manifest.json
└── release-notes.md
```

## 1.1 CI 复用 artifact

发布意图路径会把最终 GitHub Release 所需输入先上传为 workflow artifact，再由发布 workflow 下载复用：

- core release 资产：`release-core-<release-id>`
- Monitor 平台资产：`monitor-assets-ubuntu-latest-<release-id>`、`monitor-assets-windows-latest-<release-id>`、`monitor-assets-macos-latest-<release-id>`

`release-reusable.yml` 下载这些 artifact 后，会把 core 资产还原到 `artifacts/release/<release-id>/`，把三平台 Monitor 资产汇总到 `monitor/<targetPlatform>/`，再刷新 release-level manifest 与 release notes。
GitHub Release 的上传列表由 release-level `release-manifest.json` 的 `assets[]` 驱动，并额外附带根目录下的 `release-manifest.json` 与 `release-notes.md`。

## 2. `release-id` 规则

- `preview` 通道：使用 `preview-latest`
- `main` 快照预发布：使用 `main-YYYYMMDDTHHMMSSZ-<sha7>`
- 稳定版：使用稳定版 tag
- 本地 dry-run：可使用 `local-dry-run` 或其他可读名称

## 3. Host 资产规则

- 每个默认 RID 同时输出两类 framework-dependent Host ZIP：
  - `devhub-host-<rid>.zip`：multi-file 版
  - `devhub-host-<rid>-single-file.zip`：single-file compression 版
- 压缩包内部根目录与 ZIP 文件名保持一致，避免同一 RID 的两个变体解压到相同目录名。
- multi-file 版解压后保留标准 `dotnet publish` 目录布局，包含 `DevHub.Host.dll`、`DevHub.Core.dll` 与依赖侧车文件；使用时必须保留整目录。
- single-file compression 版解压后保留平台启动文件与必要配置侧车文件，不以多文件 DLL 图形式暴露 Host 主体。
- 两类 Host 资产都保持 framework-dependent，不生成 trimmed 变体，也不切换为 self-contained。
- 当前固定支持的 Host 发布 RID：
  - `win-x64`
  - `linux-x64`
  - `osx-arm64`

## 4. SDK 资产规则

- `.NET SDK` 保留 `dotnet pack` 产出的原生文件名，以便与包内版本和符号包保持一致。
- `JS/TS SDK` 保留 `npm pack` 产出的 tarball 文件名。
- `Python SDK` 保留 `python -m build` 产出的 wheel 和 sdist 文件名。
- GitHub Release 当前只承载这些打包结果，不向外部包注册中心发布。

## 5. Monitor App 资产规则

- 所有正式发布渠道都包含 Monitor App 资产。
- CI 发布通过 Linux、Windows、macOS runner 生成 Monitor bundle，并在最终发布前汇总到 `monitor/<targetPlatform>/`。
- 本地 `package_release.py` 打包会为当前机器生成一个 `monitor/<targetPlatform>/` 目录；如需局部输出，可显式传入 `--no-monitor`。
- 每个 Monitor 平台目录包含该平台的 bundle、Monitor manifest、Monitor release notes 和验证摘要。
- release-level manifest 中的 Monitor App 资产使用 `category = monitor-app`，`target` 使用 Monitor manifest 中的 `targetPlatform`，`variant` 使用最终分发包所属的 bundle 分类。
- Monitor 平台 manifest 保留完整平台 bundle 文件清单；release-level manifest 只列出最终上传到 GitHub Release 的 Host、SDK 与 Monitor 分发包文件。

## 6. Manifest 结构

`release-manifest.json` 至少包含以下字段：

```json
{
  "schemaVersion": 1,
  "releaseId": "main-20260406T080000Z-abcdef0",
  "channel": "main-snapshot",
  "releaseTag": "main-20260406T080000Z-abcdef0",
  "commit": "abcdef0123456789",
  "generatedAtUtc": "2026-04-06T08:00:00Z",
  "assets": [
    {
      "name": "devhub-host-win-x64.zip",
      "category": "host",
      "target": "win-x64",
      "variant": "multi-file",
      "path": "host/devhub-host-win-x64.zip",
      "sha256": "..."
    },
    {
      "name": "devhub-host-win-x64-single-file.zip",
      "category": "host",
      "target": "win-x64",
      "variant": "single-file",
      "path": "host/devhub-host-win-x64-single-file.zip",
      "sha256": "..."
    },
    {
      "name": "DevHub Monitor_0.8.0_x64.AppImage",
      "category": "monitor-app",
      "target": "linux-x64",
      "variant": "bundle-appimage",
      "path": "monitor/linux-x64/bundle/appimage/DevHub Monitor_0.8.0_x64.AppImage",
      "monitorVersion": "0.8.0",
      "javascriptSdkVersion": "0.8.0",
      "sha256": "..."
    }
  ],
  "monitorPackages": [
    {
      "targetPlatform": "linux-x64",
      "monitorVersion": "0.8.0",
      "javascriptSdkVersion": "0.8.0",
      "manifestPath": "monitor/linux-x64/release-manifest.json",
      "releaseNotesPath": "monitor/linux-x64/release-notes.md",
      "validationSummaryPath": "monitor/linux-x64/checks/validation-summary.json"
    }
  ],
  "validation": {
    "executed": true,
    "summaryPath": "checks/validation-summary.json"
  }
}
```

约束：

- `assets[].path` 使用相对 `artifacts/release/<release-id>/` 的相对路径。
- `assets[]` 表示最终上传到 GitHub Release 的 payload 资产集合。
- `assets[].sha256` 用于发布后人工核对或自动校验。
- Host 资产条目必须包含 `assets[].variant`，取值限定为 `multi-file` 或 `single-file`。
- Monitor App 资产条目必须包含 `target`、`variant`、`monitorVersion` 与 `javascriptSdkVersion`。
- 包含 Monitor App 资产的发布必须提供 `monitorPackages[]`，用于定位各平台 Monitor manifest、release notes 与验证摘要。
- 根目录 `release-manifest.json` 与 `release-notes.md` 作为发布辅助文件单独上传，不写入 `assets[]`。
- `validation.executed=false` 仅允许出现在显式声明“已由外部流程完成门禁”的受控场景，默认本地打包必须自行执行验证。

## 7. 发布说明文件

`release-notes.md` 应至少包含：

- 发布通道、release id、release tag、提交 SHA
- 资产摘要；同一 RID 的 Host multi-file / single-file 资产通过独立条目与 `Variant` 列区分
- Monitor Packages 表格；包含目标平台、Monitor 版本、JS SDK 版本、平台 manifest 和验证摘要路径
- 验证摘要
- 面向用户的相关入口链接
- Monitor Packages 表中的路径位于组装后的 release 工作目录与 workflow artifact 中，不作为额外 GitHub Release 资产上传。

GitHub Release 正文直接复用该文件，避免 workflow 中再维护另一套手写说明。
