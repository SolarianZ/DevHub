# DevHub M3测试任务拆分文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M3细化任务文档.md](./DevHub_M3细化任务文档.md)
> - [DevHub_M2细化任务文档.md](./DevHub_M2细化任务文档.md)
> - [DevHub_M2测试任务拆分文档.md](./DevHub_M2测试任务拆分文档.md)

## 当前状态（截至 2026-02-08）

- M3 测试任务总体状态：**已完成**。
- M2 测试基线已可复用：
  - Python 已具备 invocation / launch / app instance 主链路测试框架。
  - C# 已具备 routing / store / lease / launch 白盒测试骨架。
- 本轮完成范围：**C# 白盒补齐必要缺口 + Python 黑盒 scope 主链路与 runner 接入已完成并通过（含 `M3-SCOPE-010` 与 full 扩展）**。
- 本轮收尾已完成：日志字段规范校验、空白字符串 scope 样本黑盒覆盖、并发 dedupe 记录细化与文档状态同步。

---

## 0. 文档目标与使用方式

### 0.1 目标
- 将 M3 验收项拆分为可直接执行的测试开发任务。
- 强制覆盖 Scope 规范锚点：Spec §5.5（SCOPE-01~05）、§6.3.5/6.3.8/6.3.9/6.3.10/6.3.11、§7.1。
- 形成黑盒（Python）与白盒（C#）双重验证，降低“语义看似一致但实现漂移”的风险。

### 0.2 使用方式
- 本文档用于测试任务实施，不替代协议规范。
- 若与 `Spec.md` 冲突，以 `Spec.md` 为准。
- 执行顺序建议：先白盒锁语义，再黑盒跑闭环，最后全回归。

---

## 1. 覆盖总览（M3 验收 -> 测试任务映射）

| 用例编号 | 目标 | 测试类型 | 责任文件（建议） |
| -------- | ---- | -------- | ---------------- |
| `M3-SCOPE-001` | register scope omitted/null => Global 生效 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `ScopeParsingTests.cs` |
| `M3-SCOPE-002` | register 显式 scope 精确匹配 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `ScopeParsingTests.cs` |
| `M3-SCOPE-003` | register/list/launch `scope=""` -> `-32602` | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `ScopeParsingTests.cs` + `LaunchScopeTests.cs` |
| `M3-SCOPE-004` | register/list/launch `scope="global"` -> `-32602` | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `ScopeParsingTests.cs` + `LaunchScopeTests.cs` |
| `M3-SCOPE-005` | notify/request `target.scope` omitted/null 仅命中 Global | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |
| `M3-SCOPE-006` | notify/request 显式 scope 不回退 Global | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |
| `M3-SCOPE-007` | notify/request `target.scope` 非法值返回 `-32602` | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `ScopeParsingTests.cs` |
| `M3-SCOPE-008` | case-sensitive 精确匹配 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |
| `M3-SCOPE-009` | `target.instanceId` 优先且不回退 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |
| `M3-SCOPE-010` | offline matrix 在不同 scope 下一致 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |
| `M3-SCOPE-011` | launch dedupe 按 scope 隔离 | Python 黑盒 + C# 白盒 | `tests/test_launch_invocation.py` + `LaunchScopeTests.cs` |
| `M3-SCOPE-012` | poll 投递不跨 scope 泄漏 | Python 黑盒 + C# 白盒 | `tests/test_scope_routing.py` + `InvocationScopeRoutingTests.cs` |

---

## 2. Python 集成测试任务拆分（黑盒）

### 2.1 文件规划（新增 + 扩展）
- [x] 新增 `tests/test_scope_routing.py`。
- [x] 扩展 `tests/test_launch_invocation.py`（dedupe scope 隔离场景）。
- [x] 如有必要，复用并微调 `tests/test_base.py` 的 helper（不破坏 M2 兼容）。

### 2.2 基础能力扩展（复用 `tests/test_base.py`）
- [x] 增加/复用 scope 维度 helper：
  - `register_instance(..., scope=...)`
  - `invoke_notify(..., target_scope=...)`
  - `invoke_request(..., target_scope=...)`
  - `launch_app(..., scope=...)`
- [x] 增加通用断言 helper：
  - `assert_invalid_scope_error(reason)`
  - `assert_items_all_match_scope(scope)`
- [x] 为 M3 增加统一测试前缀（如 `m3-scope-*`），避免与 M2 案例名混淆。

### 2.3 `tests/test_scope_routing.py` 用例任务

