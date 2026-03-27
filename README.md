# DevHub

DevHub 是面向本机单用户场景的守护进程（Local Per-user Daemon），为各类工具提供实例注册、发现、调用编排与事件订阅能力。

本仓库包含协议规范、.NET 核心实现、宿主程序以及白盒/黑盒测试套件。所有公开行为、字段命名、状态转换、错误语义与序列化契约均以 [`docs/Spec.md`](docs/Spec.md) 为唯一权威标准。

## SDK 入口

- [.NET SDK](sdks/dotnet/README.md)：面向 C# / .NET 调用方，覆盖 runtime discovery、HTTP JSON-RPC、WebSocket events、统一错误模型、依赖注入工厂以及 `runtime resolver` / `HTTP transport` / `WS session` 扩展点。
- [JS/TS SDK](sdks/javascript/README.md)：面向 Node.js 调用方，覆盖运行时发现、HTTP JSON-RPC、WebSocket 事件流、类型化事件模型、本地参数校验以及可注入 `runtime resolver` / transport / session 扩展点。
- [Python SDK](sdks/python/README.md)：面向 Python 调用方，覆盖运行时发现、HTTP JSON-RPC、WebSocket 事件流、严格 JSON 校验、统一异常模型以及可扩展的 runtime resolver / transport / session 抽象。
- [无 SDK 接入指南](docs/无SDK接入指南.md)：面向不准备依赖仓库内 SDK 的第三方开发者，提供原始协议接入、自测与兼容性口径。

三套 SDK 都遵循统一的数据根目录发现规则：优先读取显式传入的数据根目录，其次读取环境变量 `DEVHUB_DATA_DIR`，最后回退到平台默认数据目录，并固定从 `<dataDir>/runtime/hub.json` 获取 `httpBaseUrl`、`wsUrl` 与 `tokenFile`。

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
