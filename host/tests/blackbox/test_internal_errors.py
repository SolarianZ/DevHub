#!/usr/bin/env python3
"""
DevHub internal_error / parse_error / invalid_request 测试
"""

import os
import uuid
import unittest
import requests


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcAssertions,
    RpcClient,
    TestResult,
    get_definitions_catalog_path,
    safe_remove,
)


class TestInternalErrors(unittest.TestCase):
    """错误处理与恢复测试类"""

    @staticmethod
    def _expect_jsonrpc_shape(result: TestResult, response, context: str):
        """断言响应满足 JSON-RPC 的基础可解析形态。"""
        if not isinstance(response, dict):
            result.mark_failure(f"❌ {context} 响应不是对象: {response}")
            return False

        has_result = "result" in response
        has_error = "error" in response
        if has_result == has_error:
            result.mark_failure(f"❌ {context} 响应必须且只能包含 result 或 error: {response}")
            return False

        if has_error and not isinstance(response.get("error"), dict):
            result.mark_failure(f"❌ {context} 的 error 不是对象: {response}")
            return False

        return True

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
            client = RpcClient(base_url, token)

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
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "jsonrpc 缺失",
                    "payload": {"id": "bad-envelope-0", "method": "hub.ping", "params": {}},
                    "expected_id": "bad-envelope-0"
                },
                {
                    "name": "jsonrpc 非 2.0",
                    "payload": {"jsonrpc": "1.0", "id": "bad-envelope-1", "method": "hub.ping", "params": {}},
                    "expected_id": "bad-envelope-1"
                },
                {
                    "name": "method 缺失",
                    "payload": {"jsonrpc": "2.0", "id": "bad-envelope-2", "params": {}},
                    "expected_id": "bad-envelope-2"
                },
                {
                    "name": "params 非 object/array",
                    "payload": {"jsonrpc": "2.0", "id": "bad-envelope-3", "method": "hub.ping", "params": "invalid"},
                    "expected_id": "bad-envelope-3"
                },
                {
                    "name": "params 为 null",
                    "payload": {"jsonrpc": "2.0", "id": "bad-envelope-4", "method": "hub.ping", "params": None},
                    "expected_id": "bad-envelope-4"
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

            root_type_cases = [
                {"name": "根节点为字符串", "body": '"not-an-object"'},
                {"name": "根节点为数字", "body": '123'},
                {"name": "根节点为布尔", "body": 'true'},
            ]

            for case in root_type_cases:
                status_code, response_payload = client.post_raw(case["body"], headers=headers)
                if not RpcAssertions.expect_http_status(result, status_code):
                    return result

                if not isinstance(response_payload, dict):
                    result.mark_failure(f"❌ {case['name']} 响应不是 JSON 对象: {response_payload}")
                    return result

                if not RpcAssertions.expect_error(
                    result,
                    response_payload,
                    expected_code=-32600,
                    expected_message="invalid_request",
                    expected_id=None
                ):
                    return result

                result.add_detail(f"✅ {case['name']} 正确返回 invalid_request")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_request_numeric_id_must_be_supported_integer(self):
        """测试 numeric JSON-RPC id 仅允许受支持整数"""
        result = TestResult("测试 numeric JSON-RPC id 仅允许受支持整数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "id 为小数", "id": 1.5},
                {"name": "id 超出 Int64 上界", "id": 9223372036854775808},
            ]

            for case in cases:
                status_code, response = client.post_json({
                    "jsonrpc": "2.0",
                    "id": case["id"],
                    "method": "hub.ping",
                    "params": {},
                })
                if not RpcAssertions.expect_http_status(result, status_code):
                    return result

                if not RpcAssertions.expect_error(
                    result,
                    response,
                    expected_code=-32600,
                    expected_message="invalid_request",
                    expected_id=None,
                ):
                    return result

                result.add_detail(f"✅ {case['name']} 正确返回 invalid_request")

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
            if not self._expect_jsonrpc_shape(result, response, "复杂参数场景"):
                return result

            if "error" in response:
                result.add_detail("✅ 复杂参数场景返回可解析错误响应")
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

            # 该场景聚焦稳定性与恢复能力，不对错误码取值做规范断言。
            acceptable = True
            success_count = 0
            error_count = 0
            for resp in responses:
                if "exception" in resp:
                    acceptable = False
                    break
                if not self._expect_jsonrpc_shape(result, resp, "并发压力子请求"):
                    acceptable = False
                    break
                if "error" in resp:
                    error_count += 1
                else:
                    success_count += 1

            if not acceptable:
                result.mark_failure("❌ 并发压力场景出现不可接受响应")
                return result

            ping_response = client.call("hub.ping")
            if not RpcAssertions.expect_success(result, ping_response, ["serverTimeUtc"]):
                return result

            result.add_detail(f"✅ 并发压力场景响应可解析（success={success_count}, error={error_count}）")
            result.add_detail("✅ 并发压力场景后服务仍可正常响应")
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

            if not self._expect_jsonrpc_shape(result, response, "超大负载场景"):
                return result

            if "error" in response:
                result.add_detail("✅ 超大负载场景返回可解析错误响应")
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
        invalid_app_path = None

        try:
            invalid_app_path = get_definitions_catalog_path()
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid": "json"')

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions", {"scope": None})

            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(invalid_app_path)

        return result

    def run_all_tests(self, full=False):
        """运行所有错误处理测试"""
        tests = [
            self.test_parse_error_invalid_json,
            self.test_invalid_request_envelope,
            self.test_invalid_request_numeric_id_must_be_supported_integer,
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