#### `M3-SCOPE-001` register omitted/null => Global
- [x] 省略 `instance.scope` 注册实例，断言在 `listInstances(scope=null)` 可见。
- [x] 显式 `instance.scope=null` 注册实例，断言行为与 omitted 完全一致。

#### `M3-SCOPE-002` register 显式 scope 精确匹配
- [x] 注册 `scope="workspace-A"` 实例。
- [x] `listInstances(scope="workspace-A")` 命中。
- [x] `listInstances(scope="workspace-a")` 不命中。

#### `M3-SCOPE-003` register/list/launch scope=`""`
- [x] `registerInstance(scope="")` -> `-32602 invalid_params`。
- [x] `listInstances(scope="")` -> `-32602 invalid_params`。
- [x] `launch(scope="")` -> `-32602 invalid_params`。

#### `M3-SCOPE-004` register/list/launch scope=`"global"`
- [x] `registerInstance(scope="global")` -> `-32602 invalid_params`。
- [x] `listInstances(scope="global")` -> `-32602 invalid_params`。
- [x] `launch(scope="global")` -> `-32602 invalid_params`。

#### `M3-SCOPE-005` notify/request 默认 Global 路由
- [x] 同时注册 Global 实例与 scoped 实例。
- [x] 发 `notify/request` 且 `target.scope` omitted/null。
- [x] 仅 Global 实例可 poll 到 invocation。

#### `M3-SCOPE-006` notify/request 显式 scope 不回退
- [x] 仅注册 Global 实例，发 `target.scope="workspace-A"` 调用。
- [x] 断言不会由 Global 实例消费；按矩阵返回 `instance_not_found` 或进入 pending。

#### `M3-SCOPE-007` target.scope 非法值
- [x] `target.scope=""` -> `-32602 invalid_params`。
- [x] `target.scope="global"` -> `-32602 invalid_params`。

#### `M3-SCOPE-008` 大小写敏感匹配
- [x] 注册 `workspace-A` 与 `workspace-a` 两类实例。
- [x] 两个 scope 的 invocation 互不消费。

#### `M3-SCOPE-009` target.instanceId 优先
- [x] 指定 `target.instanceId` 为不可达实例，且同时存在同 appId 其它实例。
- [x] 断言不回退到其它实例，返回 `target_instance_missing`。

#### `M3-SCOPE-010` offline matrix scope 一致性
- [x] 在 Global 与显式 scope 各执行一次 `queueIfOffline/autoLaunch` 组合矩阵。
- [x] 两者行为应一致，仅目标 scope 不同。

#### `M3-SCOPE-012` poll 不跨 scope 泄漏
- [x] 构造 scoped invocation 后由 Global 实例 poll，断言 items 为空。
- [x] 再由匹配 scope 实例 poll，断言可取到 invocation。

### 2.4 `tests/test_launch_invocation.py` 兼容扩展

#### `M3-SCOPE-011` launch dedupe scope 隔离
- [x] 相同 `appId`，并发 `scope=workspace-A` 与 `scope=workspace-B` launch。
- [x] 断言 dedupe key 不串扰（不同 scope 不应互判 `already_running`）。
- [x] 同一 scope 内重复 launch 仍保持 M2 dedupe 行为。

### 2.5 `tests/test_runner.py` 接入任务
- [x] 新增 `TestScopeRouting` 模块导入与执行入口。
- [x] 执行顺序建议：`app_instances -> scope_routing -> invocation -> launch_invocation`。
- [x] `--fast` 跳过长耗时 lease/offline 等场景，但保留核心 scope 断言。
- [x] `--full` 增加矩阵与并发扩展（含 dedupe scope 隔离压力）。

---

## 3. C# 单元测试任务拆分（白盒）

### 3.1 文件规划（新增）
- [x] `src/DevHub.Tests/ScopeParsingTests.cs`
- [x] `src/DevHub.Tests/InvocationScopeRoutingTests.cs`
- [x] `src/DevHub.Tests/LaunchScopeTests.cs`

### 3.2 `ScopeParsingTests.cs`
- [x] `scope` omitted/null 解析为 Global。
- [x] `scope=""`、`scope="global"` 返回 `invalid_params` + `reason=invalid_scope`。
- [x] `target.scope` 对应返回 `reason=invalid_target_scope`。
- [x] `target.instanceId` 空白/类型异常返回 `reason=invalid_target_instance`。

