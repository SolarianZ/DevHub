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
│   │   ├── DevHub.Sdk.DotNet.<version>.snupkg
│   │   ├── DevHub.Sdk.DotNet.DependencyInjection.<version>.nupkg
│   │   └── DevHub.Sdk.DotNet.DependencyInjection.<version>.snupkg
│   ├── javascript/
│   │   └── devhub-sdk-javascript-<version>.tgz
│   └── python/
│       ├── devhub_sdk_python-<version>.tar.gz
│       └── devhub_sdk_python-<version>-py3-none-any.whl
├── checks/
│   ├── validation-summary.json
│   ├── smoke-host.stdout.log
│   ├── smoke-host.stderr.log
│   └── smoke-runner.log
├── release-manifest.json
└── release-notes.md
```

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

- `.NET SDK` 保留 `dotnet pack` 产出的原生文件名，以便与主包、DI companion package 及各自符号包保持一致。
- `JS/TS SDK` 保留 `npm pack` 产出的 tarball 文件名。
- `Python SDK` 保留 `python -m build` 产出的 wheel 和 sdist 文件名。
- GitHub Release 当前只承载这些打包结果，不向外部包注册中心发布。

## 5. Manifest 结构

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
- `assets[].sha256` 用于发布后人工核对或自动校验。
- Host 资产条目必须包含 `assets[].variant`，取值限定为 `multi-file` 或 `single-file`。
- `validation.executed=false` 仅允许出现在显式声明“已由外部流程完成门禁”的受控场景，默认本地打包必须自行执行验证。

## 6. 发布说明文件

`release-notes.md` 应至少包含：

- 发布通道、release id、release tag、提交 SHA
- 资产摘要；同一 RID 的 Host multi-file / single-file 资产通过独立条目与 `Variant` 列区分
- 验证摘要
- 面向用户的相关入口链接

GitHub Release 正文直接复用该文件，避免 workflow 中再维护另一套手写说明。
