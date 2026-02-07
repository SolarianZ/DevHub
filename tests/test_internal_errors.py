#!/usr/bin/env python3
"""
DevHub M1 internal_error / parse_error / invalid_request 测试
"""

import os
import sys
import uuid
import unittest
import requests

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestInternalErrors(unittest.TestCase):
    """错误处理与恢复测试类"""

    def _headers(self, token):
        """构造标准头"""
        return {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "PythonTestClient",
            "X-DevHub-ClientSessionId": str(uuid.uuid4())
        }

    def test_parse_error_invalid_json(self):
        """测试无效 JSON 返回 -32700 parse_error 且 id=null"""
        result = TestResult("测试无效 JSON 返回 parse_error 且 id=null")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            headers = self._headers(token)

            invalid_json = '{"jsonrpc":"2.0","id":"bad-json-id","method":"hub.ping","params":{'
            response = requests.post(f"{base_url}/rpc", data=invalid_json, headers=headers, timeout=30)

            if response.status_code != 200:
                result.mark_failure(f"❌ HTTP 状态码不正确: {response.status_code}")
                return result

            payload = response.json()
            if not RpcAssertions.expect_error(
                result,
                payload,
                expected_code=-32700,
                expected_message="parse_error",
                expected_id=None
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_request_envelope(self):
        """测试非法 JSON-RPC 信封返回 -32600 invalid_request"""
        result = TestResult("测试非法 JSON-RPC 信封返回 invalid_request")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            headers = self._headers(token)

            cases = [
                {
                    "name": "jsonrpc 非 2.0",
                    "payload": {"jsonrpc": "1.0", "id": "bad-envelope-1", "method": "hub.ping", "params": {}},
                    "expected_id": None
                },
                {
                    "name": "method 缺失",
                    "payload": {"jsonrpc": "2.0", "id": "bad-envelope-2", "params": {}},
                    "expected_id": None
                },
                {
                    "name": "params 非 object/array/null",
                    "payload": {"jsonrpc": "2.0", "id": "bad-envelope-3", "method": "hub.ping", "params": "invalid"},
                    "expected_id": "bad-envelope-3"
                }
            ]

            for case in cases:
                response = requests.post(f"{base_url}/rpc", json=case["payload"], headers=headers, timeout=30)
                if response.status_code != 200:
                    result.mark_failure(f"❌ {case['name']} 返回非200状态码: {response.status_code}")
                    return result

                payload = response.json()
                if not RpcAssertions.expect_error(
                    result,
                    payload,
                    expected_code=-32600,
                    expected_message="invalid_request",
                    expected_id=case["expected_id"]
                ):
                    return result

                result.add_detail(f"✅ {case['name']} 返回 invalid_request")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_internal_error_handling(self):
        """测试潜在内部错误场景后服务可继续工作"""
        result = TestResult("测试内部错误场景后服务可继续工作")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            complex_params = {
                "nested": {
                    "deep": {
                        "very": {
                            "complex": "structure" * 1000
                        }
                    }
                }
            }

            for index in range(20):
                complex_params["nested"][f"deep_{index}"] = {"very": {"complex": "structure" * 300}}

            response = client.call("hub.ping", complex_params)
            if "error" in response:
                code = response["error"].get("code")
                if code not in [-32602, -32603]:
                    result.mark_failure(f"❌ 复杂参数返回了意外错误码: {code}")
                    return result
                result.add_detail(f"✅ 复杂参数返回可接受错误码: {code}")
            else:
                if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                    return result
                result.add_detail("✅ 复杂参数被服务端成功处理")

            # 恢复性验证
            ping_response = client.call("hub.ping")
            if not RpcAssertions.expect_success(result, ping_response, ["serverTimeUtc"]):
                return result

            result.add_detail("✅ 服务在异常场景后仍可正常响应")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_server_resource_exhaustion_simulation(self):
        """测试资源压力场景（full 模式）"""
        result = TestResult("测试服务器资源压力场景")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            import threading

            responses = []

            def send_request():
                try:
                    responses.append(client.call("hub.ping", {"echo": "test" * 1000}))
                except Exception as ex:
                    responses.append({"exception": str(ex)})

            threads = []
            for _ in range(50):
                t = threading.Thread(target=send_request)
                threads.append(t)
                t.start()

            for t in threads:
                t.join(timeout=10)

            if not responses:
                result.mark_failure("❌ 未收到任何响应")
                return result

            # 至少要有可解析结果；错误码允许 -32603/-32040
            acceptable = True
            for resp in responses:
                if "exception" in resp:
                    acceptable = False
                    break
                if "error" in resp:
                    code = resp["error"].get("code")
                    if code not in [-32603, -32040]:
                        acceptable = False
                        break

            if not acceptable:
                result.mark_failure("❌ 并发压力场景出现不可接受响应")
                return result

            result.add_detail("✅ 并发压力场景响应符合预期范围")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_large_payload(self):
        """测试超大负载（full 模式）"""
        result = TestResult("测试超大负载")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            large_payload = "x" * 1024 * 1024
            response = client.call("hub.ping", {"echo": large_payload})

            if "error" in response:
                code = response["error"].get("code")
                if code not in [-32602, -32603]:
                    result.mark_failure(f"❌ 超大负载返回意外错误码: {code}")
                    return result
                result.add_detail(f"✅ 超大负载返回可接受错误码: {code}")
            else:
                if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                    return result
                result.add_detail("✅ 超大负载被成功处理")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_server_recovery_after_error(self):
        """测试错误后恢复能力"""
        result = TestResult("测试服务器在错误后的恢复能力")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            try:
                client.call("hub.ping", {"echo": "X" * (1024 * 1024)})
            except Exception:
                pass

            response = client.call("hub.ping")
            if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_concurrent_invalid_requests(self):
        """测试并发无效请求后服务可用性"""
        result = TestResult("测试并发无效请求后服务可用性")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            import threading

            def send_invalid_request():
                client.call("hub.apps.getDefinition", {"invalid_param": "value"})

            threads = []
            for _ in range(20):
                t = threading.Thread(target=send_invalid_request)
                threads.append(t)
                t.start()

            for t in threads:
                t.join(timeout=10)

            response = client.call("hub.ping")
            if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_app_definition_files(self):
        """测试无效定义文件不导致服务不可用"""
        result = TestResult("测试处理无效应用定义文件")

        try:
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))
            os.makedirs(definitions_dir, exist_ok=True)

            invalid_app_path = os.path.join(definitions_dir, "invalid-app-definition.json")
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid": "json"')

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions")

            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "invalid_app_path" in locals() and os.path.exists(invalid_app_path):
                    os.remove(invalid_app_path)
            except Exception:
                pass

        return result

    def run_all_tests(self, full=False):
        """运行所有错误处理测试"""
        tests = [
            self.test_parse_error_invalid_json,
            self.test_invalid_request_envelope,
            self.test_internal_error_handling,
            self.test_server_recovery_after_error,
            self.test_concurrent_invalid_requests,
            self.test_invalid_app_definition_files
        ]

        if full:
            tests.extend([
                self.test_server_resource_exhaustion_simulation,
                self.test_large_payload
            ])

        return [test() for test in tests]


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
