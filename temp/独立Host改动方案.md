# 独立 Host 改动方案

## 目标

使 `dotnet`、`python`、`javascript` 三套 SDK 的集成测试在同一台机器上并发执行时，能够各自启动完全独立的临时 DevHub Host，且测试产物彼此不共享、不串扰。

本方案中的“完全独立”定义为：

- 每个测试 Host 使用独立的 `hub.json`、`token.txt`
- 每个测试 Host 使用独立的 AppDefinition 目录
- 每个测试 Host 使用独立的实例镜像目录
- 每个测试 Host 使用独立的日志目录
- 每个测试 Host 拥有独立的单实例测试槽位，不会被全局单实例互斥量挡住

## 现状结论

当前代码已经具备以下能力：

- `DEVHUB_RUNTIME_DIR` 可隔离 `hub.json` 与 `token.txt`
- `DEVHUB_APPDEFS_DIR` 可隔离 definitions
- `DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS` 可让多个 Host 并发启动
- `DEVHUB_LOG_DIR` 已存在，但三套 SDK 集成测试夹具当前未统一设置

当前仍缺的隔离点：

- `apps/instances` 目录没有独立环境变量覆盖，仍默认落到共享根目录
- 三套 SDK 集成测试夹具未统一设置独立日志目录

结论：

- 连接发现链路已经基本隔离
- 若要求“临时 Host 的全部测试产物完全独立”，还需要补齐实例目录和日志目录隔离

## 总体方案

采用“每个 SDK 集成测试 Host 都绑定独立临时根目录”的策略，但不新增新的“全局根目录”概念，继续沿用现有的分目录环境变量设计。

最终每个测试 Host 启动时统一设置以下环境变量：

- `DEVHUB_RUNTIME_DIR=<tempRoot>/runtime`
- `DEVHUB_APPDEFS_DIR=<tempRoot>/definitions`
- `DEVHUB_APPINST_DIR=<tempRoot>/instances`
- `DEVHUB_LOG_DIR=<tempRoot>/logs`
- `DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS=<unique-slot>`

这样可以在不改变 SDK 发现逻辑的前提下，让每个 Host 的全部落盘产物都进入自己的临时目录树。

## 必改模块

### 1. 核心路径解析

文件：

- `src/DevHub.Core/Services/RuntimePathOptions.cs`
- `src/DevHub.Tests/RuntimePathOptionsTests.cs`

改动：

- 在 `RuntimePathOptions` 中新增环境变量常量 `DEVHUB_APPINST_DIR`
- 在 `Resolve()` 中增加实例目录覆盖逻辑
  - 若设置了 `DEVHUB_APPINST_DIR`，则 `InstancesPath` 使用该值
  - 否则保持当前默认值 `<root>/apps/instances`
- 保持 `Create()` 现有行为不变
- 为 `RuntimePathOptionsTests` 增加断言
  - 覆盖 `DEVHUB_APPINST_DIR` 时应命中覆盖值
  - 未覆盖时仍应回退到 `<root>/apps/instances`
  - 相对路径应归一化为绝对路径

说明：

- 这是唯一必须修改的核心实现点
- `FileSystemManager` 已经通过 `RuntimePathOptions.InstancesPath` 取值，因此不需要额外改实例目录消费逻辑

### 2. .NET SDK 集成测试夹具

文件：

- `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/TestHost/DevHubHostFixture.cs`

改动：

- 在临时目录下新增 `instances` 与 `logs` 子目录
- 启动 Host 进程时补充设置：
  - `DEVHUB_APPINST_DIR`
  - `DEVHUB_LOG_DIR`
- `DisposeAsync()` 继续复用现有的整棵临时目录清理逻辑

预期结果：

- .NET SDK 集成测试产生的运行时文件、定义文件、实例镜像、日志全部只落在自己的临时目录

### 3. Python SDK 集成测试夹具

文件：

- `sdks/python/tests/integration/_host.py`

