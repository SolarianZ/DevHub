#!/usr/bin/env python3
"""
DevHub M4 WS 传输矩阵补充测试
"""

import os
import sys
import json
import uuid
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions
from tests.test_ws_events import SimpleWebSocketClient


class TestWsTransportMatrix(unittest.TestCase):
    """WS 传输矩阵测试类（补齐 Spec 6.2）。"""

    @staticmethod
    def _runtime_hub_info():
        """读取运行时 HTTP/WS 地址与 token。"""
        http_base_url, token = DiscoveryService.get_hub_info()
        runtime_dir = DiscoveryService.get_runtime_directory()
        hub_json_path = os.path.join(runtime_dir, "hub.json")

        with open(hub_json_path, "r", encoding="utf-8") as f:
            hub_info = json.load(f)

        ws_url = hub_info.get("wsUrl")
        if not ws_url:
            raise ValueError("hub.json 缺少 wsUrl")

        return http_base_url, ws_url, token

    @staticmethod
    def _new_app_id(suffix):
        return f"m4-ws-transport-{suffix}-{uuid.uuid4().hex[:6]}"

    @staticmethod
    def _new_instance_id(suffix):
        return f"m4-ws-transport-{suffix}-{uuid.uuid4().hex[:10]}"

    def _definitions_dir(self):
        if "DEVHUB_APPDEFS_DIR" in os.environ:
            definitions_dir = os.environ["DEVHUB_APPDEFS_DIR"]
        else:
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))

        os.makedirs(definitions_dir, exist_ok=True)
        return definitions_dir

    def _create_definition(self, app_id):
        path = os.path.join(self._definitions_dir(), f"{app_id}.json")
        payload = {
            "appId": app_id,
            "displayName": app_id,
            "capabilities": {
                "rpc": True,
                "events": False,
            },
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        return path

    @staticmethod
    def _authenticate(ws, token, request_id):
        ws.send_json({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "hub.ws.authenticate",
            "params": {
                "token": token,
                "protocolVersion": 1,
                "clientId": "PyWsTransportMatrix",
                "clientSessionId": str(uuid.uuid4()),
            },
        })
        return ws.recv_json(timeout=3)

    @staticmethod
    def _ws_call(ws, request_id, method, params):
        ws.send_json({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params,
        })
        return ws.recv_json(timeout=3)

    @staticmethod
    def _expect_transport_rejected(result, response, request_id):
        if "error" not in response or not isinstance(response["error"], dict):
            result.mark_failure(f"❌ 期望传输受限错误，但响应缺少 error: {response}")
            return False

        if response.get("id") != request_id:
            result.mark_failure(f"❌ 传输受限错误 id 不匹配: {response}")
            return False

        if isinstance(response.get("result"), dict) and response["result"].get("ok") is True:
            result.mark_failure(f"❌ 期望传输受限错误，但返回了成功结果: {response}")
            return False

        code = response["error"].get("code")
        if not isinstance(code, int):
            result.mark_failure(f"❌ 传输受限错误码非法: {response}")
            return False

        allowed_codes = {-32600, -32601, -32002, -32099}
        if code not in allowed_codes:
            result.mark_failure(f"❌ 传输受限错误码不在允许集合: code={code}, response={response}")
            return False

        return True

    def test_m4_ws_matrix_001_ping_should_work_after_auth(self):
        """M4-WS-MATRIX-001: 鉴权后 hub.ping 可在 WS 调用。"""
        result = TestResult("M4-WS-MATRIX-001 鉴权后 WS hub.ping")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-001")
                if not RpcAssertions.expect_success(result, auth_response, ["protocolVersion"]):
                    return result

                response = self._ws_call(ws, "matrix-ping-001", "hub.ping", {})
                if not RpcAssertions.expect_success(result, response, ["serverTimeUtc"]):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_matrix_002_list_definitions_should_work_after_auth(self):
        """M4-WS-MATRIX-002: 鉴权后 hub.apps.listDefinitions 可在 WS 调用。"""
        result = TestResult("M4-WS-MATRIX-002 鉴权后 WS hub.apps.listDefinitions")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-002")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                response = self._ws_call(ws, "matrix-list-def-002", "hub.apps.listDefinitions", {})
                if not RpcAssertions.expect_success(result, response, ["definitions"]):
                    return result

                definitions = response.get("result", {}).get("definitions")
                if not isinstance(definitions, list):
                    result.mark_failure(f"❌ definitions 不是数组: {response}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_matrix_003_get_definition_should_work_after_auth(self):
        """M4-WS-MATRIX-003: 鉴权后 hub.apps.getDefinition 可在 WS 调用。"""
        result = TestResult("M4-WS-MATRIX-003 鉴权后 WS hub.apps.getDefinition")
        definition_path = None

        try:
            app_id = self._new_app_id("get-definition")
            definition_path = self._create_definition(app_id)
            _, ws_url, token = self._runtime_hub_info()

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-003")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                response = self._ws_call(ws, "matrix-get-def-003", "hub.apps.getDefinition", {"appId": app_id})
                if not RpcAssertions.expect_success(result, response, ["definition"]):
                    return result

                definition = response.get("result", {}).get("definition", {})
                if definition.get("appId") != app_id:
                    result.mark_failure(f"❌ definition.appId 不匹配: {definition}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if definition_path and os.path.exists(definition_path):
                    os.remove(definition_path)
            except Exception:
                pass

        return result

    def test_m4_ws_matrix_004_list_instances_should_work_after_auth(self):
        """M4-WS-MATRIX-004: 鉴权后 hub.apps.listInstances 可在 WS 调用。"""
        result = TestResult("M4-WS-MATRIX-004 鉴权后 WS hub.apps.listInstances")
        instance_id = None

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            http_client = RpcClient(http_base_url, token)

            app_id = self._new_app_id("list-instances")
            instance_id = self._new_instance_id("list-instances")

            register_response = http_client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="workspace-ws-matrix",
                poll=True,
                respond=True,
                pid=6301,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-004")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                response = self._ws_call(
                    ws,
                    "matrix-list-inst-004",
                    "hub.apps.listInstances",
                    {
                        "appId": app_id,
                        "includeAllScopes": True,
                        "includeOffline": True,
                    },
                )
                if not RpcAssertions.expect_success(result, response, ["instances"]):
                    return result

                instances = response.get("result", {}).get("instances", [])
                if not any(item.get("instanceId") == instance_id for item in instances):
                    result.mark_failure(f"❌ WS listInstances 未返回目标实例: {instances}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if instance_id:
                    http_base_url, _, token = self._runtime_hub_info()
                    RpcClient(http_base_url, token).unregister_instance(instance_id)
            except Exception:
                pass

        return result

    def test_m4_ws_matrix_005_invalid_params_should_be_enforced_after_auth(self):
        """M4-WS-MATRIX-005: 鉴权后方法参数仍必须遵循 Spec 参数校验。"""
        result = TestResult("M4-WS-MATRIX-005 鉴权后 WS 参数校验")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-005")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                invalid_get_def = self._ws_call(ws, "matrix-invalid-get-def", "hub.apps.getDefinition", {})
                if not RpcAssertions.expect_error(result, invalid_get_def, -32602, "invalid_params", expected_id="matrix-invalid-get-def"):
                    return result

                invalid_list_instances = self._ws_call(
                    ws,
                    "matrix-invalid-list-instances",
                    "hub.apps.listInstances",
                    {"scope": ""},
                )
                if not RpcAssertions.expect_error(
                    result,
                    invalid_list_instances,
                    -32602,
                    "invalid_params",
                    expected_id="matrix-invalid-list-instances",
                ):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_matrix_006_http_only_methods_should_be_rejected_over_ws(self):
        """M4-WS-MATRIX-006: HTTP-only 方法在 WS 下必须被拒绝。"""
        result = TestResult("M4-WS-MATRIX-006 WS 调用 HTTP-only 方法应拒绝")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, "matrix-auth-006")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                poll_response = self._ws_call(
                    ws,
                    "matrix-ws-http-only-poll",
                    "hub.invoke.poll",
                    {
                        "instanceId": "matrix-ws-http-only-instance",
                        "maxCount": 1,
                        "waitMs": 0,
                    },
                )
                if not self._expect_transport_rejected(result, poll_response, "matrix-ws-http-only-poll"):
                    return result

                poll_error_code = poll_response.get("error", {}).get("code")
                if poll_error_code == -32010:
                    result.mark_failure(f"❌ WS 端错误执行了 poll 业务分支: {poll_response}")
                    return result

                launch_response = self._ws_call(
                    ws,
                    "matrix-ws-http-only-launch",
                    "hub.apps.launch",
                    {
                        "appId": "matrix-ws-http-only-launch-app",
                        "scope": None,
                        "waitForRegisterMs": 0,
                    },
                )
                if not self._expect_transport_rejected(result, launch_response, "matrix-ws-http-only-launch"):
                    return result

                launch_error_code = launch_response.get("error", {}).get("code")
                if launch_error_code in {-32014, -32020}:
                    result.mark_failure(f"❌ WS 端错误执行了 launch 业务分支: {launch_response}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_matrix_007_ws_only_methods_should_be_rejected_over_http(self):
        """M4-WS-MATRIX-007: WS-only 方法在 HTTP 下必须被拒绝。"""
        result = TestResult("M4-WS-MATRIX-007 HTTP 调用 WS-only 方法应拒绝")

        try:
            http_base_url, _, token = self._runtime_hub_info()
            client = RpcClient(http_base_url, token)

            auth_response = client.call(
                "hub.ws.authenticate",
                {
                    "token": token,
                    "protocolVersion": 1,
                    "clientId": "PyWsTransportMatrix",
                    "clientSessionId": str(uuid.uuid4()),
                },
                request_id="matrix-http-ws-auth",
            )
            if not self._expect_transport_rejected(result, auth_response, "matrix-http-ws-auth"):
                return result

            subscribe_response = client.call(
                "hub.events.subscribe",
                {"types": ["app.instance.registered"]},
                request_id="matrix-http-ws-subscribe",
            )
            if not self._expect_transport_rejected(result, subscribe_response, "matrix-http-ws-subscribe"):
                return result

            unsubscribe_response = client.call(
                "hub.events.unsubscribe",
                {"subscriptionId": "sub-http-unsupported"},
                request_id="matrix-http-ws-unsubscribe",
            )
            if not self._expect_transport_rejected(result, unsubscribe_response, "matrix-http-ws-unsubscribe"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
        return [
            self.test_m4_ws_matrix_001_ping_should_work_after_auth(),
            self.test_m4_ws_matrix_002_list_definitions_should_work_after_auth(),
            self.test_m4_ws_matrix_003_get_definition_should_work_after_auth(),
            self.test_m4_ws_matrix_004_list_instances_should_work_after_auth(),
            self.test_m4_ws_matrix_005_invalid_params_should_be_enforced_after_auth(),
            self.test_m4_ws_matrix_006_http_only_methods_should_be_rejected_over_ws(),
            self.test_m4_ws_matrix_007_ws_only_methods_should_be_rejected_over_http(),
        ]


if __name__ == "__main__":
    test = TestWsTransportMatrix()
    results = test.run_all_tests()

    for result in results:
        status = "✅ 通过" if result.success else "❌ 失败"
        print(f"{status}: {result.test_name}")
        if result.details:
            for detail in result.details:
                print(f"  - {detail}")
        if result.error_message:
            print(f"  错误: {result.error_message}")
        print()
