# DevHub 开发指导文档

本仓库包含 DevHub 协议规范、核心实现及测试套件。`docs/Spec.md` 是唯一权威标准：所有公开行为、字段命名、状态转换、错误语义、序列化契约、测试断言与评审结论都必须与其一致；禁止通过修改 `docs/Spec.md` 迁就实现。

## 项目结构

```text
DevHub/
├── docs/                                    # 协议规范与设计文档
│   ├── Spec.md                              # 权威技术规范（Single Source of Truth）
│   └── DevHub协议与开发规划.md               # 架构演进与里程碑规划
├── src/                                     # 核心实现代码（.NET）
│   ├── DevHub.Core/                         # 核心领域模型与基础服务
│   ├── DevHub.Host/                         # 基于 ASP.NET Core 的宿主程序
│   ├── DevHub.Tests/                        # 单元测试（白盒测试）
│   ├── DevHub.Host.Tests/                   # Host 级测试
│   └── DevHub.slnx                          # 解决方案文件
├── tests/                                   # 集成测试套件（黑盒测试）
│   ├── README.md                            # 集成测试环境配置说明
│   └── test_runner.py                       # 集成测试自动化入口脚本
├── sdks/                                    # 多语言 SDK、示例代码与相关开发资源
│   └── dotnet/                              # .NET SDK 工作区
│       ├── src/                             # .NET SDK 源码
│       │   └── DevHub.Sdk/                  # .NET SDK 核心库
│       ├── tests/                           # .NET SDK 测试项目
│       │   ├── DevHub.Sdk.UnitTests/        # .NET SDK 单元测试
│       │   └── DevHub.Sdk.IntegrationTests/ # .NET SDK 集成测试
│       ├── DevHub.DotNetSdk.slnx            # .NET SDK 解决方案文件
│       ├── Directory.Build.props            # .NET SDK 工作区公共构建配置
│       ├── Directory.Packages.props         # .NET SDK 工作区统一依赖版本管理
│       └── README.md                        # .NET SDK 使用与开发说明
├── temp/                                    # 生成的测试报告与临时产物
└── LICENSE                                  # 许可证文件
```

## 运行时数据规约

运行时根目录默认遵循系统约定，可通过环境变量 `DEVHUB_RUNTIME_DIR` 显式覆盖：

- Windows： `%LOCALAPPDATA%\DevHub\`。
- macOS： `~/Library/Application Support/DevHub/`。
- Linux： `~/.local/share/DevHub/`，遵循 XDG_DATA_HOME 规范。

标准运行时目录结构如下：

```text
<DEVHUB_RUNTIME_DIR>/
├── runtime/
│   ├── hub.json          # 发现文件（存储 httpBaseUrl, wsUrl, tokenFile 等）
│   └── token.txt         # 访问令牌（权限限定为仅当前用户可读）
├── apps/
│   ├── definitions/      # AppDefinition 定义文件（*.json）
│   └── instances/        # 实例镜像
└── logs/
    └── *.log             # 系统运行日志
```

- 客户端必须通过读取 `hub.json` 获取动态端口与服务地址，严禁硬编码端口、HTTP 地址或 WebSocket URL。
- `DEVHUB_RUNTIME_DIR` 仅用于显式覆盖默认运行时目录，不应用作规避标准运行时布局的手段。

## 构建、运行与测试命令

- `dotnet build src/DevHub.slnx -c Release`：构建全部 .NET 项目。
- `dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release`：启动本地 DevHub 守护进程。
- `dotnet test src/DevHub.slnx -c Release`：运行全部单元测试。
- `python3 tests/test_runner.py --smoke --no-header`：执行快速集成测试冒烟验证。
- `python3 tests/test_runner.py --full --no-header`：执行更完整但更慢的集成测试集。

## 技术栈与工程约束

- 开发环境使用 C# / ASP.NET Core（.NET 10）。
- 解决方案文件统一使用 `.slnx`，禁止引入 `.sln` 文件。
- JSON 序列化统一使用 `System.Text.Json`。
- 日志架构统一使用 `Microsoft.Extensions.Logging` + `Serilog`。
- 单元测试使用 xUnit + Moq，集成测试使用 Python。

## 开发与质量规范

### 代码与设计

- 实现前先确认模块职责边界，再落地代码。新增能力必须放入正确层次，禁止把协议处理、业务规则、宿主编排和基础设施访问混杂在同一处。
- 修改代码时必须优先解决根因，禁止通过临时补丁、堆叠特判、绕过现有设计等方式掩盖问题。
- 若改动暴露出抽象失衡，应同步整理相关结构，而不是继续在失衡结构上堆叠实现。
- 对真正重复的业务规则、转换逻辑、校验流程和错误处理进行提炼复用；禁止为了“看起来统一”而强行合并语义不同的逻辑。
- 代码必须优先服务于可维护性。应使用清晰命名、稳定边界和可组合服务，避免隐式耦合、跨层访问和难以验证的副作用扩散。
- 改动代码时必须同步检查相邻模块、调用链、配置入口、序列化契约和测试资产，确保整体自洽。
- 遵循现有 .NET 代码风格。序列化字段名、公开 DTO、错误码与协议状态必须保持稳定。涉及契约变化时，必须同步检查实现、测试与相关文档。

### 测试规范

- 遵循测试金字塔，以单元测试为主体，以集成测试验证完整公开契约。
- 测试必须以 `docs/Spec.md`、核心业务路径、改动范围、历史缺陷和高风险分支为依据；严禁依据当前实现反推用例。
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
- 尽量采用真实进程、运行时目录、发现文件、鉴权材料和协议交互验证系统闭环，而不是以白盒注入替代真实行为。
- 新增公开接口、扩展公开字段、调整错误语义或修改协议行为时，必须同步补齐或更新对应集成测试。
- 测试结束后必须保证环境可重复执行、状态可清理、结果可复现。
- Python 集成测试文件统一使用 `test_*.py` 命名。

### 注释与日志规范

- 统一使用中文注释。
- 所有公开接口必须完备编写 XML Documentation。
- 复杂算法或涉及框架底层机制的逻辑，应补充足够清晰的解释性注释。
- 严格遵守日志分级，保证格式统一，并记录关键操作与异常状态以支持全链路追踪。
- 功能交付前必须清理临时调试日志。

### 最小验证要求

- 涉及协议、宿主、公开接口或运行时行为的改动，在提交前至少完成最小相关 `dotnet test` 目标与 `python3 tests/test_runner.py --smoke --no-header` 冒烟验证。

## 提交与协作要求

- 提交信息应保持简短、祈使式，并沿用现有前缀风格，如 `fix:`、`test:`、`ci:`。
- 每次提交只处理一个明确关注点，避免将协议变更、重构、测试补充和杂项修复混在一起。
- PR 说明应明确关联的里程碑或 `Spec.md` 章节，并列出已执行的验证命令。
- 若改动涉及运行时路径、鉴权令牌、协议契约、序列化字段或公开接口行为，必须在评审说明中明确指出。
- 仅在确有必要时附带日志、请求样例、响应样例或截图，用于说明行为变化。
- 必须根据当前 Git 分支名称判断所属里程碑，仅在该里程碑定义的范围内开发与评审。
- 新增或修改功能后，应在最后阶段统一更新开发进度等相关文档。
- 本地文件变更后，禁止通过脚本自动提交 Git，须由开发者手动校验后提交。

## 参考资料

- [协议规范 (`docs/Spec.md`)](docs/Spec.md)
- [架构与开发规划 (`docs/DevHub协议与开发规划.md`)](docs/DevHub协议与开发规划.md)
