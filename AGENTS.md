# DevHub 开发指导文件

本仓库包含 DevHub 协议规范、规划文档、实现代码与测试。

## 项目简要说明

DevHub 是本机 per-user 守护进程，为开发工具提供实例注册、发现、调用编排与事件订阅能力。
所有实现与测试以 `docs/Spec.md` 为权威标准。

## 项目结构

```
DevHub/
├── docs/                          # 协议规范与设计文档
│   ├── Spec.md                    # 权威规范
│   ├── DevHub协议与开发规划.md      # 架构与里程碑规划
│   └── DevHub_M1细化任务文档.md     # M1 任务细化与验收要点
├── src/                           # 实现代码（.NET）
│   ├── DevHub.Core/               # 核心模型与服务
│   ├── DevHub.Host/               # ASP.NET Core Host
│   ├── DevHub.Tests/              # 单元测试（白盒测试）
│   └── DevHub.slnx                # 解决方案文件（仅`.slnx`，不使用`.sln`）
├── tests/                         # 集成测试脚本（黑盒测试）
│   └── README.md                  # 集成测试说明
│   └── test_runner.py             # 集成测试入口脚本
└── LICENSE                        # 许可证
```

## 运行时产物

运行时根目录以实现为准，可通过 `DEVHUB_RUNTIME_DIR` 覆盖。常见约定如下：

- Windows：`%LOCALAPPDATA%\DevHub\`
- macOS：`~/Library/Application Support/DevHub/`
- Linux：`~/.local/share/DevHub/`（遵循 XDG_DATA_HOME 约定）

运行时产物结构：

```
<DEVHUB_RUNTIME_DIR>/
├── runtime/
│   ├── hub.json          # 发现文件（含 httpBaseUrl/wsUrl/tokenFile）
│   └── token.txt         # 访问 token（仅当前用户可读）
├── apps/
│   ├── definitions/      # AppDefinition 定义
│   │   └── *.json
│   └── instances/        # 实例镜像（可选，用于诊断）
│       └── *.json
└── logs/
    └── *.log             # 运行日志（可选）
```

使用约定：
- 始终读取 `hub.json` 作为端口与地址来源，不允许假设固定端口。
- `hub.json`/`token.txt` 只允许当前用户访问（ACL 约束）。

## 技术栈与工程约束

- 语言与框架：C# + ASP.NET Core（.NET 10），解决方案仅使用 `.slnx`，不使用旧版 `.sln`
- JSON：System.Text.Json
- 日志：Microsoft.Extensions.Logging + Serilog
- 单元测试：xunit + Moq
- 集成测试：Python

## 项目开发规范（必须执行）

- **严守架构规范**：坚持分层与职责边界，避免“补丁式”修改。
- **遵循 DRY 原则**：重复逻辑必须抽取为可复用模块。
- **接口契约与校验**：外部输入优先使用异常，内部数据优先使用断言；统一错误处理。
- **避免功能回归**：增删改后自查核心链路，确保通过 lint/测试。
- **确保可测试**：新增/修改功能需补测试；修改后运行测试。
- **规范注释**：公开 API 必须包含文档注释；在易误解、以出错处添加对新人友好的注释；**使用中文注释**。
- **规范日志**：使用分级日志记录关键操作与故障，提交前清理调试日志。
- **文档实时同步**：变更后同步更新相关文档（README/接口文档/CHANGELOG 等）。

## 测试规范

- **总体原则**：遵循测试金字塔，单元测试为主、集成测试覆盖核心链路、端到端测试最少。
- **覆盖范围决策**：以 `docs/Spec.md`、核心业务路径、变更范围、风险与复杂度为优先；高频调用、边界条件、历史缺陷与易错逻辑必须覆盖；低风险展示逻辑与简单胶水代码可降低覆盖。
- **C# 单元测试（白盒，DevHub.Tests）**
  - **要覆盖**：复杂算法与逻辑、边界条件、DTO 映射、工具类方法。
  - **不覆盖**：外部依赖、第三方库、简单属性；外部依赖应通过 Mock/Stub 隔离。
  - **编写原则**：F.I.R.S.T.（快速、独立、可重复、自验证、及时）。
- **Python API 集成测试（黑盒，tests/）**
  - **要覆盖**：快乐路径、错误处理、中间件行为、数据持久化、接口契约。
  - **不覆盖**：函数内部实现细节、前端 UI 样式。
  - **编写原则**：黑盒思维、尽量贴近真实环境、测试后必须清理垃圾数据、关注业务闭环。

## 项目评审与优化规则

- 在确保现有功能正常运行的前提下，审查架构、业务流程与数据链路问题。
- 优化结构与职责划分，遵循 DRY，但避免把用途不同的逻辑强行合并。

## 行为规范

- 添加/修改/删除任何功能前，仔细阅读 `docs/Spec.md` 中的相关规范，确保遵守规范。
- 详细字段、错误码、状态机与时序要求请参考 `docs/Spec.md`。
- 添加/修改/删除文件后 **不要提交 Git**（由用户手动处理）。

## 参考文档

- [docs/Spec.md](docs/Spec.md)
- [docs/DevHub协议与开发规划.md](docs/DevHub协议与开发规划.md)
- [docs/DevHub_M1细化任务文档.md](docs/DevHub_M1细化任务文档.md)