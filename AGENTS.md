# DevHub 开发指导文档

本仓库包含 DevHub 协议规范、核心实现、SDK、上层 App 及测试套件。`docs/specification/protocol/Specification.md` 是唯一权威标准：所有公开行为、字段命名、状态转换、错误语义、序列化契约、测试断言与评审结论都必须与其一致；禁止通过修改 `docs/specification/protocol/Specification.md` 迁就实现。仅在 Specification 的设计与项目定位、需求冲突时，才建议修改 Specification，并要求人工确认。

## 项目结构

```text
DevHub/
├── apps/                                     # 基于 DevHub 生态的上层 App
│   ├── monitor/                              # 桌面 Monitor 工作区
│   └── unity-devhub-dispatcher/              # Unity Editor 侧 dispatcher 配套资产
├── assets/                                   # 仓库级静态资源
│   └── icons/                                # 通用图标资源
├── docs/                                     # 文档系统
│   ├── README.md                             # 文档导航与分类规则
│   ├── user/                                 # 面向使用者与集成方的文档
│   ├── developer/                            # 面向开发与维护的文档
│   └── specification/                        # 权威规范与版本化协议资产
│       ├── protocol/                         # 协议规范正文
│       ├── schema/                           # 版本化 Schema 资产
│       └── protocol-examples/                # 版本化原始协议示例
├── eng/                                      # 工程版本与公共构建配置
├── host/                                     # Host 相关代码
│   ├── DevHub.slnx                           # Host 工作区解决方案文件
│   ├── src/                                  # Host 生产代码（.NET）
│   │   ├── DevHub.Core/                      # 核心领域模型与基础服务
│   │   └── DevHub.Host/                      # 基于 ASP.NET Core 的宿主程序
│   └── tests/                                # Host 测试与验证资产
│       ├── whitebox/                         # .NET 白盒测试工程
│       │   ├── DevHub.Tests/                 # Core / 领域规则测试
│       │   └── DevHub.Host.Tests/            # Host 级白盒测试
│       ├── README.md                         # 测试分层与执行说明
│       ├── blackbox/                         # Python 黑盒测试与 runner
│       ├── conformance/                      # 符合性向量、runner 与自测
│       └── tools/                            # 覆盖率配置与辅助脚本
├── scripts/                                  # 仓库级脚本
│   ├── docs/                                 # 文档辅助脚本
│   ├── release/                              # 发布与打包脚本
│   └── sdk/                                  # SDK 验证与 Unity publish 脚本
├── sdks/                                     # 多语言 SDK、示例代码与相关开发资源
│   ├── dotnet/                               # .NET SDK 工作区
│   │   ├── src/                              # .NET SDK 源码
│   │   │   ├── DevHub.Sdk/                   # .NET SDK 核心库
│   │   │   └── DevHub.Sdk.DependencyInjection/ # .NET SDK 可选 DI companion package
│   │   ├── tests/                            # .NET SDK 测试项目
│   │   │   ├── DevHub.Sdk.UnitTests/         # .NET SDK 单元测试
│   │   │   ├── DevHub.Sdk.IntegrationTests/  # .NET SDK 集成测试
│   │   │   └── DevHub.Sdk.ConformanceAdapter/ # .NET SDK conformance 适配器
│   │   ├── DevHub.DotNetSdk.slnx             # .NET SDK 解决方案文件
│   │   ├── Directory.Build.props             # .NET SDK 工作区公共构建配置
│   │   ├── Directory.Packages.props          # .NET SDK 工作区统一依赖版本管理
│   │   └── README.md                         # .NET SDK 使用与开发说明
│   ├── javascript/                           # JavaScript / TypeScript SDK 工作区
│   │   ├── src/                              # JS/TS SDK 源码
│   │   ├── tests/                            # JS/TS SDK 单元测试与集成测试
│   │   ├── package.json                      # JS/TS SDK 包定义
│   │   └── README.md                         # JS/TS SDK 使用与开发说明
│   └── python/                               # Python SDK 工作区
│       ├── src/                              # Python SDK 源码
│       │   └── devhub_sdk/                   # Python SDK 核心包
│       ├── tests/                            # Python SDK 单元测试与集成测试
│       ├── pyproject.toml                    # Python SDK 构建配置
│       └── README.md                         # Python SDK 使用与开发说明
├── temp/                                     # 生成的测试报告与临时产物
└── LICENSE                                   # 许可证文件
```

