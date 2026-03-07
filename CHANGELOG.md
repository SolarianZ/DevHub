# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

- 修复 Host 从非当前工作目录启动时无法定位 `appsettings.json` 的问题。
- 修复 Host 启动致命错误仍返回 `0` 退出码的问题。
- 移除过期的 `src/DevHub.Host/DevHub.http` 模板文件。

## [1.0.1] - 2026-02-11

### Added
- M1: `/rpc`, discovery (`hub.json`), token auth, app definitions/instances management
- M2: invocation orchestration (`notify/request/poll/respond`), offline queueing, autoLaunch, launch dedupe, lease/ttl/wait-timeout handling
- M3: strict scope routing semantics (global default, explicit scope isolation, invalid scope validation)
- M4: WebSocket auth (`hub.ws.authenticate`), events subscribe/unsubscribe, `hub.event` notifications
- CI workflow for build/test/coverage/smoke (`.github/workflows/ci.yml`)
- Release packaging workflow for linux/win/macos (`.github/workflows/release.yml`)

### Changed
- Host/Core assemblies version metadata aligned to `1.0.1`
- HTTP JSON-RPC notifications (request without `id`) now return HTTP 200 with empty response body
- Windows token/hub 文件 ACL 设置逻辑补充平台注解，消除跨平台分析告警

### Documentation
- README expanded for quickstart/auth/invoke/subscribe/troubleshooting/known limits
- Added release notes: `docs/DevHub_v1.0.1_发布说明.md`
- Added operations runbook: `docs/运维排障手册.md`

### Verified
- White-box: `dotnet test src/DevHub.slnx -c Release` all green
- Black-box: `python3 tests/test_runner.py --full --no-header` all green
