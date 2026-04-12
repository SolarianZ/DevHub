# Changelog

本文记录 DevHub 的公开变更跟踪入口，覆盖稳定版、`main` 快照预发布和 `preview` 通道的发布信息索引。

## 发布通道

- `preview`：滚动更新的 preview release，固定 tag 为 `preview-latest`。
- `main`：命中发布范围且通过 CI 门禁的 `main` push 会生成 GitHub prerelease，tag 形如 `main-YYYYMMDDTHHMMSSZ-<sha7>`。
- 稳定版：使用语义化 `v*` tag 发布。

## Unreleased

- 发布准备文档、统一打包脚本、release manifest 和 GitHub Release 自动化已纳入仓库维护流程。
- 首个正式 GitHub Release 对外发布前，用户文档中的安装命令和下载链接继续使用 `TODO(devhub-release)` 占位。

## 历史版本

```text
TODO(devhub-release): 首个正式稳定版发布后，在此补充版本条目、发布日期、摘要和对应 GitHub Release 链接。
```
