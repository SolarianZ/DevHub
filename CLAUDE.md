# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is the **DevHub protocol specification and planning repository**. DevHub is a per-user local daemon that enables communication and orchestration between development tools on a single machine. This repo contains detailed protocol specifications, architecture documentation, and implementation plans - NOT the actual implementation code.

## Repository Structure

| File | Purpose |
|------|---------|
| `Spec.md` | English protocol specification v1.0.1 (Final) - defines wire protocol, RPC methods, data models, JSON schemas |
| `DevHub协议与开发规划.md` | Chinese version with architecture diagrams, development milestones M0-M6, scope/event system design |
| `DevHub_M1细化任务文档.md` | Detailed M1 (MVP) implementation tasks, day-by-day schedule, acceptance criteria |
| `LICENSE` | Project license |

## Technology Stack (M1 Planning)

- **Language**: C# + ASP.NET Core (Minimal API)
- **JSON**: System.Text.Json
- **Project Structure** (proposed):
  - `DevHub.Host` - ASP.NET Core Host
  - `DevHub.Core` - Models, Registry, DefinitionLoader, RpcRouter
  - `DevHub.Tests` - Integration tests

## Key Architecture Concepts

### Core Components

1. **Hub**: Central daemon running per user on localhost
   - HTTP endpoint: `POST /rpc` for JSON-RPC
   - WebSocket endpoint: `/ws` for events (M2+)
   - Listens only on loopback (127.0.0.1)

2. **AppRegistry**: In-memory + file-based registry
   - `AppDefinition`: Static metadata from `apps/definitions/*.json`
   - `AppInstance`: Runtime registrations from running processes

3. **Invocation Queue**: In-memory queue for method calls between tools
   - Request/response and notify patterns
   - Lease-based delivery with retry logic

### Data Models

| Model | Key Fields | Purpose |
|-------|------------|---------|
| `AppDefinition` | `appId`, `displayName`, `scopePolicy`, `launch` | Static app configuration |
| `AppInstance` | `instanceId`, `appId`, `scope`, `pid`, `lastSeenUtc` | Runtime instance state |
| `Invocation` | `invocationId`, `appId`, `target`, `method`, `kind` | In-flight method call |
| `HubRuntime` | `protocolVersion`, `httpBaseUrl`, `wsUrl`, `tokenFile` | Discovery file format |

### Scope Rules (v1)

- `scope` omitted/null = global scope
- Non-empty string = workspace-scoped (exact match)
- String `"global"` is prohibited as a scope value
- **No fallback**: Scoped requests MUST NOT fall back to global

### Important Timeouts

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ttlMs` (notify) | 60s | Invocation lifetime |
| `ttlMs` (request) | 300s | Invocation lifetime |
| `waitTimeoutMs` | 120s | Caller wait timeout |
| `leaseSeconds` | 30s | Delivery lease duration |
| Online threshold | 30s | `lastSeenUtc` recency |
| Dedupe window | 30s | Launch dedupe window |

## RPC Methods (M1 MVP)

| Method | HTTP | Description |
|--------|------|-------------|
| `hub.ping` | ✓ | Connectivity test |
| `hub.apps.listDefinitions` | ✓ | List app definitions |
| `hub.apps.getDefinition` | ✓ | Get single definition |
| `hub.apps.registerInstance` | ✓ | Register runtime instance |
| `hub.apps.heartbeat` | ✓ | Keepalive + update lastSeen |
| `hub.apps.unregisterInstance` | ✓ | Deregister instance |
| `hub.apps.listInstances` | ✓ | Query online instances |

## Error Codes

### Standard JSON-RPC
| Code | Name |
|------|------|
| -32600 | `invalid_request` |
| -32601 | `method_not_found` |
| -32602 | `invalid_params` |
| -32603 | `internal_error` |

### DevHub-Specific (M1)
| Code | Name | When |
|------|------|------|
| -32001 | `unauthorized` | Invalid/missing token |
| -32002 | `forbidden` | scopePolicy violation |
| -32010 | `instance_not_found` | No route or unknown instance |
| -32014 | `app_definition_not_found` | Missing definition file |
| -32099 | `not_supported` | Protocol version mismatch |

## HTTP Header Requirements

All HTTP RPC requests MUST include:

```
Authorization: Bearer <token>
X-DevHub-Protocol: 1
X-DevHub-ClientId: <logical-client-id>
X-DevHub-ClientSessionId: <uuid>
Content-Type: application/json
```

**Important**: HTTP status is always `200 OK` (even for errors). Errors are signaled via JSON-RPC `error` field.

## File System Layout (Windows)

```
%LOCALAPPDATA%\DevHub\
├── runtime\
│   ├── hub.json          # Discovery file (atomically written)
│   └── token.txt         # Bearer token (user-only ACL)
├── apps\
│   ├── definitions\      # AppDefinition JSON files
│   │   └── *.json
│   └── instances\        # Optional instance mirrors
│       └── *.json
└── logs\
    └── *.log
```

## Development Milestones

| Milestone | Goal | Key Deliverables |
|-----------|------|------------------|
| M0 | Documentation freeze | Spec v0, JSON schemas |
| M1 | Hub HTTP basics | `/rpc`, auth, apps, instances (MVP) |
| M2 | Invocation loop | notify/request/poll/respond, autoLaunch |
| M3 | Scope isolation | scopePolicy enforcement |
| M4 | WebSocket + Events | `/ws`, subscribe/unsubscribe |
| M5 | SDKs | .NET + JS/TS SDKs |
| M6 | Governance | Metrics, logging, rate limiting |

## Common Tasks

### Running Tests (Future)
Once the implementation is started, tests would be run via:
```bash
dotnet test DevHub.Tests/
```

### Testing RPC Endpoints Manually
```bash
curl -X POST "http://127.0.0.1:{port}/rpc" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $(cat %LOCALAPPDATA%/DevHub/runtime/token.txt)" \
  -H "X-DevHub-Protocol: 1" \
  -H "X-DevHub-ClientId: TestClient" \
  -H "X-DevHub-ClientSessionId: $(uuidgen)" \
  -d '{"jsonrpc":"2.0","id":"1","method":"hub.ping","params":{}}'
```

## Important Notes

1. **Protocol compliance is critical.** The M1-M6 milestones have specific acceptance criteria. Any implementation must pass the conformance tests defined in the specification.

2. **Scope isolation is strict.** There is NO fallback from scoped to global - this is a key architectural decision to prevent workspace cross-contamination.

3. **HTTP always returns 200.** Even for auth failures or parameter errors - errors are signaled via JSON-RPC error codes.

4. **Token is per-session.** Hub generates a new token on each startup. Clients must re-read `token.txt` after Hub restart.
