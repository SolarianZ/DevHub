# DevHub 文档导航

本文档定义 `docs/` 目录的稳定分类、导航入口与 M6 期间的治理口径。

## 1. 权威来源

- 公开行为、字段命名、状态转换、错误语义、序列化契约与测试断言，统一以 [`docs/spec/Spec.md`](./spec/Spec.md) 为唯一权威标准。
- M6 期间“核心目标必需约束 / 发布前可调整约束 / 不应固化的实现或发布策略说明”的分类，以 [`Spec.md` §1.4](./spec/Spec.md#14-m6-期间的规范治理口径) 为准。
- `host/tests/README.md` 与 `host/tests/conformance/README.md` 分别是仓库级测试治理与 conformance 的稳定入口，不迁入 `docs/`。

## 2. 分类与落点

- [`spec/`](./spec/): 规范与版本化协议资产。
  - [`spec/Spec.md`](./spec/Spec.md): 唯一权威规范。
  - [`spec/schema/v1.0.1/README.md`](./spec/schema/v1.0.1/README.md): 版本化 Schema 包。
  - [`spec/protocol-examples/v1.0.1/README.md`](./spec/protocol-examples/v1.0.1/README.md): 原始协议示例。
- [`architecture/`](./architecture/): 架构规划与专题评估。
  - [`architecture/DevHub协议与开发规划.md`](./architecture/DevHub协议与开发规划.md)
  - [`architecture/MCP-Report.md`](./architecture/MCP-Report.md)
- [`milestones/`](./milestones/): 当前阶段任务文档。
  - [`milestones/DevHub_M6任务文档.md`](./milestones/DevHub_M6任务文档.md)
- [`guides/`](./guides/): 面向开发者与接入方的使用文档。
  - [`guides/开发指南.md`](./guides/开发指南.md)
  - [`guides/无SDK接入指南.md`](./guides/无SDK接入指南.md)
- [`operations/`](./operations/): 部署、运行与排障文档。
  - [`operations/部署与运行指南.md`](./operations/部署与运行指南.md)
  - [`operations/运维排障手册.md`](./operations/运维排障手册.md)
- [`assets/`](./assets/): 文档静态资源。

## 3. 维护规则

- 新增文档时，优先放入现有分类目录，不再向 `docs/` 根目录平铺新增 Markdown。
- 若文档涉及治理口径、兼容冻结、conformance 门禁或版本化资产发布方式，先回到 [`Spec.md` §1.4](./spec/Spec.md#14-m6-期间的规范治理口径) 判断其是否属于协议核心契约。
- 若文档变更会影响仓库外部导航，应同步更新仓库根 [`README.md`](../README.md) 与相关工作区 README。
