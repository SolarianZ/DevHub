# DevHub M3细化任务文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M1细化任务文档.md](./DevHub_M1细化任务文档.md)
> - [DevHub_M2细化任务文档.md](./DevHub_M2细化任务文档.md)
> - [DevHub_M2测试任务拆分文档.md](./DevHub_M2测试任务拆分文档.md)

## 当前状态（截至 2026-02-08）

- M3 总体状态：**进行中**。
- M1 / M2 已完成并通过既有回归，M3 以此为基线推进。
- 本轮已完成（代码 + 白盒 + 黑盒）：
  - 公共解析器已收敛：`TryGetOptionalScope(...)`、`TryParseInvocationTarget(...)`。
  - `AppInstances/Launch/Invocation` handlers 与 `InvocationRoutingService` 已完成语义收敛。
  - C# 白盒测试补齐必要缺口并通过（含 omitted/null 等价、Global 默认路由、大小写敏感断言）。
  - Python 黑盒 `test_scope_routing.py` 已落地并接入 `test_runner.py`，`M3-SCOPE-001~012` 已覆盖（含 `M3-SCOPE-010` 与 full 扩展）。
- 当前主要剩余缺口：日志字段规范与空白字符串 scope 样本待补齐。

---

## 0. 目标与验收对齐（必须满足）

### 0.1 M3 必须实现（对齐里程碑验收清单）
- [x] 以 Spec §5.5 为准，完整落地 **SCOPE-01 ~ SCOPE-05** 到以下链路：
  - `hub.apps.registerInstance`
  - `hub.apps.listInstances`
  - `hub.apps.launch`
  - `hub.invoke.notify`
  - `hub.invoke.request`
- [x] 调用未指定 `target.scope`（omitted / `null`）时，仅命中 Global（`scope == null`）实例。
- [x] 调用显式指定 `target.scope`（非空字符串）时，仅命中该 scope，禁止 fallback 到 Global。
- [x] `target.instanceId` 指定时只允许命中该实例，不发生 scope/global 回退。
- [x] `scope` / `target.scope` 为 `""` 或 `"global"` 时，统一返回 `-32602 invalid_params`。
- [x] 非空字符串 scope 采用**大小写敏感**精确匹配（如 `workspace-A` ≠ `workspace-a`）。

### 0.2 协议输出约束（M3 继续沿用）
- [x] 外部 RPC 协议**不新增方法、不改字段**，仅收紧现有语义。
- [x] HTTP 状态码保持 `200`，业务错误走 JSON-RPC `error`。
- [x] `error.message` 继续使用 Spec 规范字符串，不引入别名。
- [x] `scope` 字面量策略严格按 Spec：
  - 非法仅包含 `""` 与 `"global"`
  - 不做 trim/lower 收敛
  - 非空字符串按原值大小写敏感匹配

### 0.3 非 M3 范围（必须明确）
- **M4 范围**：`/ws`、`hub.ws.authenticate`、`hub.events.subscribe/unsubscribe`、`hub.event` 推送。
- **M5 范围**：SDK（.NET / JS/TS）与对外契约封装。
- **v2 范围**：Invocation 持久化、事件重放、Hub 重启恢复。

---

## 1. M3 架构落点与重构策略

### 1.1 重构目标（闭环 + 收敛）
- [x] 建立 `scope/target.scope` 的**单一解析入口**，减少 handler 私有分支。
- [x] 建立路由判定的**单一语义入口**，避免不同模块对 Global 的解释不一致。
- [x] 建立 `invalid_params` 的**统一 reason 约束**，便于黑盒测试与诊断。
- [x] 在不改变外部协议的前提下完成内部 DRY 收敛。

### 1.2 文件级改造清单（精确到类）

#### `src/DevHub.Core/Services/Rpc/RpcParamReader.cs`
- [x] 新增统一 `scope` 解析函数（用于 register/list/launch）：
  - 输入：`JsonElement` + 字段名
  - 输出：成功/失败 + `scope`（`null` 或字符串）+ `error.data.reason`
- [x] 新增统一 `target` 解析函数（用于 notify/request）：
  - 解析 `target.scope` / `target.instanceId`
  - 统一返回 `invalid_target_scope` / `invalid_target_instance`。

#### `src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`
- [x] `registerInstance` 与 `listInstances` 改用公共 `scope` 解析器。
- [x] 删除本地重复 `scope` 解析分支，保留业务语义判定。
- [x] 将非法 `scope` 错误统一为 `-32602` + 规范 `reason`。

