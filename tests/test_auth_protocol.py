#!/usr/bin/env python3
"""
DevHub M1 鉴权与协议版本测试
"""

import os
import sys
import unittest

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestAuthProtocol(unittest.TestCase):
    """鉴权与协议版本测试类"""

    def test_ping_with_valid_credentials(self):
        """测试使用有效凭证调用 hub.ping"""
        result = TestResult("测试使用有效凭证调用 hub.ping")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 测试不带参数的情况
            response = client.call("hub.ping")

            if "result" in response and response["result"].get("ok") and "serverTimeUtc" in response["result"]:
                result.add_detail("✅ 调用成功")
                result.add_detail(f"服务器时间: {response['result']['serverTimeUtc']}")

                # 检查是否支持 echo 参数（可选）
                test_echo = "test-message-123"
                response_with_echo = client.call("hub.ping", {"echo": test_echo})

                if "result" in response_with_echo and "ok" in response_with_echo["result"] and response_with_echo["result"]["ok"] == True:
                    if "echo" in response_with_echo["result"] and response_with_echo["result"]["echo"] == test_echo:
                        result.add_detail(f"✅ Echo 参数测试成功: {response_with_echo['result']['echo']}")
                    else:
                        result.add_detail("⚠️  Echo 参数未实现")

                result.mark_success()
            else:
                result.mark_failure("❌ 响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_token(self):
        """测试缺少 token 的调用"""
        result = TestResult("测试缺少 token 的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"Authorization": ""}
            )

            if "error" in response and response["error"]["code"] == -32001:
                result.add_detail(f"✅ 正确返回未授权错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_with_invalid_token(self):
        """测试使用无效 token 的调用"""
        result = TestResult("测试使用无效 token 的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"Authorization": "Bearer invalid_token"}
            )

            if "error" in response and response["error"]["code"] == -32001:
                result.add_detail(f"✅ 正确返回未授权错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_with_invalid_protocol_version(self):
        """测试使用无效协议版本的调用"""
        result = TestResult("测试使用无效协议版本的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-Protocol": "2"}
            )

            if "error" in response and response["error"]["code"] == -32099:
                result.add_detail(f"✅ 正确返回不支持协议版本错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result


    def test_batch_request_rejected(self):
        """测试批量请求（数组根）被拒绝"""
        result = TestResult("测试批量请求（数组根）被拒绝")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 发送 batch 请求
            batch_request = [
                {
                    "jsonrpc": "2.0",
                    "id": "1",
                    "method": "hub.ping",
                    "params": {}
                },
                {
                    "jsonrpc": "2.0",
                    "id": "2",
                    "method": "hub.ping",
                    "params": {"echo": "test"}
                }
            ]

            response, status_code = client.send_batch_request(batch_request)

            # 验证 HTTP 状态码始终是 200 OK
            if status_code != 200:
                result.mark_failure(f"❌ HTTP 状态码不正确: {status_code}")
                return result

            # 验证响应包含错误
            if isinstance(response, dict) and "error" in response:
                # Spec.md 6.1 明确要求 batch 请求必须返回 -32600 invalid_request
                if response["error"]["code"] == -32600:
                    result.add_detail(f"✅ 正确返回错误: {response['error']['message']}")
                    result.mark_success()
                else:
                    result.mark_failure(f"❌ 错误码不正确: code={response['error']['code']}")
            else:
                result.mark_failure(f"❌ 响应格式不正确: {response}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_protocol_version_must_be_string(self):
        """测试 X-DevHub-Protocol 头部值必须是字符串 \"1\""""
        result = TestResult("测试 X-DevHub-Protocol 头部值必须是字符串 \"1\"")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 测试使用其他字符串值作为协议版本
            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-Protocol": "1.0"}  # 注意：这里是字符串但不是 "1"
            )

            if "error" in response and response["error"]["code"] == -32099:
                result.add_detail(f"✅ 正确返回不支持协议版本错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_client_id(self):
        """测试缺少X-DevHub-ClientId头部的调用"""
        result = TestResult("测试缺少X-DevHub-ClientId头部的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-ClientId": ""}
            )

            if "error" in response and response["error"]["code"] == -32600:
                result.add_detail(f"✅ 正确返回无效请求错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_client_session_id(self):
        """测试缺少X-DevHub-ClientSessionId头部的调用"""
        result = TestResult("测试缺少X-DevHub-ClientSessionId头部的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-ClientSessionId": ""}
            )

            if "error" in response and response["error"]["code"] == -32600:
                result.add_detail(f"✅ 正确返回无效请求错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_http_status_code_always_200(self):
        """测试 HTTP 响应状态码始终为 200 OK，即使发生错误"""
        result = TestResult("测试 HTTP 响应状态码始终为 200 OK")

        try:
            base_url, token = DiscoveryService.get_hub_info()

            # 直接使用 requests 库发送请求，以便检查 HTTP 状态码
            import requests
            headers = {
                "Content-Type": "application/json",
                "Authorization": f"Bearer {token}",
                "X-DevHub-Protocol": "1",
                "X-DevHub-ClientId": "PythonTestClient",
                "X-DevHub-ClientSessionId": "test-session-123"
            }

            # 测试有效请求
            payload = {
                "jsonrpc": "2.0",
                "id": "1",
                "method": "hub.ping",
                "params": {}
            }

            response = requests.post(f"{base_url}/rpc", json=payload, headers=headers, timeout=30)
            if response.status_code == 200:
                result.add_detail("✅ 有效请求返回 200 OK")
            else:
                result.mark_failure(f"❌ 有效请求返回了错误的状态码: {response.status_code}")
                return result

            # 测试无效请求（无效的方法名）
            payload = {
                "jsonrpc": "2.0",
                "id": "2",
                "method": "invalid.method",
                "params": {}
            }

            response = requests.post(f"{base_url}/rpc", json=payload, headers=headers, timeout=30)
            if response.status_code == 200:
                result.add_detail("✅ 无效方法请求返回 200 OK")
            else:
                result.mark_failure(f"❌ 无效方法请求返回了错误的状态码: {response.status_code}")
                return result

            # 测试无效 token 请求
            invalid_headers = headers.copy()
            invalid_headers["Authorization"] = "Bearer invalid_token"
            response = requests.post(f"{base_url}/rpc", json=payload, headers=invalid_headers, timeout=30)
            if response.status_code == 200:
                result.add_detail("✅ 无效 token 请求返回 200 OK")
            else:
                result.mark_failure(f"❌ 无效 token 请求返回了错误的状态码: {response.status_code}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self):
        """运行所有鉴权与协议版本测试"""
        return [
            self.test_ping_with_valid_credentials(),
            self.test_ping_without_token(),
            self.test_ping_with_invalid_token(),
            self.test_ping_with_invalid_protocol_version(),
            self.test_batch_request_rejected(),
            self.test_protocol_version_must_be_string(),
            self.test_http_status_code_always_200(),
            self.test_ping_without_client_id(),
            self.test_ping_without_client_session_id()
        ]


if __name__ == "__main__":
    # 运行测试
    test = TestAuthProtocol()
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
