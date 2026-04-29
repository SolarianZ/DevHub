# 发布与维护入口

本分组面向维护 DevHub 对外分发流程的仓库维护者，覆盖发布准备、发布流程、资产命名、发布前检查和发布后核验。

## 入口导航

- [`release-process.md`](./release-process.md)：`preview`、`main` 和稳定版的发布流程、触发方式与 GitHub Release 约定。
- [`release-asset-layout.md`](./release-asset-layout.md)：统一发布资产布局、命名规则和 manifest 结构。
- [`release-checklist.md`](./release-checklist.md)：发布前检查项、发布后核验步骤和 dry-run 入口。
- [`../operations/deployment.md`](../operations/deployment.md)：运行、部署与数据根目录说明。
- [`../operations/troubleshooting.md`](../operations/troubleshooting.md)：运行期排障与恢复说明。
- [`../../README.md`](../../README.md)：文档总入口。

## Workflow 编排基线

- `.github/workflows/ci.yml` 在入口先解析 ref 语义与改动范围。`preview`、`main` 与 `v*` tag push 进入发布意图路径；普通分支 push 与 `pull_request` 进入按改动范围裁剪的验证路径。
- 发布意图路径在 `ci.yml` 内完成发布级门禁后准备可复用 release 资产，并把这些资产上传为 workflow artifact，供后续发布阶段复用。
- `.github/workflows/release.yml` 的 `workflow_dispatch` 只接受 `preview`、`main` 与 `v*` tag，发布前会先确认目标提交存在成功的 `ci` push run，然后复用该 run 产出的资产完成发布。
- `.github/workflows/release-reusable.yml` 负责解析发布元数据、校验 `preview` HEAD 防陈旧条件、下载成功 `ci` run 的资产、刷新最终 manifest/release notes 并执行 GitHub Release 发布。
- GitHub 官方 action 基线统一为 `actions/checkout@v6`、`actions/setup-node@v6`、`actions/setup-dotnet@v5`、`actions/setup-python@v6`、`actions/upload-artifact@v7` 与 `actions/download-artifact@v7`。该组合保持 Node 24 兼容，并继续使用默认压缩 artifact 流程。

## 发布资产占位规范

当以下信息尚未生成时，文档必须使用显式 TODO 占位，而不是填写推测值或示例值：

- 首个稳定版本号
- GitHub Release 下载链接
- Host 与 SDK 的正式安装命令
- 尚未生成的发布资产名称

统一写法如下：

```text
TODO(devhub-release): 正式发布资产可用后，填写 <资产名称 / 版本号 / 下载链接 / 安装命令>；当前不要填写未生成的版本号、下载地址或仓库外安装命令。
```

示例：

```text
TODO(devhub-release): 正式发布资产可用后，填写 Host Windows x64 压缩包下载链接与启动命令；当前不要填写未生成的版本号、下载链接或安装命令。
```

```text
TODO(devhub-release): 正式发布资产可用后，填写 DevHub .NET SDK 的发布资产名称、版本号与安装命令；当前不要填写未生成的版本号、下载链接或仓库外安装命令。
```

约束：

- 已经成立的仓库内开发命令、本地打包命令和测试命令可以保留，但必须明确其适用范围是仓库内开发或本地验证。
- 涉及版本、下载与安装的占位必须说明未来会被哪个 GitHub Release 资产或版本信息替换。
- 本分组中的发布流程、资产布局和检查清单文档必须与 `scripts/release/package_release.py`、`.github/workflows/ci.yml`、`.github/workflows/release.yml` 和 `.github/workflows/release-reusable.yml` 的实际行为保持一致。