#### `src/DevHub.Core/Services/Rpc/Handlers/LaunchHandler.cs`
- [x] `scope` 解析改用公共解析器。
- [x] `waitForRegisterMs` 仍保留本地参数边界校验。
- [x] `scope` 非法错误结构与其它 handler 对齐。

#### `src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`
- [x] 移除私有 `TryParseTarget`，改为复用 `RpcParamReader`。
- [x] `notify/request` 在 `target.scope` 非法时返回统一 `reason`。
- [x] 保持离线矩阵、`queueIfOffline + autoLaunch` 业务语义不变。

#### `src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs`
- [x] 移除 `IsNullOrWhiteSpace` 作为 Global 判据。
- [x] 明确规则：
  - `target.Scope == null` -> Global 路由（仅 `instance.Scope == null`）
  - `target.Scope != null` -> 显式 scope 路由（精确匹配）
- [x] 确认 `target.instanceId` 优先于 scope 条件。

### 1.3 公共接口/类型变化（M3 文档约束）
- [x] 外部 RPC 返回结构保持兼容。
- [x] 内部新增类型/方法仅限 `internal` 使用，不外泄公共协议面。
- [ ] 建议新增（命名可按代码风格微调，但语义必须一致）：
  - `TryGetOptionalScope(...)`
  - `TryParseInvocationTarget(...)`
  - `BuildInvalidScopeErrorData(...)`

---

## 2. Scope 语义统一规则（规范映射）

| 规则ID | 规范要求 | M3 实现落点 | M3 验收断言 |
| ------ | -------- | ----------- | ----------- |
| SCOPE-01 | `registerInstance`/`launch` 的 `scope` omitted 或 `null` 解释为 Global | `AppInstancesHandler`、`LaunchHandler` | `scope` 缺省与 `null` 行为一致，不写入非空默认值 |
| SCOPE-02 | `notify/request` 的 `target.scope` omitted 或 `null` 仅命中 Global | `InvocationHandler` + `InvocationRoutingService` | 无 `target.scope` 时不命中任何非 Global 实例 |
| SCOPE-03 | `target.scope` 显式字符串仅命中该 scope，禁止 fallback | `InvocationRoutingService` | 显式 scope 无在线实例时返回 `instance_not_found`，不转 Global |
| SCOPE-04 | `scope` / `target.scope` 为 `""` 或 `"global"` 返回 `invalid_params` | `RpcParamReader` + 各 Handler | 错误码 `-32602`，`error.message=invalid_params`，携带统一 `reason` |
| SCOPE-05 | 非空字符串 scope 大小写敏感精确匹配 | `InvocationRoutingService`、`AppRegistry` 过滤调用方 | `workspace-A` 与 `workspace-a` 可同时存在且互不命中 |

---

## 3. RPC 逐项任务拆分

### 3.1 `hub.apps.registerInstance` / `hub.apps.listInstances`

#### `registerInstance`
- [x] `instance.scope` omitted / `null`：登记为 Global（`scope == null`）。
- [x] `instance.scope` 为非空字符串：按原值登记。
- [x] `instance.scope` 为 `""` 或 `"global"`：返回 `-32602 invalid_params`。
- [x] 非法 scope 错误格式：
  ```json
  {
    "error": {
      "code": -32602,
      "message": "invalid_params",
      "data": { "reason": "invalid_scope" }
    }
  }
  ```

#### `listInstances`
- [x] `scope` omitted / `null`：过滤 Global。
- [x] `scope` 非空字符串：仅过滤该 scope。
- [x] `includeAllScopes=true`：忽略 scope 过滤逻辑，返回所有 scope。
- [x] `scope` 为 `""` 或 `"global"`：返回 `-32602 invalid_params`。

### 3.2 `hub.apps.launch`
- [x] `scope` omitted / `null`：视为 Global 启动维度。
- [x] 显式 `scope`：用于在线判定与 dedupe key 生成。
- [x] `scope` 为 `""` 或 `"global"`：返回 `-32602 invalid_params`。
- [x] dedupe 隔离：同 `appId` 不同 scope 互不串扰。

### 3.3 `hub.invoke.notify` / `hub.invoke.request`

