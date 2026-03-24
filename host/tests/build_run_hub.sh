#!/bin/bash

# DevHub 一键构建和运行脚本
# 适用于 macOS/Linux 系统
# 依赖：.NET 10 SDK

set -e  # 遇到错误时立即停止

# 项目根目录
PROJECT_ROOT="$(cd "$(dirname "$(dirname "$(dirname "$0")")")" && pwd)"
HOST_DIR="$PROJECT_ROOT/host"
HOST_SRC_DIR="$HOST_DIR/src"
HOST_PROJECT="$HOST_SRC_DIR/DevHub.Host/DevHub.Host.csproj"
HOST_SOLUTION="$HOST_DIR/DevHub.slnx"

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # 无颜色

echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}        DevHub 构建和运行脚本${NC}"
echo -e "${GREEN}=========================================${NC}"

# 检查 .NET SDK 是否可用
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}错误: 未找到 .NET SDK。请安装 .NET 10 SDK。${NC}"
    exit 1
fi

echo -e "${GREEN}✓ .NET SDK 已找到$(dotnet --version)${NC}"

# 检查项目文件是否存在
if [ ! -f "$HOST_PROJECT" ]; then
    echo -e "${RED}错误: 未找到项目文件 $HOST_PROJECT${NC}"
    exit 1
fi

# 恢复 NuGet 包
echo -e "${YELLOW}正在恢复 NuGet 包...${NC}"
dotnet restore "$HOST_SOLUTION"

# 构建项目
echo -e "${YELLOW}正在构建项目...${NC}"
dotnet build "$HOST_SOLUTION" -c Release

# 检查构建是否成功
if [ $? -ne 0 ]; then
    echo -e "${RED}错误: 项目构建失败${NC}"
    exit 1
fi

echo -e "${GREEN}✓ 项目构建成功${NC}"

# 运行 DevHub
echo -e "${YELLOW}正在启动 DevHub...${NC}"
echo -e "${GREEN}DevHub 将在默认端口启动${NC}"
echo -e "${YELLOW}按 Ctrl+C 停止服务${NC}"
echo -e "${GREEN}=========================================${NC}"

dotnet run --project "$HOST_PROJECT" --configuration Release

echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}        DevHub 服务已停止${NC}"
echo -e "${GREEN}=========================================${NC}"
