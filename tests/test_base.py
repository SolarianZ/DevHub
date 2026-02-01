#!/usr/bin/env python3
"""
DevHub M1 测试基础类和工具函数
"""

import os
import json
import platform
import requests
from datetime import datetime


class DiscoveryService:
    """
    从 hub.json 发现文件中获取 DevHub 服务信息
    """

    @staticmethod
    def get_runtime_directory():
        """获取运行时目录"""
        system = platform.system()
        if system == "Windows":
            return os.path.join(os.environ["LOCALAPPDATA"], "DevHub", "runtime")
        elif system == "Darwin":  # macOS
            return os.path.join(os.environ["HOME"], "Library", "Application Support", "DevHub", "runtime")
        elif system == "Linux":
            return os.path.join(os.environ["HOME"], ".local", "share", "DevHub", "runtime")
        else:
            raise Exception(f"Unsupported OS: {system}")

    @staticmethod
    def get_hub_info():
        """
        从 hub.json 获取服务信息
        :return: (http_base_url, token)
        """
        runtime_dir = DiscoveryService.get_runtime_directory()
        hub_json_path = os.path.join(runtime_dir, "hub.json")
        token_path = os.path.join(runtime_dir, "token.txt")

        if not os.path.exists(hub_json_path):
            raise FileNotFoundError(f"hub.json not found: {hub_json_path}")
        if not os.path.exists(token_path):
            raise FileNotFoundError(f"token.txt not found: {token_path}")

        with open(hub_json_path, "r", encoding="utf-8") as f:
            hub_info = json.load(f)

        with open(token_path, "r", encoding="utf-8") as f:
            token = f.read().strip()

        return hub_info["httpBaseUrl"], token


class RpcClient:
    """
    JSON-RPC 客户端
    """

    def __init__(self, base_url, token):
        self.base_url = base_url
        self.token = token
        self.headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "PythonTestClient",
            "X-DevHub-ClientSessionId": "test-session-123"
        }

    def call(self, method, params=None, request_id="1"):
        """
        调用 JSON-RPC 方法
        :param method: 方法名
        :param params: 参数字典
        :param request_id: 请求 ID
        :return: 响应字典
        """
        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

        url = f"{self.base_url}/rpc"
        try:
            response = requests.post(url, json=payload, headers=self.headers, timeout=30)
            # 注意：根据 DevHub 规范，HTTP 状态码始终返回 200 OK
            # 错误通过 JSON-RPC 的 error 字段表示，不应使用 raise_for_status()
            return response.json()
        except requests.exceptions.RequestException as e:
            raise Exception(f"RPC request failed: {e}")

    def call_with_invalid_headers(self, method, invalid_headers, params=None, request_id="1"):
        """
        使用无效的请求头调用方法（用于测试鉴权）
        """
        headers = self.headers.copy()
        headers.update(invalid_headers)

        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

        url = f"{self.base_url}/rpc"
        try:
            response = requests.post(url, json=payload, headers=headers, timeout=30)
            return response.json()
        except requests.exceptions.RequestException as e:
            raise Exception(f"RPC request failed: {e}")


class TestResult:
    """
    测试结果类
    """

    def __init__(self, test_name):
        self.test_name = test_name
        self.start_time = datetime.now()
        self.end_time = None
        self.success = False
        self.error_message = None
        self.details = []

    def add_detail(self, message):
        """添加详细信息"""
        self.details.append(message)

    def mark_success(self):
        """标记成功"""
        self.success = True
        self.end_time = datetime.now()

    def mark_failure(self, error_message):
        """标记失败"""
        self.success = False
        self.error_message = error_message
        self.end_time = datetime.now()

    def to_dict(self):
        """转换为字典"""
        return {
            "test_name": self.test_name,
            "start_time": self.start_time.isoformat(),
            "end_time": self.end_time.isoformat() if self.end_time else None,
            "success": self.success,
            "error_message": self.error_message,
            "details": self.details
        }


class TestReport:
    """
    测试报告类
    """

    def __init__(self):
        self.results = []
        self.start_time = datetime.now()
        self.end_time = None

    def add_result(self, result):
        """添加测试结果"""
        self.results.append(result)

    def mark_complete(self):
        """标记所有测试完成"""
        self.end_time = datetime.now()

    def get_summary(self):
        """获取摘要"""
        total = len(self.results)
        passed = sum(1 for r in self.results if r.success)
        failed = total - passed
        return {
            "total": total,
            "passed": passed,
            "failed": failed,
            "success_rate": passed / total * 100 if total > 0 else 0
        }

    def save_to_file(self, file_path):
        """保存到文件"""
        self.mark_complete()

        data = {
            "start_time": self.start_time.isoformat(),
            "end_time": self.end_time.isoformat(),
            "duration": (self.end_time - self.start_time).total_seconds(),
            "summary": self.get_summary(),
            "results": [r.to_dict() for r in self.results]
        }

        with open(file_path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2, default=str)

    def save_text_report(self, file_path):
        """保存文本格式报告"""
        self.mark_complete()
        summary = self.get_summary()

        with open(file_path, "w", encoding="utf-8") as f:
            f.write("=" * 60 + "\n")
            f.write("DevHub M1 功能测试报告\n")
            f.write("=" * 60 + "\n\n")
            f.write(f"测试时间: {self.start_time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"完成时间: {self.end_time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"测试时长: {self.end_time - self.start_time}\n")
            f.write("\n")
            f.write(f"总测试数: {summary['total']}\n")
            f.write(f"通过数: {summary['passed']}\n")
            f.write(f"失败数: {summary['failed']}\n")
            f.write(f"成功率: {summary['success_rate']:.1f}%\n")
            f.write("\n" + "-" * 60 + "\n\n")

            for i, result in enumerate(self.results, 1):
                status = "✅" if result.success else "❌"
                f.write(f"{status} 测试 {i}: {result.test_name}\n")

                if result.details:
                    for detail in result.details:
                        f.write(f"  - {detail}\n")

                if not result.success and result.error_message:
                    f.write(f"  错误: {result.error_message}\n")

                f.write("\n")


def create_temp_directory():
    """创建 temp 目录"""
    temp_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "temp")
    os.makedirs(temp_dir, exist_ok=True)
    return temp_dir