#### 目标解析
- [x] `target` 缺省：等价 `{ scope: null, instanceId: null }`。
- [x] `target.scope` omitted / `null`：仅路由 Global。
- [x] `target.scope` 显式非空字符串：仅路由显式 scope。
- [x] `target.scope` 为 `""` 或 `"global"`：`-32602 invalid_params` + `reason=invalid_target_scope`。

#### 路由行为
- [x] 指定 `target.instanceId` 时：仅路由该实例，优先级高于 scope。
- [x] 指定 `target.instanceId` 且实例不存在：返回 `instance_not_found` + `reason=target_instance_missing`。
- [x] 不指定 `target.instanceId` 时：按 `target.scope`（含 Global 默认）筛选候选实例。

#### 离线矩阵（沿用 M2，补齐 scope 维度）
- [x] `queueIfOffline=false`：无候选实例直接 `instance_not_found`。
- [x] `queueIfOffline=true && autoLaunch=false`：进入 Pending（需 AppDefinition 存在）。
- [x] `queueIfOffline=true && autoLaunch=true`：按**相同 scope**触发 launch。
- [x] 任意 scope 下均遵守“无定义不入队”规则。

---

## 4. 路由矩阵与冲突边界

### 4.1 优先级（必须固定）
1. `target.instanceId`（若指定）
2. `target.scope`（若显式指定）
3. Global 默认路由（`target.scope` omitted / `null`）

### 4.2 路由矩阵（M3）

| 输入条件 | 候选集规则 | 禁止行为 | 失败返回 |
| -------- | ---------- | -------- | -------- |
| 指定 `target.instanceId` | 仅该 `instanceId` | 命中其它实例 | `instance_not_found` (`target_instance_missing`) |
| `target.scope=null/omitted` | 仅 `instance.scope == null` | 命中任意 scoped 实例 | `instance_not_found` (`offline_no_queue` 等) |
| `target.scope="workspace-A"` | 仅 `instance.scope == "workspace-A"` | fallback 到 Global | `instance_not_found` |

### 4.3 冲突边界
- [x] `poll` 投递不得跨 scope 泄漏（Global 实例不得认领显式 scoped invocation）。
- [x] lease 到期重投递后仍需遵守同一 scope 路由条件。
- [x] `queueIfOffline + autoLaunch` 的 launch scope 必须与 invocation target scope 一致。

---

## 5. 错误码与日志要求

### 5.1 错误码要求（M3 增强但不扩码）
- [x] scope 非法统一为：`-32602 invalid_params`。
- [x] 非 scope 业务错误保持现有编码：`instance_not_found` / `forbidden` / `launch_failed` 等。

### 5.2 `error.data.reason` 统一约束（M3）
- [x] `invalid_scope`：用于 `register/list/launch` 的 `scope` 非法。
- [x] `invalid_target_scope`：用于 `notify/request` 的 `target.scope` 非法。
- [x] `invalid_target_instance`：用于 `target.instanceId` 类型或值非法。

### 5.3 日志字段要求（便于排障）
- [ ] 关键路由日志至少包含：
  - `method`
  - `appId`
  - `target.scope`
  - `target.instanceId`
  - `candidateCount`
  - `matchedScope`（global / explicit）
- [ ] 交付前清理临时调试日志，保留稳定运营日志。

---

## 6. 开发排期（1 人可执行）

### Day 0.5：解析器重构骨架
- [x] 在 `RpcParamReader` 增加统一 `scope/target` 解析入口。
- [x] 补齐解析器级单元测试样例（非法值、大小写、omitted/null）。

### Day 1：register/list/launch 收敛
- [x] `AppInstancesHandler` 切换到公共解析器。
- [x] `LaunchHandler` 切换到公共解析器。
- [x] 完成 `invalid_scope` reason 一致化。

### Day 1.5：notify/request 路由收敛
- [x] `InvocationHandler` 复用公共 `target` 解析。
- [x] `InvocationRoutingService` 移除空白字符串 global 判定。
- [x] 校验 `target.instanceId` 优先级不变。

### Day 2：白盒 + 黑盒补齐
- [x] 新增/调整 C# 单测覆盖 M3-SCOPE 核心路径。
- [x] 新增 Python `test_scope_routing.py` 并接入 runner。

### Day 3：回归与文档闭环
- [x] 运行 `dotnet test` + Python `default/fast/full` 回归。
- [x] 运行 `dotnet test` + Python scope 专项 + `test_runner --fast` 回归。
- [x] 更新 M3 文档中的状态/风险/DoD 实际结果。

