# DevHub

DevHub 是面向本机单用户场景的守护进程（Local Per-user Daemon），为各类工具提供实例注册、发现、调用编排与事件订阅能力。

本仓库包含协议规范、Host 核心实现、多语言 SDK、官方桌面 `DevHub Monitor` 以及白盒 / 黑盒 / conformance 测试套件。所有公开行为、字段命名、状态转换、错误语义与序列化契约均以 [`docs/specification/protocol/Specification.md`](docs/specification/protocol/Specification.md) 为唯一权威标准。

完整文档导航见 [`docs/README.md`](docs/README.md)。

## 仓库内容

- [`docs/specification/README.md`](docs/specification/README.md)：权威规范、版本化 Schema 与原始协议示例。
- [`docs/user/README.md`](docs/user/README.md)：面向使用者和接入方的 Host / SDK / 原始协议文档。
- [`docs/developer/README.md`](docs/developer/README.md)：面向开发者与维护者的架构、开发、运维和发布文档。
- [`host/`](host/)：基于 ASP.NET Core 的 Host 实现，以及仓库级 whitebox / blackbox / conformance 测试与验证资产。
- [`sdks/`](sdks/)：`.NET`、`JS/TS`、`Python` SDK 工作区。
- [`apps/monitor/README.md`](apps/monitor/README.md)：官方桌面 Monitor 工作区说明与本地运行入口。
- [`host/tests/README.md`](host/tests/README.md)：仓库级 whitebox / blackbox / conformance 分层与验证工具说明。

## 用户手册

- Host 上手：[`docs/user/host/README.md`](docs/user/host/README.md)
- Host 快速验证：[`docs/user/host/quickstart.md`](docs/user/host/quickstart.md)
- 官方 SDK 接入：[`docs/user/sdk/README.md`](docs/user/sdk/README.md)
- 原始协议接入：[`docs/user/protocol/README.md`](docs/user/protocol/README.md)
- 桌面 Monitor：[`apps/monitor/README.md`](apps/monitor/README.md)

如果需要核对协议字段、错误语义或 JSON 示例，请回到 [`docs/specification/README.md`](docs/specification/README.md)。

## 开发手册

- 开发入口：[`docs/developer/README.md`](docs/developer/README.md)
- 开发环境与验证：[`docs/developer/guides/development.md`](docs/developer/guides/development.md)
- 贡献与仓库治理：[`docs/developer/guides/contribution.md`](docs/developer/guides/contribution.md)
- 系统边界与测试架构：[`docs/developer/architecture/system-overview.md`](docs/developer/architecture/system-overview.md)

常用命令：

```bash
dotnet build host/DevHub.slnx -c Release
dotnet test host/DevHub.slnx -c Release
python3 host/tests/blackbox/test_runner.py --smoke --no-header
```

## 治理与支持

- 文档分类规则：[`docs/README.md`](docs/README.md)
- 发布流程与 TODO 占位规范：[`docs/developer/publishing/README.md`](docs/developer/publishing/README.md)

## 规范与验证资产

- 协议规范：[`docs/specification/protocol/Specification.md`](docs/specification/protocol/Specification.md)
- Schema：[`docs/specification/schema/v1.0.1/README.md`](docs/specification/schema/v1.0.1/README.md)
- 原始协议示例：[`docs/specification/protocol-examples/v1.0.1/README.md`](docs/specification/protocol-examples/v1.0.1/README.md)
- 仓库级测试说明：[`host/tests/README.md`](host/tests/README.md)
- Conformance 说明：[`host/tests/conformance/README.md`](host/tests/conformance/README.md)
