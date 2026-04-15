# DevHub

DevHub 是面向本机单用户场景的守护进程（Local Per-user Daemon），为各类工具提供实例注册、发现、调用编排与事件订阅能力。

本仓库包含协议规范、Host 核心实现、多语言 SDK、官方桌面 `DevHub Monitor` 以及白盒、黑盒和 conformance 测试套件。所有公开行为、字段命名、状态转换、错误语义与序列化契约均以 [`docs/specification/protocol/Specification.md`](docs/specification/protocol/Specification.md) 为唯一权威标准。

完整文档导航见 [`docs/README.md`](docs/README.md)。

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

## 规范与验证资产

- 协议规范：[`docs/specification/protocol/Specification.md`](docs/specification/protocol/Specification.md)
- Schema：[`docs/specification/schema/v1.0.1/README.md`](docs/specification/schema/v1.0.1/README.md)
- 原始协议示例：[`docs/specification/protocol-examples/v1.0.1/README.md`](docs/specification/protocol-examples/v1.0.1/README.md)
- 仓库级测试说明：[`host/tests/README.md`](host/tests/README.md)
- Conformance 说明：[`host/tests/conformance/README.md`](host/tests/conformance/README.md)
