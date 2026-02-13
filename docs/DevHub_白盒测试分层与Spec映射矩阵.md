# DevHub 白盒测试分层与 Spec 映射矩阵

更新时间：2026-02-13  
适用分支：`review_before_m5`  
规范基线：`docs/Spec.md`（v1.0.1，2026-01-31）

## 1. 分层规则

- `Spec_*`：协议行为测试，必须绑定 `SpecRef`（条款号），并参与 Spec 符合性统计。
- `Impl_*`：实现行为测试（组件/回归/健壮性），不计入 Spec 符合性统计。
- 测试类必须声明 `[Trait("Category", "Spec"|"Impl")]`，并与类内测试方法命名保持一致。

## 2. 守门机制

- 规则守门测试：`src/DevHub.Tests/TestGovernanceTests.cs`
- 守门项：
  - 测试方法必须以 `Spec_` 或 `Impl_` 开头。
  - `Spec_*` 必须声明 `SpecRef`，且条款号与方法名一致。
  - 测试类 `Category` 必须与类内方法分层一致。

## 3. 当前 Spec 映射清单（协议行为层）

| Spec 条款 | 测试文件 | 用例数 |
| --- | --- | --- |
| 4.2 | `src/DevHub.Tests/TransportValidationTests.cs` | 12 |
| 4.3 | `src/DevHub.Tests/TransportValidationTests.cs` | 12 |
| 6.1 | `src/DevHub.Tests/TransportValidationTests.cs` | 7 |
| 6.2 | `src/DevHub.Tests/TransportValidationTests.cs` | 2 |
| 6.3.6 | `src/DevHub.Tests/AppInstancesHeartbeatSpecTests.cs` | 2 |
| 6.3.14 | `src/DevHub.Tests/TransportValidationTests.cs` | 6 |
| 6.3.15 | `src/DevHub.Tests/TransportValidationTests.cs` | 1 |
| 6.3.16 | `src/DevHub.Tests/HubEventNotificationFactoryTests.cs` | 2 |
| 8.3 | `src/DevHub.Tests/TransportValidationTests.cs` | 1 |

总计：45 个 `Spec_*` 白盒测试用例。

## 4. 说明

- 本矩阵仅覆盖“协议可观察行为层”白盒测试。
- 组件内部行为（如计时器、缓存、内部队列、内部清理路径）归入 `Impl_*`，避免实现细节污染 Spec 符合性结论。
