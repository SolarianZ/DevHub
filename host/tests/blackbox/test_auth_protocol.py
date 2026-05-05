#!/usr/bin/env python3
"""
DevHub 鉴权与协议版本测试
"""

import os
import re
import uuid
import unittest
import requests


from tests.blackbox.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestAuthProtocol(unittest.TestCase):
    """鉴权与协议版本测试类"""
    _RPC_CORS_ALLOWED_HEADERS = [
        "Authorization",
        "Content-Type",
        "X-DevHub-Protocol",
        "X-DevHub-ClientId",
        "X-DevHub-ClientSessionId",
    ]

    def _build_payload(self, request_id, method="hub.ping", params=None):
        """构造 JSON-RPC 请求体"""
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

    def _build_headers(self, token, content_type="application/json", origin=None):
        """构造标准请求头"""
        headers = {
            "Content-Type": content_type,
            "Authorization": f"Bearer {token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "PythonTestClient",
            "X-DevHub-ClientSessionId": str(uuid.uuid4())
        }
        if origin:
            headers["Origin"] = origin
        return headers

    def _post_json(self, base_url, headers, payload):
        """发送 JSON 请求并返回 (status_code, json_response)"""
        response = requests.post(f"{base_url}/rpc", json=payload, headers=headers, timeout=30)
        return response.status_code, response.json()

    def _post_json_response(self, base_url, headers, payload):
        """发送 JSON 请求并返回原始 HTTP 响应。"""
        return requests.post(f"{base_url}/rpc", json=payload, headers=headers, timeout=30)

    def _assert_origin_cors_headers(self, result, response, origin, require_preflight=False):
        """断言带 Origin 的 /rpc 响应包含 CORS 头。"""
        allow_origin = response.headers.get("Access-Control-Allow-Origin")
        if allow_origin != origin:
            result.mark_failure(
                f"❌ Access-Control-Allow-Origin 不正确: 期望 {origin}，实际 {allow_origin}"
            )
            return False

        vary_values = [
            item.strip().lower()
            for item in response.headers.get("Vary", "").split(",")
            if item.strip()
        ]
        if "origin" not in vary_values:
            result.mark_failure(f"❌ Vary 头缺少 Origin: {response.headers.get('Vary')}")
            return False

        if not require_preflight:
            return True

        allow_methods = {
            item.strip().upper()
            for item in response.headers.get("Access-Control-Allow-Methods", "").split(",")
            if item.strip()
        }
        if not {"POST", "OPTIONS"}.issubset(allow_methods):
            result.mark_failure(
                f"❌ Access-Control-Allow-Methods 未包含 POST/OPTIONS: {response.headers.get('Access-Control-Allow-Methods')}"
            )
            return False

        allow_headers = {
            item.strip().lower()
            for item in response.headers.get("Access-Control-Allow-Headers", "").split(",")
            if item.strip()
        }
        for required_header in self._RPC_CORS_ALLOWED_HEADERS:
            if required_header.lower() not in allow_headers:
                result.mark_failure(
                    f"❌ Access-Control-Allow-Headers 缺少 {required_header}: {response.headers.get('Access-Control-Allow-Headers')}"
                )
                return False

        return True

    def test_ping_with_valid_credentials(self):
        """测试使用有效凭证调用 hub.ping"""
        result = TestResult("测试使用有效凭证调用 hub.ping")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.ping")
            if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                return result

            result.add_detail(f"✅ 服务器时间: {response['result']['serverTimeUtc']}")

            # hub.ping echo 为可选实现
            test_echo = "test-message-123"
            response_with_echo = client.call("hub.ping", {"echo": test_echo})
            if not RpcAssertions.expect_success(result, response_with_echo, ["serverTimeUtc"]):
                return result

            if response_with_echo["result"].get("echo") == test_echo:
                result.add_detail(f"✅ Echo 参数测试成功: {response_with_echo['result']['echo']}")
            else:
                result.add_detail("⚠️ Echo 参数未实现（允许）")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_get_version_with_valid_credentials(self):
        """测试使用有效凭证调用 hub.getVersion"""
        result = TestResult("测试使用有效凭证调用 hub.getVersion")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.getVersion")
            if not RpcAssertions.expect_success(result, response, ["version"]):
                return result

            version = response["result"].get("version")
            if not isinstance(version, str) or not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:[-+][0-9A-Za-z.-]+)?", version):
                result.mark_failure(f"❌ hub.getVersion.version 不是合法 SemVer: {version!r}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_token(self):
        """测试缺少 token 的调用"""
        result = TestResult("测试缺少 token 的调用")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            request_id = "auth-missing-token-id"

            headers = client.headers.copy()
            headers.pop("Authorization", None)
            _, response = client.post_json(self._build_payload(request_id), headers=headers)

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32001,
                expected_message="unauthorized",
                expected_id=request_id,
                expected_data={"reason": "missing_token"}
            ):
                return result

            result.mark_success()

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
                invalid_headers={"Authorization": "Bearer invalid_token"},
                request_id="auth-invalid-token-id"
            )

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32001,
                expected_message="unauthorized",
                expected_id=None,
                expected_data={"reason": "invalid_token"}
            ):
                return result

            result.mark_success()

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
                invalid_headers={"X-DevHub-Protocol": "2"},
                request_id="auth-invalid-protocol-id"
            )

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32099,
                expected_message="not_supported",
                expected_id="auth-invalid-protocol-id",
                expected_data={"expected": 1, "received": "2", "reason": "mismatch"}
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_protocol_header(self):
        """测试缺少协议版本头"""
        result = TestResult("测试缺少协议版本头")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            request_id = "auth-missing-protocol-id"
            headers = client.headers.copy()
            headers.pop("X-DevHub-Protocol", None)
            _, response = client.post_json(self._build_payload(request_id), headers=headers)

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32099,
                expected_message="not_supported",
                expected_id=None,
                expected_data={"expected": 1, "reason": "missing"}
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_client_id(self):
        """测试缺少 X-DevHub-ClientId"""
        result = TestResult("测试缺少 X-DevHub-ClientId")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            request_id = "auth-missing-client-id"
            headers = client.headers.copy()
            headers.pop("X-DevHub-ClientId", None)
            _, response = client.post_json(self._build_payload(request_id), headers=headers)

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id=request_id
            ):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "missing_header"}):
                return result

            error_data = response.get("error", {}).get("data", {})
            header_name = error_data.get("header")
            if header_name is not None and header_name != "X-DevHub-ClientId":
                result.mark_failure(f"❌ error.data.header 不正确: {header_name}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_ping_without_client_session_id(self):
        """测试缺少 X-DevHub-ClientSessionId"""
        result = TestResult("测试缺少 X-DevHub-ClientSessionId")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            request_id = "auth-missing-client-session-id"
            headers = client.headers.copy()
            headers.pop("X-DevHub-ClientSessionId", None)
            _, response = client.post_json(self._build_payload(request_id), headers=headers)

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id=request_id
            ):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "missing_header"}):
                return result

            error_data = response.get("error", {}).get("data", {})
            header_name = error_data.get("header")
            if header_name is not None and header_name != "X-DevHub-ClientSessionId":
                result.mark_failure(f"❌ error.data.header 不正确: {header_name}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_client_session_id_must_be_uuid(self):
        """测试 X-DevHub-ClientSessionId 必须为 UUID"""
        result = TestResult("测试 X-DevHub-ClientSessionId 必须为 UUID")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"X-DevHub-ClientSessionId": "not-a-uuid"},
                request_id="auth-invalid-session-id"
            )

            # Spec 要求为 UUID；当前实现若未校验，此测试会失败并提示实现不合规
            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id="auth-invalid-session-id"
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_authorization_must_use_bearer_scheme(self):
        """测试 Authorization 必须为 Bearer 方案"""
        result = TestResult("测试 Authorization 必须为 Bearer 方案")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call_with_invalid_headers(
                "hub.ping",
                invalid_headers={"Authorization": "Basic abcdef"},
                request_id="auth-invalid-scheme-id"
            )

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32001,
                expected_message="unauthorized",
                expected_id="auth-invalid-scheme-id"
            ):
                return result

            error_data = response.get("error", {}).get("data", {})
            reason = error_data.get("reason")
            if reason not in ["missing_token", "invalid_token"]:
                result.mark_failure(f"❌ unauthorized.reason 不在规范范围: {reason}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_batch_request_rejected(self):
        """测试批量请求（数组根）被拒绝"""
        result = TestResult("测试批量请求（数组根）被拒绝")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            batch_request = [
                {
                    "jsonrpc": "2.0",
                    "id": "1",
                    "method": "hub.ping",
                    "params": {}
                }
            ]

            response, status_code = client.send_batch_request(batch_request)
            if not RpcAssertions.expect_http_status(result, status_code):
                return result

            if not isinstance(response, dict):
                result.mark_failure(f"❌ batch 响应格式不正确: {response}")
                return result

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id=None
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_method_not_found(self):
        """测试未知方法返回 method_not_found"""
        result = TestResult("测试未知方法返回 method_not_found")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            request_id = "auth-method-not-found-id"

            response = client.call("hub.unknown.method", {}, request_id=request_id)
            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32601,
                expected_message="method_not_found",
                expected_id=request_id
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_jsonrpc_id_null_rejected(self):
        """测试 JSON-RPC id=null 被拒绝为 invalid_request"""
        result = TestResult("测试 JSON-RPC id=null 被拒绝")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            payload = {
                "jsonrpc": "2.0",
                "id": None,
                "method": "hub.ping",
                "params": {}
            }

            status_code, response = client.post_json(payload)
            if not RpcAssertions.expect_http_status(result, status_code):
                return result

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id=None
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_jsonrpc_id_must_be_string_or_number(self):
        """测试 JSON-RPC id 仅允许 string/number"""
        result = TestResult("测试 JSON-RPC id 仅允许 string/number")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "id 为对象", "id": {"bad": 1}},
                {"name": "id 为数组", "id": [1, 2, 3]},
            ]

            for case in cases:
                payload = {
                    "jsonrpc": "2.0",
                    "id": case["id"],
                    "method": "hub.ping",
                    "params": {}
                }

                status_code, response = client.post_json(payload)
                if not RpcAssertions.expect_http_status(result, status_code):
                    return result

                if not RpcAssertions.expect_error(
                    result,
                    response,
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

    def test_content_type_must_be_application_json(self):
        """测试 Content-Type 必须为 application/json"""
        result = TestResult("测试 Content-Type 必须为 application/json")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            headers = self._build_headers(token, content_type="text/plain")
            payload = {
                "jsonrpc": "2.0",
                "id": "auth-content-type-id",
                "method": "hub.ping",
                "params": {}
            }

            status_code, response = self._post_json(base_url, headers, payload)
            if status_code != 200:
                result.mark_failure(f"❌ HTTP 状态码不正确: {status_code}")
                return result

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32600,
                expected_message="invalid_request",
                expected_id=None
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_rpc_options_preflight_returns_cors_headers(self):
        """测试带 Origin 的 OPTIONS /rpc 预检返回 CORS 头。"""
        result = TestResult("测试带 Origin 的 OPTIONS /rpc 预检返回 CORS 头")

        try:
            base_url, _ = DiscoveryService.get_hub_info()
            origin = "http://localhost:1420"
            response = requests.options(
                f"{base_url}/rpc",
                headers={
                    "Origin": origin,
                    "Access-Control-Request-Method": "POST",
                    "Access-Control-Request-Headers": ", ".join(self._RPC_CORS_ALLOWED_HEADERS),
                },
                timeout=30,
            )

            if not RpcAssertions.expect_http_status(result, response.status_code, expected_status=204):
                return result

            if not self._assert_origin_cors_headers(result, response, origin, require_preflight=True):
                return result

            if response.text.strip():
                result.mark_failure(f"❌ OPTIONS /rpc 不应返回响应体: {response.text}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_post_with_origin_returns_cors_headers_on_success(self):
        """测试带 Origin 的 POST /rpc 成功响应返回 CORS 头。"""
        result = TestResult("测试带 Origin 的 POST /rpc 成功响应返回 CORS 头")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            origin = "tauri://localhost"
            response = self._post_json_response(
                base_url,
                self._build_headers(token, origin=origin),
                self._build_payload("cors-success-id"),
            )

            if not RpcAssertions.expect_http_status(result, response.status_code):
                return result

            body = response.json()
            if not RpcAssertions.expect_success(result, body, ["serverTimeUtc"]):
                return result

            if not self._assert_origin_cors_headers(result, response, origin):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_post_with_origin_returns_cors_headers_on_jsonrpc_error(self):
        """测试带 Origin 的 POST /rpc 错误响应返回 CORS 头。"""
        result = TestResult("测试带 Origin 的 POST /rpc 错误响应返回 CORS 头")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            origin = "http://localhost:1420"
            headers = self._build_headers(token, origin=origin)
            headers.pop("X-DevHub-Protocol", None)
            response = self._post_json_response(
                base_url,
                headers,
                self._build_payload("cors-error-id"),
            )

            if not RpcAssertions.expect_http_status(result, response.status_code):
                return result

            body = response.json()
            if not RpcAssertions.expect_error(
                result,
                body,
                expected_code=-32099,
                expected_message="not_supported",
                expected_id=None,
                expected_data={"expected": 1, "reason": "missing"}
            ):
                return result

            if not self._assert_origin_cors_headers(result, response, origin):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_http_status_code_always_200(self):
        """测试 HTTP 响应状态码始终为 200"""
        result = TestResult("测试 HTTP 响应状态码始终为 200")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            headers = self._build_headers(token)

            cases = [
                {
                    "name": "有效请求",
                    "payload": {"jsonrpc": "2.0", "id": "1", "method": "hub.ping", "params": {}},
                    "headers": headers
                },
                {
                    "name": "无效方法",
                    "payload": {"jsonrpc": "2.0", "id": "2", "method": "invalid.method", "params": {}},
                    "headers": headers
                },
                {
                    "name": "无效 token",
                    "payload": {"jsonrpc": "2.0", "id": "3", "method": "hub.ping", "params": {}},
                    "headers": {**headers, "Authorization": "Bearer invalid_token"}
                },
                {
                    "name": "缺少协议头",
                    "payload": {"jsonrpc": "2.0", "id": "4", "method": "hub.ping", "params": {}},
                    "headers": {**headers, "X-DevHub-Protocol": ""}
                }
            ]

            for case in cases:
                response = requests.post(f"{base_url}/rpc", json=case["payload"], headers=case["headers"], timeout=30)
                if response.status_code != 200:
                    result.mark_failure(f"❌ {case['name']} 返回非200状态码: {response.status_code}")
                    return result
                result.add_detail(f"✅ {case['name']} 返回 200 OK")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
        """运行所有鉴权与协议版本测试"""
        tests = [
            self.test_ping_with_valid_credentials,
            self.test_ping_without_token,
            self.test_ping_with_invalid_token,
            self.test_ping_with_invalid_protocol_version,
            self.test_ping_without_protocol_header,
            self.test_ping_without_client_id,
            self.test_ping_without_client_session_id,
            self.test_client_session_id_must_be_uuid,
            self.test_authorization_must_use_bearer_scheme,
            self.test_batch_request_rejected,
            self.test_method_not_found,
            self.test_jsonrpc_id_null_rejected,
            self.test_jsonrpc_id_must_be_string_or_number,
            self.test_content_type_must_be_application_json,
            self.test_rpc_options_preflight_returns_cors_headers,
            self.test_post_with_origin_returns_cors_headers_on_success,
            self.test_post_with_origin_returns_cors_headers_on_jsonrpc_error,
            self.test_http_status_code_always_200
        ]
        return [test() for test in tests]


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