---

## 7. M3 验收用例（最小可验收）

> 详见《[DevHub_M3测试任务拆分文档](./DevHub_M3测试任务拆分文档.md)》编号明细。

- [x] `M3-SCOPE-001` register: scope omitted/null => Global 生效
- [x] `M3-SCOPE-002` register: 显式 scope 精确匹配
- [x] `M3-SCOPE-003` register/list/launch: scope=`""` -> `-32602`
- [x] `M3-SCOPE-004` register/list/launch: scope=`"global"` -> `-32602`
- [x] `M3-SCOPE-005` notify/request: target.scope omitted/null 仅命中 Global
- [x] `M3-SCOPE-006` notify/request: target.scope 显式仅命中该 scope，不回退 Global
- [x] `M3-SCOPE-007` notify/request: target.scope 非法值返回 `-32602`
- [x] `M3-SCOPE-008` case-sensitive 精确匹配（`workspace-A` ≠ `workspace-a`）
- [x] `M3-SCOPE-009` `target.instanceId` 优先，不发生 scope/global 回退
- [x] `M3-SCOPE-010` offline matrix 在不同 scope 下行为一致
- [x] `M3-SCOPE-011` launch dedupe 在不同 scope 隔离
- [x] `M3-SCOPE-012` poll 投递不跨 scope 泄漏

---

## 8. 边界与风险

### 8.1 与后续里程碑边界
- [ ] M3 不扩展 WS 协议与事件推送（M4）。
- [ ] M3 不处理 invocation 持久化与重放（v2）。
- [ ] M3 不引入 SDK 行为改造（M5）。

### 8.2 风险与应对
- [ ] **风险：解析器收敛引发回归**
  - 应对：先补白盒解析测试，再替换 handler 调用。
- [ ] **风险：scope 语义与历史行为不一致**
  - 应对：黑盒测试固定 `M3-SCOPE-*` 断言，避免隐式回退。
- [ ] **风险：大小写敏感规则误用**
  - 应对：强制加入 `workspace-A` / `workspace-a` 并行测试。

### 8.3 默认值与假设（本阶段固化）
- [ ] 双文档交付：M3 细化 + M3 测试拆分。
- [ ] 文档状态表达：M3 进行中（严格口径，未完成项保持待办）。
- [ ] `scope` 策略严格按 Spec，不做 trim/lower 补偿。
- [ ] 不修改 `Spec.md`，仅新增 M3 文档。

---

## 9. 本轮完成证据（2026-02-08）

- 代码文件：
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Core/Services/Rpc/RpcParamReader.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Core/Services/Rpc/Handlers/LaunchHandler.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs`
- 测试文件：
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/ScopeParsingTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/InvocationScopeRoutingTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/LaunchScopeTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/InvocationRoutingTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/InvocationStoreTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/LaunchCoordinatorTests.cs`
  - `/Users/qiuyu/projects/DevHub/src/DevHub.Tests/SpecConformanceTests.cs`
  - `/Users/qiuyu/projects/DevHub/tests/test_scope_routing.py`
  - `/Users/qiuyu/projects/DevHub/tests/test_launch_invocation.py`
  - `/Users/qiuyu/projects/DevHub/tests/test_runner.py`
- 测试命令（已通过）：
  - `dotnet test src/DevHub.Tests/DevHub.Tests.csproj --filter "FullyQualifiedName~InvocationScopeRoutingTests|FullyQualifiedName~LaunchScopeTests|FullyQualifiedName~InvocationRoutingTests|FullyQualifiedName~LaunchCoordinatorTests|FullyQualifiedName~ScopeParsingTests|FullyQualifiedName~InvocationStoreTests"`
  - `DEVHUB_RUNTIME_DIR=/tmp/devhub-m3-runtime-launch001 DEVHUB_APPDEFS_DIR=/tmp/devhub-m3-appdefs-launch001 python3 tests/test_scope_routing.py`
  - `DEVHUB_RUNTIME_DIR=/tmp/devhub-m3-runtime-launch001 DEVHUB_APPDEFS_DIR=/tmp/devhub-m3-appdefs-launch001 python3 tests/test_runner.py --fast --no-header`
  - `DEVHUB_RUNTIME_DIR=/tmp/devhub-m3-runtime-launch001 DEVHUB_APPDEFS_DIR=/tmp/devhub-m3-appdefs-launch001 python3 tests/test_runner.py --full --no-header`
