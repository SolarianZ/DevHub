# DevHub

DevHub 是面向本机单用户场景的守护进程（Local Per-user Daemon），为各类工具提供实例注册、发现、调用编排与事件订阅能力。

本仓库包含协议规范、.NET 核心实现、宿主程序以及白盒/黑盒测试套件。所有公开行为、字段命名、状态转换、错误语义与序列化契约均以 [`docs/Spec.md`](docs/Spec.md) 为唯一权威标准。

## 文档导航

- [开发指南](docs/开发指南.md)：开发环境、仓库结构、本地构建、运行与验证流程。
- [部署与运行指南](docs/部署与运行指南.md)：发布、启动、数据根目录布局、配置项与上线后校验。
- [运维排障手册](docs/运维排障手册.md)：日志定位、常见故障与恢复步骤。
- [协议规范](docs/Spec.md)：公开协议与对外契约。
- [无 SDK 接入指南](docs/无SDK接入指南.md)：面向第三方开发者的原始协议接入、错误语义与自测入口。
- [版本化 Schema 包](docs/schema/v1.0.1/README.md)：v1.0.1 对外发布的 Draft-07 Schema 文件。
- [原始协议示例](docs/protocol-examples/v1.0.1/README.md)：HTTP / WebSocket 原始 JSON 示例。
- [架构与开发规划](docs/DevHub协议与开发规划.md)：架构背景、里程碑与当前状态。
- [集成测试说明](host/tests/README.md)：Python 黑盒测试夹具与执行方式。
- [Conformance 说明](host/tests/conformance/README.md)：跨语言符合性向量、运行方式与失败快照说明。
