# DevHub 文档导航

本文档定义 `docs/` 目录的稳定分类和对外入口，帮助外部用户、SDK 接入方与仓库维护者快速定位合适的文档路径。

## 1. 权威来源

- 公开行为、字段命名、状态转换、错误语义、序列化契约与测试断言，统一以 [`spec/Spec.md`](./spec/Spec.md) 为唯一权威标准。
- `host/tests/README.md` 与 `host/tests/conformance/README.md` 分别是仓库级测试治理与 conformance 的稳定入口，不迁入 `docs/`。

## 2. 受众入口

### Host 上手

- [`guides/getting-started/README.md`](./guides/getting-started/README.md)：首次启动 Host、运行时发现和最小验证入口。
- [`operations/部署与运行指南.md`](./operations/部署与运行指南.md)：当前可直接参考的部署、启动和数据根目录说明。

### SDK 接入

- [`guides/sdk/README.md`](./guides/sdk/README.md)：官方 SDK 接入路径导航。
- [`../sdks/dotnet/README.md`](../sdks/dotnet/README.md)
- [`../sdks/javascript/README.md`](../sdks/javascript/README.md)
- [`../sdks/python/README.md`](../sdks/python/README.md)

### 无 SDK 接入

- [`guides/无SDK接入指南.md`](./guides/无SDK接入指南.md)：直接对接原始协议与自测入口。

### 仓库改造与贡献

- [`guides/contributor/README.md`](./guides/contributor/README.md)：仓库开发、贡献与治理入口。
- [`guides/开发指南.md`](./guides/开发指南.md)：当前开发环境、构建、运行与验证流程。
- [`architecture/DevHub协议与开发规划.md`](./architecture/DevHub协议与开发规划.md)
- [`milestones/DevHub_M6任务文档.md`](./milestones/DevHub_M6任务文档.md)

### 发布与维护

- [`operations/publishing/README.md`](./operations/publishing/README.md)：发布路径、资产约定与 TODO 占位规范入口。
- [`operations/运维排障手册.md`](./operations/运维排障手册.md)：运行与排障说明。

## 3. 分类与落点

- [`spec/`](./spec/)：规范与版本化协议资产。
- [`architecture/`](./architecture/)：架构规划与专题评估。
- [`milestones/`](./milestones/)：当前阶段任务文档。
- [`guides/getting-started/`](./guides/getting-started/)：面向 Host 新用户的上手入口。
- [`guides/sdk/`](./guides/sdk/)：面向官方 SDK 使用者的接入入口。
- [`guides/contributor/`](./guides/contributor/)：面向仓库维护者与贡献者的入口。
- [`guides/开发指南.md`](./guides/开发指南.md)：仓库开发流程。
- [`guides/无SDK接入指南.md`](./guides/无SDK接入指南.md)：原始协议接入路径。
- [`operations/publishing/`](./operations/publishing/)：发布准备、发布流程与发布维护入口。
- [`operations/部署与运行指南.md`](./operations/部署与运行指南.md)：部署、启动与运行时数据说明。
- [`operations/运维排障手册.md`](./operations/运维排障手册.md)：排障与恢复说明。
- [`assets/`](./assets/)：文档静态资源。

## 4. 正式发布前 TODO 占位规范

当正式 GitHub Release 资产、下载链接或安装命令尚未存在时，相关文档统一使用以下写法：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 <资产名称 / 版本号 / 下载链接 / 安装命令>；当前阶段不要填写未发布的版本号、下载地址或仓库外安装命令。
```

使用规则：

- 需要同时说明未来将由哪个发布资产或版本信息替换当前占位。
- 可以保留已经成立的仓库内开发命令、本地验证命令或项目引用方式，但必须明确它们不是正式发布安装入口。
- 详细说明与示例见 [`operations/publishing/README.md`](./operations/publishing/README.md)。

## 5. 维护规则

- 新增文档时，优先放入现有分类目录，不在 `docs/` 根目录平铺新增 Markdown。
- 外部导航发生变化时，同步更新仓库根 [`README.md`](../README.md) 与相关工作区 README。
- 若文档涉及治理口径、兼容冻结、conformance 门禁或版本化资产发布方式，先回到 [`spec/Spec.md`](./spec/Spec.md) 判断其是否属于协议核心契约。
