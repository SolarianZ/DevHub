#!/usr/bin/env python3
"""
DevHub M1 AppDefinition 测试
"""

import os
import sys
import unittest
import json

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestAppDefinitions(unittest.TestCase):
    """AppDefinition 测试类"""

    def _safe_remove_file(self, file_path):
        """仅删除当前测试创建的文件，避免误删运行时根目录"""
        if not file_path:
            return
        try:
            if os.path.exists(file_path):
                os.remove(file_path)
        except Exception:
            pass

    def get_test_app_definition_path(self):
        """获取应用程序定义文件夹路径（使用规范目录）"""
        if "DEVHUB_APPDEFS_DIR" in os.environ:
            definitions_dir = os.environ["DEVHUB_APPDEFS_DIR"]
        else:
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))

        os.makedirs(definitions_dir, exist_ok=True)
        return definitions_dir

    def create_test_app_definition(self):
        """创建测试应用程序定义"""
        definitions_dir = self.get_test_app_definition_path()
        test_app_path = os.path.join(definitions_dir, "test-app-1.json")

        test_app = {
            "appId": "test-app-1",
            "displayName": "Test Application",
            "description": "This is a test application",
            "scopePolicy": "any",
            "launch": {
                "exePath": "echo",
                "argsTemplate": "Hello from Test Application"
            },
            "capabilities": {"rpc": True, "events": False}
        }

        with open(test_app_path, "w", encoding="utf-8") as f:
            json.dump(test_app, f, ensure_ascii=False, indent=2)

        return "test-app-1", test_app_path

    def test_list_definitions(self):
        """测试列出所有应用程序定义"""
        result = TestResult("测试列出所有应用程序定义")
        test_app_path = None

        try:
            _, test_app_path = self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.listDefinitions")
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            result.add_detail(f"✅ 返回 {len(definitions)} 个应用程序定义")

            found_test_app = any(d.get("appId") == "test-app-1" for d in definitions)
            if not found_test_app:
                result.mark_failure("❌ 未找到测试应用程序定义")
                return result

            result.add_detail("✅ 测试应用程序定义在返回列表中")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(test_app_path)

        return result

    def test_get_definition(self):
        """测试获取单个应用程序定义"""
        result = TestResult("测试获取单个应用程序定义")
        test_app_path = None

        try:
            app_id, test_app_path = self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": app_id})
            if not RpcAssertions.expect_success(result, response, ["definition"]):
                return result

            definition = response["result"]["definition"]
            if definition.get("appId") != app_id:
                result.mark_failure("❌ 返回的应用程序定义 ID 不匹配")
                return result

            result.add_detail("✅ 获取应用程序定义成功")
            result.add_detail(f"应用程序名称: {definition.get('displayName')}")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(test_app_path)

        return result

    def test_get_nonexistent_definition(self):
        """测试获取不存在的应用程序定义"""
        result = TestResult("测试获取不存在的应用程序定义")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": "non-existent-app"})
            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32014,
                expected_message="app_definition_not_found"
            ):
                return result

            error_data = response.get("error", {}).get("data", {})
            if error_data and error_data.get("appId") != "non-existent-app":
                result.mark_failure(f"❌ 错误数据中 appId 不正确: {error_data}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_app_definition(self):
        """测试无效格式的应用程序定义文件"""
        result = TestResult("测试无效格式的应用程序定义文件")

        try:
            definitions_dir = self.get_test_app_definition_path()
            invalid_app_path = os.path.join(definitions_dir, "invalid-app.json")

            # 缺少 appId/displayName/scopePolicy
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid_field": "value"}')

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found_invalid_app = any(d.get("appId") == "invalid-app" for d in definitions)
            if found_invalid_app:
                result.mark_failure("❌ DevHub 错误地加载了无效定义")
                return result

            result.add_detail("✅ DevHub 正确忽略了无效定义")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def test_app_definition_appid_format_validation(self):
        """测试应用程序定义 appId 格式验证"""
        result = TestResult("测试应用程序定义 appId 格式验证")

        try:
            definitions_dir = self.get_test_app_definition_path()
            invalid_app_path = os.path.join(definitions_dir, "invalid app id.json")

            invalid_app = {
                "appId": "invalid app id",
                "displayName": "Invalid AppId Application",
                "scopePolicy": "any"
            }

            with open(invalid_app_path, "w", encoding="utf-8") as f:
                json.dump(invalid_app, f, ensure_ascii=False, indent=2)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found_invalid_app = any(d.get("appId") == "invalid app id" for d in definitions)
            if found_invalid_app:
                result.mark_failure("❌ DevHub 错误地加载了 appId 格式无效的定义")
                return result

            result.add_detail("✅ DevHub 正确忽略 appId 格式无效定义")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def test_app_definition_scopepolicy_validation(self):
        """测试应用程序定义 scopePolicy 值验证"""
        result = TestResult("测试应用程序定义 scopePolicy 值验证")

        try:
            definitions_dir = self.get_test_app_definition_path()
            invalid_app_path = os.path.join(definitions_dir, "invalid-scopepolicy-app.json")

            invalid_app = {
                "appId": "invalid-scopepolicy-app",
                "displayName": "Invalid ScopePolicy Application",
                "scopePolicy": "invalid"
            }

            with open(invalid_app_path, "w", encoding="utf-8") as f:
                json.dump(invalid_app, f, ensure_ascii=False, indent=2)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found_invalid_app = any(d.get("appId") == "invalid-scopepolicy-app" for d in definitions)
            if found_invalid_app:
                result.mark_failure("❌ DevHub 错误地加载了 scopePolicy 无效定义")
                return result

            result.add_detail("✅ DevHub 正确忽略 scopePolicy 无效定义")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def test_definition_filename_must_match_appid(self):
        """测试文件名与 appId 不一致时应被忽略"""
        result = TestResult("测试文件名与 appId 不一致时应被忽略")

        try:
            definitions_dir = self.get_test_app_definition_path()
            mismatch_path = os.path.join(definitions_dir, "mismatch-name.json")

            mismatch_app = {
                "appId": "real-app-id",
                "displayName": "Mismatch Name Application",
                "scopePolicy": "any"
            }

            with open(mismatch_path, "w", encoding="utf-8") as f:
                json.dump(mismatch_app, f, ensure_ascii=False, indent=2)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found = any(d.get("appId") == "real-app-id" for d in definitions)
            if found:
                result.mark_failure("❌ 文件名与 appId 不一致的定义不应被加载")
                return result

            result.add_detail("✅ 文件名与 appId 不一致的定义被正确忽略")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._safe_remove_file(locals().get("mismatch_path"))

        return result

    def run_all_tests(self, full=False):
        """运行所有 AppDefinition 测试"""
        return [
            self.test_list_definitions(),
            self.test_get_definition(),
            self.test_get_nonexistent_definition(),
            self.test_invalid_app_definition(),
            self.test_app_definition_appid_format_validation(),
            self.test_app_definition_scopepolicy_validation(),
            self.test_definition_filename_must_match_appid()
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
