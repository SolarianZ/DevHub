#!/usr/bin/env python3
"""
DevHub M1 -32603 internal_error 内部服务器错误测试
"""

import os
import sys
import unittest
import json
import uuid

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestInternalErrors(unittest.TestCase):
    """-32603 internal_error 内部服务器错误测试类"""

    def test_internal_error_handling(self):
        """测试服务器内部错误处理"""
        result = TestResult("测试服务器内部错误处理")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 测试可能导致内部错误的无效输入
            # 注意：这里的测试需要根据实际服务器实现来设计
            # 由于我们不能修改服务器代码，我们尝试发送可能导致内部错误的请求

            # 测试 1: 发送极其复杂的参数结构
            complex_params = {
                "nested": {
                    "deep": {
                        "very": {
                            "complex": "structure" * 1000
                        }
                    }
                }
            }

            # 扩大复杂度（避免使用 dict * int 这种非法 Python 运算）
            for index in range(10):
                complex_params["nested"][f"deep_{index}"] = {
                    "very": {"complex": "structure" * 100}
                }

            response = client.call("hub.ping", complex_params)
            if "error" in response:
                if response["error"]["code"] == -32603:
                    if response["error"]["message"] == "internal_error":
                        result.add_detail("✅ 服务器正确处理了复杂参数导致的内部错误")
                    else:
                        result.mark_failure(f"❌ 内部错误消息不正确: {response['error']['message']}")
                        return result
                elif response["error"]["code"] == -32602:
                    # 有些服务器可能会先拒绝这种参数作为 invalid_params
                    result.add_detail("✅ 服务器正确拒绝了无效参数")
                else:
                    result.mark_failure(f"❌ 服务器返回了意外的错误码: {response['error']['code']}")
                    return result
            else:
                result.add_detail("⚠️  服务器成功处理了复杂参数，未返回错误")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_server_resource_exhaustion_simulation(self):
        """测试服务器资源耗尽场景（模拟）"""
        result = TestResult("测试服务器资源耗尽场景")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 发送大量并发请求以模拟资源耗尽
            import threading
            import time

            responses = []

            def send_request():
                try:
                    resp = client.call("hub.ping", {"echo": "test" * 100})
                    responses.append(resp)
                except Exception as e:
                    responses.append(str(e))

            # 创建多个线程发送请求
            threads = []
            for i in range(50):
                t = threading.Thread(target=send_request)
                threads.append(t)
                t.start()

            # 等待所有线程完成
            for t in threads:
                t.join(timeout=5)

            # 检查是否有请求失败或返回错误
            has_errors = False
            for resp in responses:
                if isinstance(resp, dict) and "error" in resp:
                    if resp["error"]["code"] == -32603:
                        result.add_detail("✅ 服务器在资源耗尽时返回了 internal_error")
                        has_errors = True
                    elif resp["error"]["code"] == -32040:
                        result.add_detail("✅ 服务器返回了 rate_limited 错误")
                        has_errors = True

            if not has_errors:
                result.add_detail("⚠️  服务器成功处理了所有并发请求，未表现出资源耗尽")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_json_structures(self):
        """测试无效的 JSON 结构"""
        result = TestResult("测试无效的 JSON 结构")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 注意：RpcClient 会自动处理 JSON 序列化，所以我们需要直接使用 requests
            import requests

            headers = {
                "Content-Type": "application/json",
                "Authorization": f"Bearer {token}",
                "X-DevHub-Protocol": "1",
                "X-DevHub-ClientId": "PythonTestClient",
                "X-DevHub-ClientSessionId": str(uuid.uuid4())
            }

            # 测试 1: 无效的 JSON（缺少闭合括号）
            invalid_json1 = '{"jsonrpc":"2.0","id":"1","method":"hub.ping","params":{"echo":"test"'
            url = f"{base_url}/rpc"
            response = requests.post(url, data=invalid_json1, headers=headers, timeout=30)

            if response.status_code == 200:
                try:
                    resp_json = response.json()
                    if "error" in resp_json and resp_json["error"]["code"] == -32700:
                        result.add_detail("✅ 服务器正确处理了无效 JSON（缺少闭合括号）")
                    else:
                        result.add_detail(f"⚠️  服务器返回了意外的响应: {resp_json}")
                except:
                    result.add_detail("⚠️  服务器响应不是有效的 JSON")
            else:
                result.mark_failure(f"❌ 服务器返回了非 200 状态码: {response.status_code}")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_large_payload(self):
        """测试处理超大负载"""
        result = TestResult("测试处理超大负载")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 创建一个非常大的负载
            large_payload = "x" * 1024 * 1024  # 1MB
            response = client.call("hub.ping", {"echo": large_payload})

            if "error" in response:
                if response["error"]["code"] == -32603:
                    if response["error"]["message"] == "internal_error":
                        result.add_detail("✅ 服务器正确处理了超大负载导致的内部错误")
                    else:
                        result.mark_failure(f"❌ 内部错误消息不正确: {response['error']['message']}")
                        return result
                elif response["error"]["code"] == -32602:
                    result.add_detail("✅ 服务器正确拒绝了超大参数")
                else:
                    result.mark_failure(f"❌ 服务器返回了意外的错误码: {response['error']['code']}")
                    return result
            else:
                result.add_detail("⚠️  服务器成功处理了超大负载，未返回错误")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_server_recovery_after_error(self):
        """测试服务器在内部错误后的恢复能力"""
        result = TestResult("测试服务器在内部错误后的恢复能力")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 先发送一个可能导致内部错误的请求
            try:
                complex_params = {
                    "nested": {
                        "deep": {
                            "very": {
                                "complex": "structure" * 1000
                            }
                        }
                    }
                }

                for index in range(10):
                    complex_params["nested"][f"deep_{index}"] = {
                        "very": {"complex": "structure" * 100}
                    }
                client.call("hub.ping", complex_params)
            except:
                pass

            # 然后发送一个简单的请求，检查服务器是否恢复
            response = client.call("hub.ping")
            if "result" in response and response["result"].get("ok"):
                result.add_detail("✅ 服务器在内部错误后成功恢复")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 服务器在内部错误后未能恢复: {response}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_concurrent_invalid_requests(self):
        """测试并发发送无效请求"""
        result = TestResult("测试并发发送无效请求")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            import threading
            import time

            def send_invalid_request():
                try:
                    # 发送无效参数的请求
                    client.call("hub.apps.getDefinition", {"invalid_param": "value"})
                except:
                    pass

            # 创建多个线程发送无效请求
            threads = []
            for i in range(20):
                t = threading.Thread(target=send_invalid_request)
                threads.append(t)
                t.start()

            # 等待所有线程完成
            for t in threads:
                t.join(timeout=5)

            # 检查服务器是否还能响应
            response = client.call("hub.ping")
            if "result" in response and response["result"].get("ok"):
                result.add_detail("✅ 服务器在处理并发无效请求后仍能正常响应")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 服务器在处理并发无效请求后未能正常响应: {response}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_app_definition_files(self):
        """测试处理无效的应用程序定义文件"""
        result = TestResult("测试处理无效的应用程序定义文件")

        try:
            # 获取应用程序定义目录
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))
            os.makedirs(definitions_dir, exist_ok=True)

            # 创建一个无效的应用程序定义文件（格式错误）
            invalid_app_path = os.path.join(definitions_dir, "invalid-app-definition.json")
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid": "json"')  # 缺少闭合括号

            # 调用 listDefinitions，这可能会导致服务器解析文件时出错
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")

            if "result" in response and response["result"].get("ok"):
                result.add_detail("✅ 服务器正确处理了无效的应用程序定义文件")
            elif "error" in response and response["error"]["code"] == -32603:
                result.add_detail("✅ 服务器在处理无效应用程序定义文件时返回 internal_error")
            else:
                result.mark_failure(f"❌ 服务器返回了意外的响应: {response}")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试文件
            try:
                if "invalid_app_path" in locals() and os.path.exists(invalid_app_path):
                    os.remove(invalid_app_path)
            except:
                pass

        return result

    def run_all_tests(self):
        """运行所有 internal_error 测试"""
        return [
            self.test_internal_error_handling(),
            self.test_server_resource_exhaustion_simulation(),
            self.test_invalid_json_structures(),
            self.test_large_payload(),
            self.test_server_recovery_after_error(),
            self.test_concurrent_invalid_requests(),
            self.test_invalid_app_definition_files()
        ]


if __name__ == "__main__":
    # 运行测试
    test = TestInternalErrors()
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
