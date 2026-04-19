# DevHub 文档导航

- [`user/`](./user/README.md)：面向使用者和集成方的使用文档
- [`developer/`](./developer/README.md)：面向仓库开发者与维护者的开发、运维、发布文档
- [`specification/`](./specification/README.md)：权威规范、Schema 与原始协议示例

## 1. 顶层分类

| 目录 | 面向对象 | 关键入口 | 说明 |
| --- | --- | --- | --- |
| [`user/`](./user/README.md) | 接入方、调用方、首次使用者 | [`user/host/quickstart.md`](./user/host/quickstart.md) | Host 上手、官方 SDK 接入、原始协议接入 |
| [`developer/`](./developer/README.md) | 仓库开发者、维护者、发布负责人 | [`developer/guides/development.md`](./developer/guides/development.md) | 架构、开发协作、部署排障、发布维护 |
| [`specification/`](./specification/README.md) | 需要核对公开契约的所有读者 | [`specification/protocol/Specification.md`](./specification/protocol/Specification.md) | 唯一权威规范、版本化 Schema 与原始协议示例 |

补充入口：

- [`../host/tests/README.md`](../host/tests/README.md)：仓库级测试分层、验证入口与执行说明。
- [`../host/tests/conformance/README.md`](../host/tests/conformance/README.md)：符合性向量、adapter manifest 与向量运行说明。

## 2. 按目标查找

### 我想使用 DevHub

- [`user/README.md`](./user/README.md)：使用路径总入口。
- [`user/host/README.md`](./user/host/README.md)：Host 上手导航。
- [`user/sdk/README.md`](./user/sdk/README.md)：官方 SDK 接入入口。
- [`user/protocol/README.md`](./user/protocol/README.md)：原始协议接入路径。
- [`../apps/monitor/README.md`](../apps/monitor/README.md)：官方桌面 Monitor 的工作区、本地运行入口、前后端分层和验证方式。

### 我想开发或维护仓库

- [`developer/README.md`](./developer/README.md)：开发与维护总入口。
- [`developer/architecture/system-overview.md`](./developer/architecture/system-overview.md)：系统边界、Host 分层、SDK 边界与长期演进原则。
- [`developer/guides/development.md`](./developer/guides/development.md)：开发环境、常用命令、联调与最小验证要求。
- [`developer/guides/contribution.md`](./developer/guides/contribution.md)：协作规则、仓库治理、文档治理与发布准备。
- [`developer/operations/deployment.md`](./developer/operations/deployment.md)：部署、运行与上线后校验。
- [`developer/operations/troubleshooting.md`](./developer/operations/troubleshooting.md)：运行期诊断、恢复动作与 Monitor 排障入口。
- [`developer/publishing/README.md`](./developer/publishing/README.md)：发布流程、资产命名与 TODO 占位规范。

### 我想核对协议、Schema 或原始报文

- [`specification/README.md`](./specification/README.md)：规范资产总入口。
- [`specification/protocol/Specification.md`](./specification/protocol/Specification.md)：公开行为与错误语义的唯一权威标准。
- [`specification/schema/v1.0.1/README.md`](./specification/schema/v1.0.1/README.md)：版本化 Schema。
- [`specification/protocol-examples/v1.0.1/README.md`](./specification/protocol-examples/v1.0.1/README.md)：HTTP / WebSocket 原始 JSON 示例。

## 3. 分类规则

- `docs/user/`：只放“如何使用 DevHub”的文档，按 `host/`、`sdk/`、`protocol/` 分组。
- `docs/developer/`：只放“如何开发、维护、发布 DevHub”的文档，按 `architecture/`、`guides/`、`operations/`、`publishing/` 分组。
- `docs/specification/`：只放权威协议资产，按 `protocol/`、`schema/`、`protocol-examples/` 分组。
- `docs/assets/`：保留静态资源，不作为规范、使用或运维口径来源。
- 除本文件外，不在 `docs/` 根目录新增 Markdown。
- 涉及公开协议事实、字段定义、错误语义或 Schema 契约时，必须回指 [`specification/protocol/Specification.md`](./specification/protocol/Specification.md)，避免维护并行副本。

## 4. 发布资产占位规范

当文档需要引用尚未生成的正式发布资产、下载链接或安装命令时，统一使用以下占位写法：

```text
TODO(devhub-release): 正式发布资产可用后，在此补充 <资产名称 / 版本号 / 下载链接 / 安装命令>；当前不要填写未生成的版本号、下载地址或仓库外安装命令。
```

使用规则：

- 必须说明未来会由哪个发布资产或版本信息替换当前占位。
- 可以保留仓库内开发命令、本地验证命令或项目引用方式，但要明确它们不是正式安装入口。
- 详细示例与发布资产命名规则见 [`developer/publishing/README.md`](./developer/publishing/README.md)。
