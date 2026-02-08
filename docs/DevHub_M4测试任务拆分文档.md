# DevHub M4测试任务拆分文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M4细化任务文档.md](./DevHub_M4细化任务文档.md)
> - [DevHub_M3测试任务拆分文档.md](./DevHub_M3测试任务拆分文档.md)

## 当前状态（截至 2026-02-08）

- M4 测试任务总体状态：**首轮已完成并接入回归入口**。
- 已新增 WS 黑盒模块与白盒事件链路测试，覆盖 M4 必测主路径。
- runner 已由 M1~M3 扩展为 M1~M4。

---

## 0. 测试目标

### 0.1 覆盖目标（M4 必测）
- [x] WS 首条消息必须为 `hub.ws.authenticate`。
- [x] 鉴权前非鉴权请求返回 `unauthorized`。
- [x] 鉴权前非鉴权通知触发断连。
- [x] 鉴权失败（非法 token / 不支持协议）返回规范错误并断连。
- [x] 订阅/取消订阅主链路与幂等行为。
- [x] 事件推送覆盖 `registered`、`delivered`、`completed`、`failed`。

### 0.2 测试层次
- 白盒（C#）：校验事件总线与处理器事件发布钩子。
- 黑盒（Python）：校验 WS 传输、鉴权流程、事件通知行为。

---

## 1. 用例矩阵（M4 -> 测试文件映射）

| 用例编号 | 场景 | 类型 | 责任文件 |
| --- | --- | --- | --- |
| `M4-WS-001` | 首条非鉴权请求（带 id）返回 unauthorized | Python 黑盒 | `tests/test_ws_events.py` |
| `M4-WS-002` | 鉴权前非鉴权通知（无 id）关闭连接 | Python 黑盒 | `tests/test_ws_events.py` |
| `M4-WS-003` | 非法 token 鉴权失败并断连 | Python 黑盒 | `tests/test_ws_events.py` |
| `M4-WS-004` | 鉴权后 subscribe/unsubscribe 成功 | Python 黑盒 | `tests/test_ws_events.py` |
| `M4-WS-005` | 推送 registered/queued/delivered/completed 事件 | Python 黑盒 | `tests/test_ws_events.py` |
| `M4-WS-006` | 推送 invocation.failed 事件 | Python 黑盒（full） | `tests/test_ws_events.py` |
| `M4-WB-001` | 支持事件类型集合与过滤匹配 | C# 白盒 | `src/DevHub.Tests/HubEventBusTests.cs` |
| `M4-WB-002` | 订阅鉴权门禁与取消订阅幂等 | C# 白盒 | `src/DevHub.Tests/HubEventBusTests.cs` |
| `M4-WB-003` | register/unregister 发布实例事件 | C# 白盒 | `src/DevHub.Tests/AppInstanceEventTests.cs` |
| `M4-WB-004` | notify->poll->respond 发布 queued/delivered/completed | C# 白盒 | `src/DevHub.Tests/InvocationEventFlowTests.cs` |
| `M4-WB-005` | notify->poll->respond(error) 发布 failed | C# 白盒 | `src/DevHub.Tests/InvocationEventFlowTests.cs` |

---

## 2. 黑盒任务拆分（Python）

### 2.1 文件与基础设施
- [x] 新增 `tests/test_ws_events.py`。
- [x] 内置轻量 `SimpleWebSocketClient`，使用标准库实现握手与帧收发（避免第三方依赖）。
- [x] 复用 `tests/test_base.py` 的 `RpcClient/TestResult/RpcAssertions`。

### 2.2 事件流测试路径
- [x] 订阅成功后，通过 HTTP 触发实例注册与 invocation 主链路。
- [x] 校验 `hub.event` 通知中的 `params.type` 至少覆盖核心事件集。
- [x] full 模式增加 `invocation.failed` 验证。

---

## 3. 白盒任务拆分（C#）

### 3.1 事件总线测试
- [x] 支持事件类型集合断言。
- [x] 未鉴权订阅拦截断言。
- [x] 类型过滤投递断言。
- [x] 取消订阅幂等与停止投递断言。

### 3.2 处理器事件钩子测试
- [x] `AppInstancesHandler`：注册/注销事件发布断言。
- [x] `InvocationHandler + InvocationStore`：
  - [x] queued/delivered/completed 发布断言。
  - [x] queued/delivered/failed 发布断言。

---

## 4. Runner 接入

- [x] `tests/test_runner.py` 增加 `TestWsEvents` 模块导入与执行。
- [x] runner 标题/覆盖描述升级为 M1~M4。
- [x] full 模式额外执行 `M4-WS-006`（failed 事件流）。

---

## 5. 完成定义（DoD）

- [x] M4 必测用例（WS 鉴权 + 订阅 + 事件主链路）具备自动化覆盖。
- [x] 白盒与黑盒对同一事件类型集合使用一致断言。
- [x] 回归入口可一键纳入 M4 模块。
