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

from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestAppDefinitions(unittest.TestCase):
    """AppDefinition 测试类"""

    def _safe_remove_file(self, file_path):
        """仅删除当前测试创建的文件，避免误删运行时根目录"""
        if not file_path:
            return

        try:
            if os.path.exists(file_path):
                os.remove(file_path)
        except:
            pass

    def get_test_app_definition_path(self):
        """获取应用程序定义文件夹路径（使用规范目录）"""
        if "DEVHUB_APPDEFS_DIR" in os.environ:
            definitions_dir = os.environ["DEVHUB_APPDEFS_DIR"]
        else:
            from tests.test_base import DiscoveryService
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))

        os.makedirs(definitions_dir, exist_ok=True)
        return definitions_dir

    def create_test_app_definition(self):
        """创建测试应用程序定义"""
        definitions_dir = self.get_test_app_definition_path()
        test_app_path = os.path.join(definitions_dir, "test-app-1.json")  # 文件名与appId一致

        # 如果文件不存在则创建
        if not os.path.exists(test_app_path):
            test_app = {
                "appId": "test-app-1",
                "displayName": "Test Application",
                "description": "This is a test application",
                "scopePolicy": "any",
                "launch": {
                    "exePath": "echo",
                    "argsTemplate": "Hello from Test Application"
                },
                "capabilities": { "rpc": True, "events": False }
            }

            with open(test_app_path, "w", encoding="utf-8") as f:
                json.dump(test_app, f, ensure_ascii=False, indent=2)

        return "test-app-1", definitions_dir  # 返回appId和定义文件所在目录，以便测试后清理

    def test_list_definitions(self):
        """测试列出所有应用程序定义"""
        result = TestResult("测试列出所有应用程序定义")
        test_app_path = None

        try:
            # 确保有一个测试应用程序定义
            _, definitions_dir = self.create_test_app_definition()
            test_app_path = os.path.join(definitions_dir, "test-app-1.json")

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.listDefinitions")

            if "result" in response and response["result"].get("ok") and "definitions" in response["result"]:
                definitions = response["result"]["definitions"]
                result.add_detail(f"✅ 返回 {len(definitions)} 个应用程序定义")

                found_test_app = any(d.get("appId") == "test-app-1" for d in definitions)
                if found_test_app:
                    result.add_detail("✅ 测试应用程序定义在返回列表中")
                else:
                    result.mark_failure("❌ 未找到测试应用程序定义")
                    return result

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 仅清理当前测试创建的文件，避免误删 DevHub 运行时目录
            self._safe_remove_file(test_app_path)

        return result

    def test_get_definition(self):
        """测试获取单个应用程序定义"""
        result = TestResult("测试获取单个应用程序定义")
        test_app_path = None

        try:
            # 确保有一个测试应用程序定义
            app_id, definitions_dir = self.create_test_app_definition()
            test_app_path = os.path.join(definitions_dir, "test-app-1.json")

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": app_id})

            if "result" in response and response["result"].get("ok") and "definition" in response["result"]:
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
        finally:
            # 仅清理当前测试创建的文件，避免误删 DevHub 运行时目录
            self._safe_remove_file(test_app_path)

        return result

    def test_get_nonexistent_definition(self):
        """测试获取不存在的应用程序定义"""
        result = TestResult("测试获取不存在的应用程序定义")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": "non-existent-app"})

            if "error" in response and response["error"]["code"] == -32014:
                # 验证 error.message 与 Spec.md 一致
                if response["error"]["message"] == "app_definition_not_found":
                    result.add_detail("✅ 错误消息正确")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

                # 验证 error.data.appId（如果存在）包含请求的 appId
                if "data" in response["error"] and "appId" in response["error"]["data"]:
                    if response["error"]["data"].get("appId") == "non-existent-app":
                        result.add_detail("✅ 错误数据包含正确的 appId")
                    else:
                        result.mark_failure(f"❌ 错误数据中 appId 不正确: {response['error']['data'].get('appId')}")
                        return result

                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_app_definition_appid_format_validation(self):
        """测试应用程序定义的appId格式验证"""
        result = TestResult("测试应用程序定义的appId格式验证")
        definitions_dir = None

        try:
            # 获取真实的 DevHub 应用程序定义目录
            from tests.test_base import DiscoveryService
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))
            os.makedirs(definitions_dir, exist_ok=True)

            invalid_app_path = os.path.join(definitions_dir, "invalid app id.json")  # 文件名与appId一致

            # 创建appId格式无效的应用程序定义
            invalid_app = {
                "appId": "invalid app id",  # 包含空格，不符合格式要求
                "displayName": "Invalid AppId Application",
                "scopePolicy": "any"
            }

            with open(invalid_app_path, "w", encoding="utf-8") as f:
                json.dump(invalid_app, f, ensure_ascii=False, indent=2)

            # 调用 listDefinitions，DevHub 应忽略无效的appId格式
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")

            if "result" in response and response["result"].get("ok") and "definitions" in response["result"]:
                definitions = response["result"]["definitions"]
                found_invalid_app = any(d.get("appId") == "invalid app id" for d in definitions)

                if not found_invalid_app:
                    result.add_detail("✅ DevHub 正确忽略了appId格式无效的应用程序定义")
                else:
                    result.mark_failure("❌ DevHub 错误地加载了appId格式无效的应用程序定义")

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试文件
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def test_app_definition_scopepolicy_validation(self):
        """测试应用程序定义的scopePolicy值验证"""
        result = TestResult("测试应用程序定义的scopePolicy值验证")
        definitions_dir = None

        try:
            # 获取真实的 DevHub 应用程序定义目录
            from tests.test_base import DiscoveryService
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))
            os.makedirs(definitions_dir, exist_ok=True)

            invalid_app_path = os.path.join(definitions_dir, "invalid-scopepolicy-app.json")

            # 创建scopePolicy值无效的应用程序定义
            invalid_app = {
                "appId": "invalid-scopepolicy-app",
                "displayName": "Invalid ScopePolicy Application",
                "scopePolicy": "invalid"  # 无效的scopePolicy值
            }

            with open(invalid_app_path, "w", encoding="utf-8") as f:
                json.dump(invalid_app, f, ensure_ascii=False, indent=2)

            # 调用 listDefinitions，DevHub 应忽略无效的scopePolicy值
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")

            if "result" in response and response["result"].get("ok") and "definitions" in response["result"]:
                definitions = response["result"]["definitions"]
                found_invalid_app = any(d.get("appId") == "invalid-scopepolicy-app" for d in definitions)

                if not found_invalid_app:
                    result.add_detail("✅ DevHub 正确忽略了scopePolicy值无效的应用程序定义")
                else:
                    result.mark_failure("❌ DevHub 错误地加载了scopePolicy值无效的应用程序定义")

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试文件
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def test_invalid_app_definition(self):
        """测试无效格式的应用程序定义文件"""
        result = TestResult("测试无效格式的应用程序定义文件")

        try:
            # 获取真实的 DevHub 应用程序定义目录
            from tests.test_base import DiscoveryService
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))
            os.makedirs(definitions_dir, exist_ok=True)

            invalid_app_path = os.path.join(definitions_dir, "invalid-app.json")

            # 创建无效格式的应用程序定义
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid_field": "value"}')  # 缺少必填字段 appId, displayName, scopePolicy

            # 调用 listDefinitions，DevHub 应忽略无效的定义文件
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")

            if "result" in response and response["result"].get("ok") and "definitions" in response["result"]:
                definitions = response["result"]["definitions"]
                found_invalid_app = any(d.get("appId") == "invalid-app" for d in definitions)

                if not found_invalid_app:
                    result.add_detail("✅ DevHub 正确忽略了无效的应用程序定义文件")
                else:
                    result.mark_failure("❌ DevHub 错误地加载了无效的应用程序定义文件")

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试文件
            self._safe_remove_file(locals().get("invalid_app_path"))

        return result

    def run_all_tests(self):
        """运行所有 AppDefinition 测试"""
        return [
            self.test_list_definitions(),
            self.test_get_definition(),
            self.test_get_nonexistent_definition(),
            self.test_invalid_app_definition(),
            self.test_app_definition_appid_format_validation(),
            self.test_app_definition_scopepolicy_validation()
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
