# DevHub M1~M4 对外发布准备评估报告（不含 SDK）

> 评估日期：2026-02-11  
> 评估分支：`m4`  
> 判定口径：严格口径（功能闭环 + 测试通过 + 发布工程项齐备）

---

## 1. 评估结论

- M1~M4 功能已完成，可运行闭环。
- 对外发布前的工程化阻塞项已补齐（CI/CD、发布文档、版本元数据、平台告警处理）。
- 在不含 SDK（M5）的范围内，当前代码基线可进入对外发布流程。

---

## 2. 里程碑完成度（代码证据）

### 2.1 M1（HTTP 基础 + 发现/鉴权 + apps）

- `/rpc`：`src/DevHub.Host/Program.cs`
- token：`src/DevHub.Core/Services/FileSystemManager.cs`
- `hub.json`：`src/DevHub.Core/Services/FileSystemManager.cs`
- definitions：`src/DevHub.Core/Services/Rpc/Handlers/AppDefinitionsHandler.cs`
- instances：`src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`

### 2.2 M2（invoke 闭环 + 离线矩阵 + autoLaunch + lease）

- `hub.invoke.*`：`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`
- invocation 存储/租约：`src/DevHub.Core/Services/Invocation/InvocationStore.cs`
- 启动协调：`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs`
- 超时扫描：`src/DevHub.Core/Services/Invocation/InvocationTimeoutWorker.cs`

### 2.3 M3（scope 一致性与隔离）

- scope 统一解析：`src/DevHub.Core/Services/Rpc/RpcParamReader.cs`
- scope 路由：`src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs`

### 2.4 M4（WS 鉴权 + 订阅 + 事件）

- `/ws`：`src/DevHub.Host/Program.cs`
- 首条鉴权：`src/DevHub.Host/WebSocketSessionHandler.cs`
- 订阅管理：`src/DevHub.Core/Services/Events/HubEventBus.cs`
- 事件通知：`src/DevHub.Host/Transport/HubEventNotificationFactory.cs`

---

## 3. 验证记录

- `dotnet test src/DevHub.slnx -c Release`：168/168 通过
- `python3 tests/test_runner.py --full --no-header`：137/137 通过
- 黑盒报告：`temp/test_results.json`（`mode=full`，`failed=0`）
- 发布烟测：`dotnet publish src/DevHub.Host/DevHub.Host.csproj -c Release -o /tmp/devhub-publish-check` 成功

---

## 4. 既有发布阻塞项处理结果

1. 缺少 CI/CD 工作流：已补齐  
   - `.github/workflows/ci.yml`
   - `.github/workflows/release.yml`

2. 缺少对外发布文档与变更记录：已补齐  
   - `README.md`
   - `CHANGELOG.md`

3. 缺少版本元数据：已补齐  
   - `src/DevHub.Core/DevHub.Core.csproj`
   - `src/DevHub.Host/DevHub.Host.csproj`

4. CA1416 平台告警：已处理  
   - `src/DevHub.Core/Services/FileSystemManager.cs` 增加平台注解，Windows-only ACL 调用不再触发跨平台误报

---

## 5. 说明

- 本报告评估范围严格限定 M1~M4，不包含 SDK（M5）。
- 协议对齐以 `docs/Spec.md`（v1.0.1）为准，未修改 Spec 文档。
