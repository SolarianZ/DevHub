# 完整测试过程操作顺序

## 前置要求

确保已安装以下软件：
- [.NET SDK 10.0 或更高版本](https://dotnet.microsoft.com/download)
- [Python 3.7 或更高版本](https://www.python.org/downloads/)
- Python 依赖库：`pip install requests`

## 1. 项目构建

首先需要构建 .NET 项目：

    # 进入项目根目录（使用相对路径）
    cd <项目根目录>

    # 恢复 NuGet 包
    dotnet restore src/DevHub.slnx

    # 构建整个解决方案（Release 模式）
    dotnet build src/DevHub.slnx -c Release

## 2. 运行 .NET 单元测试

使用 dotnet test 命令运行 .NET 测试项目：

    dotnet test src/DevHub.Tests/ -c Release --logger "console;verbosity=normal"

## 3. 运行 DevHub 主机

启动 DevHub 服务（用于功能测试）：

    dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release

注意：DevHub 会在启动时：
- 检查是否已在运行（单实例限制）
- 创建必要的目录结构
  - Windows: `%LOCALAPPDATA%/DevHub/`
  - macOS: `~/Library/Application Support/DevHub/`
  - Linux: `~/.config/DevHub/`
- 生成并写入 token.txt 和 hub.json 文件
- 动态分配监听端口（通过 hub.json 发现）

## 4. 运行 Python 功能测试

在另一个终端窗口中运行 Python 测试脚本：

    cd <项目根目录>

    # 确保 Python 环境（Python 3.x）
    python3 --version  # 或 python --version（根据系统配置）

    # 安装 Python 依赖（仅首次运行需要）
    pip3 install requests  # 或 pip install requests

    # 运行完整测试套件
    python3 tests/test_runner.py

    # 或者使用 --no-header 参数跳过头部信息
    python3 tests/test_runner.py --no-header

## 5. 测试结果查看

.NET 测试结果：
- 直接在控制台输出

Python 测试结果：
- 控制台实时输出
- 日志文件：temp/test_log.txt
- 详细报告：temp/test_results.json（JSON 格式）
- 文本报告：temp/test_results.txt（可读性更好）

## 6. 停止 DevHub 服务

测试完成后，可以通过以下方式停止服务：
- 在运行 DevHub 的终端按 `Ctrl+C`
- 或者通过进程管理器结束 `DevHub.Host` 进程

## 简化的测试命令（可选）

如果使用 PowerShell 或 Bash，可以使用以下简化命令：

    # 构建并运行所有测试
    cd <项目根目录>
    dotnet build src/DevHub.slnx -c Release
    dotnet test src/DevHub.Tests/ -c Release
    python3 tests/test_runner.py

## 重要说明

- 第一次运行时：DevHub 会创建目录结构和 token 文件，可能需要几秒钟时间
- 端口号：DevHub 每次启动都会动态分配随机端口，测试脚本会自动从 hub.json 中读取
- 单实例限制：同一时间只能运行一个 DevHub 实例
- 临时文件：测试过程中会在 temp/ 目录生成日志和报告文件，可以安全删除
- 权限：确保对 DevHub 配置目录有读写权限（路径根据操作系统而定）

## 测试覆盖范围

.NET 测试（DevHub.Tests）：
- 单元测试（UnitTest1.cs）
- 负数测试（NegativeTests.cs）- 错误场景测试
Python 功能测试（tests/）：
- 启动与发现测试（test_launch_discovery.py）
- 鉴权与协议版本测试（test_auth_protocol.py）
- 应用程序定义测试（test_app_definitions.py）
- 应用程序实例测试（test_app_instances.py）