### 3.3 `InvocationScopeRoutingTests.cs`
- [x] `target.scope=null` 仅命中 Global。
- [x] 显式 `target.scope` 仅命中对应 scope。
- [x] 显式 scope 不回退 Global。
- [x] `target.instanceId` 优先，不回退到 scope/global。
- [x] `workspace-A` 与 `workspace-a` 区分大小写。
- [x] lease 重投递后仍受 scope 过滤约束。

### 3.4 `LaunchScopeTests.cs`
- [x] `launch(scope=null)` 与 omitted 行为一致。
- [x] `scope=""` / `"global"` 返回 `-32602 invalid_params`。
- [x] dedupe key 在不同 scope 间隔离。
- [x] `autoLaunch` 场景调用 `LaunchCoordinator` 时 scope 透传正确。

---

## 4. 测试数据与环境任务

### 4.1 Scope 样本集（M3 固化）
- [x] `null`：Global。
- [x] `workspace-A`：显式 scope（大小写样本 1）。
- [x] `workspace-a`：显式 scope（大小写样本 2）。
- [x] `"   "`：按严格 Spec 视为**合法显式 scope**（不 trim），用于验证“按原值精确匹配”。

### 4.2 实例与资源隔离
- [x] 每个测试用例使用唯一 `instanceId`（uuid/timestamp）。
- [x] 每个测试结束执行 unregister，异常路径也要清理。
- [x] 按 appId 前缀隔离 M2 与 M3 测试数据。

### 4.3 环境稳定性
- [x] 长耗时场景仅放在 `full`，默认模式优先快速稳定反馈。
- [x] 并发 dedupe 用例固定并发度（建议 5~10）并记录每次 `launchId/status`。

---

## 5. 执行顺序与排期建议

### 5.1 迭代顺序
1. 先实现并通过 `ScopeParsingTests.cs`（锁解析语义）。
2. 再实现 `InvocationScopeRoutingTests.cs` / `LaunchScopeTests.cs`（锁路由与 dedupe）。
3. 再实现 Python `test_scope_routing.py` 黑盒闭环。
4. 最后扩展 `test_launch_invocation.py` 与 runner 集成。

### 5.2 日程建议（测试维度）
- Day 1：白盒解析与路由核心测试。
- Day 2：黑盒 scope 矩阵主链路。
- Day 3：launch scope dedupe + 全回归。

---

## 6. 完成定义（DoD）

### 6.1 功能覆盖
- [x] `M3-SCOPE-001 ~ M3-SCOPE-012` 全部落地并可重复执行。
- [x] 黑盒与白盒覆盖矩阵一致，无孤立用例编号。

### 6.2 协议一致性
- [x] `scope` 行为与 Spec §5.5 完全一致。
- [x] 相关错误码与 `error.message` 使用规范字符串。
- [x] `invalid_params` 场景覆盖统一 `error.data.reason` 断言。

### 6.3 可维护性
- [x] 新增测试按 M3 专用文件分层，不污染 M1/M2 历史用例结构。
- [x] 公共逻辑复用 `test_base.py` 与既有测试基础设施，避免重复构造器代码。

---

## 7. 风险与应对

### 7.1 解析器收敛后回归风险
- 风险：统一解析入口可能影响既有 handler 分支。
- 应对：先白盒再黑盒，逐步替换并保持每步可回归。

### 7.2 scope 语义误解风险
- 风险：将 `"   "` 错误地当作非法 scope（与 strict Spec 假设冲突）。
- 应对：为 `"   "` 增加显式白盒 + 黑盒断言，确保不 trim。

### 7.3 并发 dedupe 场景不稳定
- 风险：并发波动导致偶发误判。
- 应对：固定并发度、记录原始响应、允许短时重试机制。

---

## 8. 交付清单（测试阶段）

- [x] 新增 `tests/test_scope_routing.py` 并接入 `tests/test_runner.py`。
- [x] 扩展 `tests/test_launch_invocation.py` 的 scope dedupe 隔离用例。
- [x] 新增 `src/DevHub.Tests/ScopeParsingTests.cs`。
- [x] 新增 `src/DevHub.Tests/InvocationScopeRoutingTests.cs`。
- [x] 新增 `src/DevHub.Tests/LaunchScopeTests.cs`。
- [x] 输出回归报告（`temp/test_results.json` / `temp/test_results.txt` / `temp/test_log.txt`）。
- [x] 与 `DevHub_M3细化任务文档.md` 用例编号一一对应并完成交叉复核。

> 备注：M3 测试任务已完成收尾，白盒/黑盒/runner 回归均通过，文档待办已清零。