改动：

- 在 `TemporaryDirectory` 下新增 `instances` 与 `logs` 子目录
- 启动 Host 进程时补充设置：
  - `DEVHUB_APPINST_DIR`
  - `DEVHUB_LOG_DIR`
- 继续复用 `TemporaryDirectory.cleanup()` 统一清理

预期结果：

- Python SDK 集成测试产生的全部 Host 产物只落在当前测试临时目录

### 4. JavaScript SDK 集成测试夹具

文件：

- `sdks/javascript/tests/integration/host.ts`

改动：

- 在 `mkdtemp()` 生成的临时目录下新增 `instances` 与 `logs` 子目录
- 启动 Host 进程时补充设置：
  - `DEVHUB_APPINST_DIR`
  - `DEVHUB_LOG_DIR`
- `close()` 继续删除整棵临时目录

预期结果：

- JavaScript SDK 集成测试产生的全部 Host 产物只落在当前测试临时目录

## 建议同步更新的文档

### 5. 规范与运维文档

文件：

- `docs/Spec.md`
- `docs/部署与运行指南.md`
- `docs/运维排障手册.md`

改动建议：

- 在 `Spec.md` 中补充 `DEVHUB_APPINST_DIR`
  - 定义其用途为覆盖 AppInstance 镜像目录
  - 说明主要用途为测试框架 / 便携式安装
  - 保持 `DEVHUB_RUNTIME_DIR` “仅影响运行时目录”的现有语义不变
- 在部署与运维文档中增加该环境变量说明与示例

说明：

- `DEVHUB_APPINST_DIR` 属于新的公开环境变量能力
- 若实现上线但规范与运维文档不更新，会形成实现与文档不一致

## 本次不需要改的模块

- `src/DevHub.Host/Program.cs`
- `src/DevHub.Host/appsettings.json`
- 三套 SDK 的 runtime discovery 实现
- `FileSystemManager` 的 `hub.json` / `token.txt` 写入逻辑

原因：

- `DEVHUB_LOG_DIR` 已存在，只需让测试夹具传入独立日志目录即可
- SDK 客户端本来就只依赖 `runtimeDir` 发现 `hub.json`
- `FileSystemManager` 已消费 `RuntimePathOptions`，新增实例目录覆盖后可自动生效

## 推荐测试补充

### 1. 单元测试

建议补充或更新：

- `src/DevHub.Tests/RuntimePathOptionsTests.cs`
  - 新增 `DEVHUB_APPINST_DIR` 覆盖场景
- `src/DevHub.Tests/ServiceCollectionExtensionsTests.cs`
  - 断言注册后的 `RuntimePathOptions.InstancesPath` 使用覆盖值
- `src/DevHub.Tests/FileSystemManagerRecoveryTests.cs`
  - 若需要，可增加“实例目录覆盖时目录初始化命中正确路径”的验证

### 2. Host 级测试

建议补充：

- `src/DevHub.Host.Tests/ProgramProcessTests.cs`
  - 真实进程启动 Host
  - 传入 `DEVHUB_RUNTIME_DIR`、`DEVHUB_APPDEFS_DIR`、`DEVHUB_APPINST_DIR`、`DEVHUB_LOG_DIR`
  - 断言四个目录都被创建
  - 断言 `hub.json` 与 `token.txt` 写到独立 runtime 目录

### 3. SDK 集成测试

三套 SDK 现有集成测试用例主体可以基本不变，重点是夹具更新后继续跑通：

- `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release`
- `python -m pytest sdks/python/tests/integration -q`
- `npm test -- --runInBand` 或当前仓库既定的 `vitest run`

## 并发验证方案

实现完成后，建议增加一次“跨 SDK 并发启动验证”：

