# DevHub M6细化任务文档

---

> 参考文档：
> - [Spec.md](../spec/Spec.md)
> - [DevHub协议与开发规划.md](../architecture/DevHub协议与开发规划.md)
> - [开发指南.md](../guides/开发指南.md)
> - [部署与运行指南.md](../operations/部署与运行指南.md)

## 当前状态

- M0~M5 已完成，Hub、`.NET SDK`、`JS/TS SDK`、`Python SDK`、Host 黑盒测试、跨语言 conformance 与 CI 门禁均已具备可工作的基线。
- 当前仓库主干布局为：`host/DevHub.slnx` 作为 Host 工作区入口，`host/src/` 承载 Host 生产代码，`host/tests/whitebox/` 承载 .NET 白盒测试工程，`host/tests/` 其余目录承载 Python 黑盒/集成/conformance 与验证资产，`sdks/` 下按语言拆分 SDK 工作区，`docs/` 下按 `spec/`、`architecture/`、`milestones/`、`guides/`、`operations/`、`assets/` 分类维护规范、规划、里程碑、指南与运维资料。
- 当前不以“新增协议能力”为主要目标，而是围绕“整理、优化、完善”推进发布前收尾，重点治理仓库目录、模块边界、依赖关系、测试分层和文档归档。
- 当前公开基线已吸纳发布前必须完成的安全收敛：Host 与三套官方 SDK 统一支持 `hub.apps.validateDefinition` / `hub.apps.upsertDefinition` / `hub.apps.deleteDefinition`、`app.definition.upserted` / `app.definition.deleted`，以及带实例级 `password` 的 `hub.apps.registerInstance` / `hub.apps.unregisterInstance`。
- 项目尚未正式发布，允许做必要的破坏性调整；但必须区分“协议核心契约”和“发布前可调整约束”：不得通过修改 Spec 掩盖核心协议问题，但若识别出不贴合核心目标的冻结承诺、兼容口径或发布策略约束，应先修正规范，再同步实现、测试与文档资产。
- 默认工作方式是“先审查并形成目标结构，再成批调整，再以完整本地验证确认收敛”，避免在未统一边界的情况下持续小修小补。

## 假设与默认选择

- 默认项目当前阶段以“内部优先、先把闭环做稳”为主，不以外部生态冻结承诺为首要目标。
- 默认 M6 以“整理与收敛”为主，不主动扩充协议能力。
- 默认允许对未发布 SDK API、测试布局和文档结构做破坏性调整。
- 默认 `docs/spec/Spec.md` 仍是唯一权威标准，任何实现与测试都必须向它对齐；但其中的发布策略与兼容冻结约束在正式发布前允许按核心目标修订。
- 默认 M6 结束时，仓库应进入“可发布前冻结”的状态，而不是继续保留明显的结构性债务。
