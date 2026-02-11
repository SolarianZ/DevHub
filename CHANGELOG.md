# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### Added
- GitHub Actions CI workflow (`.github/workflows/ci.yml`)
- GitHub Actions release packaging workflow (`.github/workflows/release.yml`)
- Root release documents: `README.md`, `CHANGELOG.md`

### Changed
- Added explicit version metadata to Host/Core project files (`1.0.1`)
- Annotated Windows-only ACL method with platform attribute to eliminate CA1416 analyzer warnings
- Relaxed Spec invocation `args` model to allow any JSON value (`object/array/string/number/boolean/null`)
- HTTP JSON-RPC notifications (requests without `id`) now return `200` with empty body (no JSON-RPC response payload)

## [1.0.1] - 2026-02-11

### Added
- M1: `/rpc`, discovery (`hub.json`), token auth, app definitions/instances management
- M2: invocation orchestration (`notify/request/poll/respond`), offline queueing, autoLaunch, launch dedupe, lease/ttl/wait-timeout handling
- M3: strict scope routing semantics (global default, explicit scope isolation, invalid scope validation)
- M4: WebSocket auth (`hub.ws.authenticate`), events subscribe/unsubscribe, `hub.event` notifications

### Verified
- White-box: `dotnet test src/DevHub.slnx -c Release` all green
- Black-box: `python3 tests/test_runner.py --full --no-header` all green