1. 同时执行三套 SDK 的集成测试命令
2. 观察每套测试各自的临时目录
3. 重点确认以下事实：
   - 每个 Host 的 `hub.json` 路径不同
   - 每个 Host 的 `token.txt` 路径不同
   - 每个 Host 的 definitions 目录不同
   - 每个 Host 的 instances 目录不同
   - 每个 Host 的 logs 目录不同
   - 每个 Host 的 `httpBaseUrl` 端口不同
   - 进程之间不会因单实例互斥量而提前退出

## 风险与注意事项

### 1. 规范变更风险

- `DEVHUB_APPINST_DIR` 是新增公开环境变量
- 若不更新 `docs/Spec.md`，会造成实现能力与规范定义不一致

### 2. 当前实例镜像尚未真正落盘

- 目前 `apps/instances` 在实现中主要是目录初始化
- 因此这次改动的主要价值是为“完全独立的测试产物”补齐隔离面，并为后续实例镜像落盘能力预留正确路径覆盖

### 3. .NET 进程级环境变量测试

- `sdks/dotnet/tests/DevHub.Sdk.IntegrationTests/Http/HttpFlowTests.cs` 中存在通过进程级环境变量验证 runtime discovery 的测试
- 若未来要进一步提高同一测试程序集内部的并行稳定性，可考虑将该类用例放入不并行集合
- 这不是本次“跨 SDK 独立 Host”方案的阻塞项

## 实施顺序

推荐顺序如下：

1. 修改 `RuntimePathOptions`，引入 `DEVHUB_APPINST_DIR`
2. 补齐 `RuntimePathOptionsTests`
3. 更新三套 SDK 集成测试夹具，统一设置 `APPINST_DIR` 与 `LOG_DIR`
4. 补充 Host 真实进程测试
5. 更新 `Spec`、部署文档、运维文档
6. 运行最小验证与三套 SDK 并发验证

## 本轮任务状态（2026-03-16，Host 先行）

- [x] Host/Core：`RuntimePathOptions` 已引入 `DEVHUB_APPINST_DIR`，并保持 `Create()` 现有行为不变。
- [x] Host/Core：已补齐 `RuntimePathOptionsTests` 与 `ServiceCollectionExtensionsTests`，覆盖实例目录环境变量解析与 DI 注册结果。
- [x] Host：已补充 `ProgramProcessTests`，验证传入 `DEVHUB_RUNTIME_DIR`、`DEVHUB_APPDEFS_DIR`、`DEVHUB_APPINST_DIR`、`DEVHUB_LOG_DIR` 时四类目录均可独立创建，且 `hub.json` / `token.txt` 落在独立 runtime 目录。
- [x] 文档：已同步更新 `docs/Spec.md`、`docs/部署与运行指南.md`、`docs/运维排障手册.md`，补充 `DEVHUB_APPINST_DIR` 的规范与运维说明。
- [x] 验证：已完成 `dotnet restore`、`dotnet build src/DevHub.slnx -c Release --no-restore`、`dotnet test src/DevHub.slnx -c Release --no-build --collect:\"XPlat Code Coverage\" --settings tests/coverage.runsettings`、`python tests/verify_coverage.py --root . --line-threshold 0.90 --branch-threshold 0.80`，以及 Host 隔离环境下的 `python tests/test_runner.py --fast/--full/--smoke --no-header`。
- [ ] SDK：`.NET SDK` 集成测试夹具尚未改动。
- [ ] SDK：`Python SDK` 集成测试夹具尚未改动。
- [ ] SDK：`JavaScript SDK` 集成测试夹具尚未改动。
- [ ] 跨 SDK 并发验证：待三套 SDK 夹具统一接入 `DEVHUB_APPINST_DIR` / `DEVHUB_LOG_DIR` 后再执行。

## 最终落地后的预期状态

落地后，每个 SDK 集成测试 Host 都会拥有独立的：

- 发现文件
- 鉴权令牌
- definitions
- instances
- logs
- 单实例测试槽位

从而满足“同一台电脑同时执行 dotnet/python/javascript SDK 集成测试，三者互不干扰”的目标。
