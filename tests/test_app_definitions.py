#!/usr/bin/env python3
"""
DevHub M1 AppDefinition 测试
"""

import unittest
import os
import json
from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestAppDefinitions(unittest.TestCase):
    """AppDefinition 测试类"""

    def get_test_app_definition_path(self):
        """获取应用程序定义文件夹路径"""
        system = os.name
        if system == "nt":  # Windows
            definitions_dir = os.path.join(os.environ["LOCALAPPDATA"], "DevHub", "apps", "definitions")
        elif system == "posix":
            if os.uname().sysname == "Darwin":  # macOS
                definitions_dir = os.path.join(os.environ["HOME"], "Library", "Application Support", "DevHub", "apps", "definitions")
            else:  # Linux
                definitions_dir = os.path.join(os.environ["HOME"], ".local", "share", "DevHub", "apps", "definitions")

        os.makedirs(definitions_dir, exist_ok=True)
        return definitions_dir

    def create_test_app_definition(self):
        """创建测试应用程序定义"""
        definitions_dir = self.get_test_app_definition_path()
        test_app_path = os.path.join(definitions_dir, "test-app.json")

        # 如果文件不存在则创建
        if not os.path.exists(test_app_path):
            test_app = {
                "appId": "test-app-1",
                "displayName": "Test Application",
                "description": "This is a test application",
                "scopePolicy": "global",
                "launch": {
                    "command": "echo",
                    "args": ["Hello from Test Application"]
                },
                "capabilities": ["test"]
            }

            with open(test_app_path, "w", encoding="utf-8") as f:
                json.dump(test_app, f, ensure_ascii=False, indent=2)

        return "test-app-1"

    def test_list_definitions(self):
        """测试列出所有应用程序定义"""
        result = TestResult("测试列出所有应用程序定义")

        try:
            # 确保有一个测试应用程序定义
            self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.listDefinitions")

            if "result" in response and "definitions" in response["result"]:
                definitions = response["result"]["definitions"]
                result.add_detail(f"✅ 返回 {len(definitions)} 个应用程序定义")

                found_test_app = any(d.get("appId") == "test-app-1" for d in definitions)
                if found_test_app:
                    result.add_detail("✅ 测试应用程序定义在返回列表中")
                else:
                    result.add_detail("⚠️  未找到测试应用程序定义")

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_get_definition(self):
        """测试获取单个应用程序定义"""
        result = TestResult("测试获取单个应用程序定义")

        try:
            # 确保有一个测试应用程序定义
            app_id = self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": app_id})

            if "result" in response and "definition" in response["result"]:
                definition = response["result"]["definition"]
                if definition.get("appId") == app_id:
                    result.add_detail(f"✅ 获取应用程序定义成功")
                    result.add_detail(f"应用程序名称: {definition.get('displayName')}")
                    result.mark_success()
                else:
                    result.mark_failure("❌ 返回的应用程序定义 ID 不匹配")
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_get_nonexistent_definition(self):
        """测试获取不存在的应用程序定义"""
        result = TestResult("测试获取不存在的应用程序定义")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": "non-existent-app"})

            if "error" in response and response["error"]["code"] == -32014:
                result.add_detail(f"✅ 正确返回应用程序定义不存在错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self):
        """运行所有 AppDefinition 测试"""
        return [
            self.test_list_definitions(),
            self.test_get_definition(),
            self.test_get_nonexistent_definition()
        ]


if __name__ == "__main__":
    # 运行测试
    test = TestAppDefinitions()
    results = test.run_all_tests()

    # 输出结果
    for result in results:
        status = "✅ 通过" if result.success else "❌ 失败"
        print(f"{status}: {result.test_name}")

        if result.details:
            for detail in result.details:
                print(f"  - {detail}")

        if result.error_message:
            print(f"  错误: {result.error_message}")

        print()