## 运行时数据规约

运行时数据根目录默认遵循系统约定，可通过环境变量 `DEVHUB_DATA_DIR` 显式覆盖：

- Windows： `%LOCALAPPDATA%\DevHub\`。
- macOS： `~/Library/Application Support/DevHub/`。
- Linux： `~/.local/share/DevHub/`，遵循 XDG_DATA_HOME 规范。

标准数据根目录结构如下：

```text
<DEVHUB_DATA_DIR>/
├── runtime/
│   ├── hub.json          # 发现文件（存储 httpBaseUrl, wsUrl, tokenFile 等）
│   └── token.txt         # 访问令牌（权限限定为仅当前用户可读）
├── apps/
│   ├── definitions/      # AppDefinition 定义文件（*.json）
│   └── instances/        # 实例镜像
└── logs/
    └── *.log             # 系统运行日志
```

- 客户端必须通过读取 `<dataDir>/runtime/hub.json` 获取动态端口与服务地址，严禁硬编码端口、HTTP 地址或 WebSocket URL。
- 单实例粒度为“同一 OS 用户 + 同一数据根目录”；不同 `DEVHUB_DATA_DIR` 可并行启动，且并行测试必须为每个 Host 分配独立数据根目录。

## 构建、运行与测试命令

- `dotnet build host/DevHub.slnx -c Release`：构建全部 Host .NET 项目。
- `dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release`：启动本地 DevHub 守护进程。
- `dotnet test host/DevHub.slnx -c Release`：运行全部 Host 白盒测试。
- `python3 host/tests/blackbox/test_runner.py --smoke --no-header`：执行快速集成测试冒烟验证。
- `python3 host/tests/blackbox/test_runner.py --full --no-header`：执行更完整但更慢的集成测试集。

## 技术栈与工程约束

- 开发环境使用 C# / ASP.NET Core（.NET 10）。
- 解决方案文件统一使用 `.slnx`，禁止引入 `.sln` 文件。
- JSON 序列化统一使用 `System.Text.Json`。
- 日志架构统一使用 `Microsoft.Extensions.Logging` + `Serilog`。
- GitHub workflow 中的 Node.js 运行时与 JavaScript Action 统一使用 Node 24；选择 `actions/*` 等依赖时，必须使用已支持 Node 24 的版本，禁止使用仍运行在 Node 20 上的旧版本包。

## 开发与质量规范

### 文档规范

- 文档必须基于项目当前状态编写，除任务记录与变更说明文档外，其余文档严禁使用 `不再` `改为` `补充` `现已` 等词汇来描述相对于旧版文档的变更内容。
- 编写中文文档时采用客观陈述或无主句形式，避免使用第二人称“你”。
- 新增或修改功能后，应在任务收尾时统一更新相关文档，并确保文档内容与项目当前状态一致。

### 代码与设计

- 保持架构简洁、模块边界和依赖关系清晰，遵守 DRY 原则，新增能力必须放入正确模块。
- 代码必须优先服务于可维护性，使用清晰命名、稳定边界和可组合服务，避免隐式耦合、跨层访问和难以验证的副作用扩散。
- 修改代码前先确认修改方式和影响范围，同步检查相邻模块、调用链、配置入口、序列化契约和测试资产，确保整体自洽，避免破坏架构。
- 修复问题时要处理问题根源，禁止为了方便而通过临时补丁、堆叠特判、绕过现有设计等方式掩盖问题。
- 若改动暴露出抽象失衡，应同步整理相关结构，而不是继续在失衡结构上堆叠实现，必要时重构相关代码（甚至询问是否允许做破坏性变更）。
- 对真正重复的业务规则、转换逻辑、校验流程和错误处理进行提炼复用；禁止为了“看起来统一”而强行合并语义不同的逻辑。
- 序列化字段名、公开 DTO、错误码与协议状态必须保持稳定。涉及契约变化时，必须同步检查实现、测试与相关文档。
- 修改 package.json 后，必须同步刷新对应 `package-lock.json` ，避免导致 Github Workflow 失败。

### 测试规范

- 遵循测试金字塔，以单元测试为主体，以集成测试验证完整公开契约。
- 测试必须以 `docs/specification/protocol/Specification.md`、核心业务路径、改动范围、历史缺陷和高风险分支为依据；严禁依据当前实现反推用例。
- 断言应优先验证外部可观察结果，包括返回值、状态变化、输出契约、异常语义与协作边界。
- 除明确白盒场景外，不得复制实现逻辑或依赖私有调用顺序。

#### 单元测试

- 单元测试重点验证局部模块、领域规则、边界条件、异常路径、复杂分支和历史缺陷回归点，不以追求 100% 覆盖率为目标。
- 简单属性存取、无业务含义的样板转发、第三方库既有行为，以及已被更高层稳定覆盖且几乎无分支的薄封装，无需机械补测。
- 优先从模块对外暴露的稳定职责入口编写测试；仅在公开入口无法充分定位风险时，才补充更细粒度验证。
- 文件系统、网络、时钟、随机数、进程环境等非确定性依赖应通过 Mock、Stub 或测试替身隔离，确保失败原因单一且可快速定位。
- 测试文件使用 `*Tests.cs` 命名，方法命名延续现有模式，如 `Impl_Resolve_WithOverrides_ShouldUseEnvironmentOverrides`。
- 单元测试整体遵循 F.I.R.S.T. 原则。

#### 集成测试

- 集成测试必须覆盖所有公开接口与对外协议入口，确保客户端可从黑盒视角验证每项公开能力是否符合规范。
- 禁止读取或依赖内部实现细节、私有状态、内部调用顺序或临时调试行为。
- 除 Happy Path 外，还应覆盖参数校验、鉴权约束、错误响应、状态切换、兼容性要求、幂等性和关键异常链路。
- 尽量采用真实进程、数据根目录、发现文件、鉴权材料和协议交互验证系统闭环，而不是以白盒注入替代真实行为。
- 新增公开接口、扩展公开字段、调整错误语义或修改协议行为时，必须同步补齐或更新对应集成测试。
- 测试结束后必须保证环境可重复执行、状态可清理、结果可复现。
- Python 集成测试文件统一使用 `test_*.py` 命名。

### 注释与日志规范

- 所有公开接口必须完备编写文档注释。统一使用中文注释。
- 复杂算法或涉及框架底层机制的逻辑，应补充足够清晰的解释性注释。
- 严格遵守日志分级，保证格式统一，并记录关键操作与异常状态以支持全链路追踪。
- 注释基于当前代码状态编写，不要体现出给予已废弃版本的变更。
- 功能交付前必须清理临时调试日志。

### 最小验证要求

- 涉及协议、宿主、公开接口或运行时行为的改动，在提交前至少完成最小相关 `dotnet test` 目标与 `python3 host/tests/blackbox/test_runner.py --smoke --no-header` 冒烟验证。
- 修改代码后，收尾时必须执行相关模型的白盒、黑盒测试和 GitHub workflow 中与该模块相关的同级别验证流程。
- 如果同时修改了 host 和 sdk 代码，收尾时必须确保 `scripts/release/package_release.py` 脚本能够顺利发布。

## 提交与协作要求

- 提交信息应保持简短、祈使式，并沿用现有前缀风格，如 `fix:`、`test:`、`ci:`。
- 提交时在 description 中附带详细的变更说明。
- 修改文件后，只在明确收到commit指令时才自动提交Git，否则不做任何Git操作。

## 参考资料

- [协议规范 (`docs/specification/protocol/Specification.md`)](docs/specification/protocol/Specification.md)
- [系统架构总览 (`docs/developer/architecture/system-overview.md`)](docs/developer/architecture/system-overview.md)
