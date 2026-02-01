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

            response = client.call("hub.ping")

            if "result" in response and "ok" in response["result"] and response["result"]["ok"] == True and "serverTimeUtc" in response["result"]:
                result.add_detail("✅ 调用成功")
                result.add_detail(f"服务器时间: {response['result']['serverTimeUtc']}")
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

    def test_ping_missing_client_id(self):
        """测试缺少 X-DevHub-ClientId 的调用"""
        result = TestResult("测试缺少 X-DevHub-ClientId 的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-ClientId": ""}
            )

            if "error" in response and response["error"]["code"] == -32602:
                result.add_detail(f"✅ 正确返回无效参数错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_missing_session_id(self):
        """测试缺少 X-DevHub-ClientSessionId 的调用"""
        result = TestResult("测试缺少 X-DevHub-ClientSessionId 的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-ClientSessionId": ""}
            )

            if "error" in response and response["error"]["code"] == -32602:
                result.add_detail(f"✅ 正确返回无效参数错误: {response['error']['message']}")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

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
            self.test_ping_missing_client_id(),
            self.test_ping_missing_session_id()
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
