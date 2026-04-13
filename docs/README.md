# DevHub 文档导航

本文档说明 `docs/` 目录的权威入口、分类规则与常用查找路径，帮助接入方、仓库维护者和发布人员快速定位正确文档。

## 1. 权威来源图

| 主题 | 权威入口 | 说明 |
| --- | --- | --- |
| 协议契约 | [`spec/Spec.md`](./spec/Spec.md) | 公开行为、字段命名、状态转换、错误语义、序列化契约与测试断言 |
| 架构边界 | [`architecture/DevHub协议与开发规划.md`](./architecture/DevHub协议与开发规划.md) | 模块职责、设计取舍与长期演进原则 |
| 稳定使用方式 | [`guides/`](./guides/) | Host 上手、SDK 接入、无 SDK 接入、开发与贡献指南 |
| 运维与发布 | [`operations/`](./operations/) | 部署、排障、发布流程、资产命名与检查清单 |
| 文档资源 | [`assets/`](./assets/) | 文档静态资源 |

补充入口：

- [`host/tests/README.md`](../host/tests/README.md)：仓库级测试分层、验证入口与执行说明。
- [`host/tests/conformance/README.md`](../host/tests/conformance/README.md)：conformance 资产、adapter manifest 与向量运行说明。

## 2. 按场景查找

### Host 上手

- [`guides/getting-started/README.md`](./guides/getting-started/README.md)：Host 上手路径总入口。
- [`guides/getting-started/host-quickstart.md`](./guides/getting-started/host-quickstart.md)：启动 Host、读取 `hub.json` / `tokenFile` 与最小 `hub.ping` 验证。
- [`operations/部署与运行指南.md`](./operations/部署与运行指南.md)：部署、启动、数据根目录与上线后检查。

### SDK 接入

- [`guides/sdk/README.md`](./guides/sdk/README.md)：官方 SDK 接入总入口。
- [`guides/sdk/dotnet.md`](./guides/sdk/dotnet.md)
- [`guides/sdk/javascript.md`](./guides/sdk/javascript.md)
- [`guides/sdk/python.md`](./guides/sdk/python.md)
- [`../sdks/dotnet/README.md`](../sdks/dotnet/README.md)
- [`../sdks/javascript/README.md`](../sdks/javascript/README.md)
- [`../sdks/python/README.md`](../sdks/python/README.md)

### 无 SDK 接入

- [`guides/无SDK接入指南.md`](./guides/无SDK接入指南.md)：直接基于公开协议、Schema、示例和 conformance 接入。

### 仓库维护与贡献

- [`guides/contributor/README.md`](./guides/contributor/README.md)：仓库协作与治理入口。
- [`guides/contributor/repository-contribution.md`](./guides/contributor/repository-contribution.md)：代码、文档、发布准备与本地打包流程。
- [`guides/开发指南.md`](./guides/开发指南.md)：开发环境、常用命令、联调与最小验证要求。
- [`../apps/monitor/README.md`](../apps/monitor/README.md)：Monitor 工作区说明与验证入口。
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md)、[`../CHANGELOG.md`](../CHANGELOG.md)、[`../SECURITY.md`](../SECURITY.md)：仓库级治理入口。

### 运行、排障与发布

- [`operations/部署与运行指南.md`](./operations/部署与运行指南.md)：部署、运行与回滚关注点。
- [`operations/运维排障手册.md`](./operations/运维排障手册.md)：运行期诊断与恢复动作。
- [`operations/publishing/README.md`](./operations/publishing/README.md)：发布流程入口、资产命名与占位规范。
- [`operations/publishing/release-process.md`](./operations/publishing/release-process.md)
- [`operations/publishing/release-asset-layout.md`](./operations/publishing/release-asset-layout.md)
- [`operations/publishing/release-checklist.md`](./operations/publishing/release-checklist.md)

## 3. 分类规则

- [`spec/`](./spec/)：规范正文、Schema 与协议示例。涉及公开契约时先回到这里。
- [`architecture/`](./architecture/)：架构分层、设计取舍、开发规划与专题评估。不要在这里维护协议副本。
- [`guides/`](./guides/)：面向不同角色的稳定入口，描述当前仓库的开发、接入与维护方式。
- [`operations/`](./operations/)：部署、排障、发布、回滚与发布资产管理说明。
- [`assets/`](./assets/)：文档静态资源，不承载执行口径。

## 4. 发布资产占位规范

当文档需要引用尚未生成的正式发布资产、下载链接或安装命令时，统一使用以下占位写法：

```text
TODO(devhub-release): 正式发布资产可用后，在此补充 <资产名称 / 版本号 / 下载链接 / 安装命令>；当前不要填写未生成的版本号、下载地址或仓库外安装命令。
```

使用规则：

- 必须说明未来会由哪个发布资产或版本信息替换当前占位。
- 可以保留仓库内开发命令、本地验证命令或项目引用方式，但要明确它们不是正式安装入口。
- 详细示例与发布资产命名规则见 [`operations/publishing/README.md`](./operations/publishing/README.md)。

## 5. 维护规则

- 新增文档时优先放入现有分类目录，不在 `docs/` 根目录平铺新增 Markdown。
- 若新增的是架构规划或专题评估，应放入 `architecture/`；若新增的是开发、接入、运维或发布说明，应放入对应的 `guides/` 或 `operations/`。
- 若文档陈述协议事实、错误语义或字段定义，应回指 [`spec/Spec.md`](./spec/Spec.md)，避免维护并行副本。
- 外部导航发生变化时，同步更新仓库根 [`README.md`](../README.md) 与相关工作区 README。
