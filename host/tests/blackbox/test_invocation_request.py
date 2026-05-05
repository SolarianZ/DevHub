#!/usr/bin/env python3
"""
DevHub 调用请求冒烟测试
"""

import os
import time
import uuid
import json
import threading
import unittest


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcClient,
    RpcAssertions,
    TestResult,
    new_instance_id,
    safe_remove,
    unregister_instances,
    write_app_definition,
)


class TestInvocationRequest(unittest.TestCase):
    """Invocation request 测试类"""

    def _create_definition(self, app_id, rpc=True):
        return write_app_definition(app_id, rpc=rpc, events=False)

    @staticmethod
    def _new_app_id(prefix):
        return f"{prefix}-{uuid.uuid4().hex[:8]}"

    @staticmethod
    def _instance_id(prefix):
        return new_instance_id(prefix)

    def test_request_roundtrip_success(self):
        """REQ-001: request 成功往返"""
        result = TestResult("REQ-001 request 成功往返")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = self._new_app_id("request-success-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-success")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24001,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            poll_outcome = {}

            def callee_worker():
                poll_response = client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                poll_outcome["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if not items:
                    return

                invocation_id = items[0].get("invocationId")
                lease_token = items[0].get("delivery", {}).get("leaseToken")
                poll_outcome["invocation_id"] = invocation_id
                respond_response = client.respond_value(callee_instance_id, invocation_id, {"status": "ok", "count": 1}, lease_token=lease_token)
                poll_outcome["respond"] = respond_response

            worker = threading.Thread(target=callee_worker, daemon=True)
            worker.start()

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                args={"branch": "main"},
                options={
                    "ttlMs": 5000,
                    "waitTimeoutMs": 2000,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-success",
            )

            worker.join(timeout=3)

            if not RpcAssertions.expect_success(result, request_response, ["invocationId", "value"]):
                return result

            value = request_response["result"].get("value", {})
            if value.get("status") != "ok":
                result.mark_failure(f"❌ request 返回 value.status 不正确: {value}")
                return result

            respond_response = poll_outcome.get("respond")
            if not respond_response:
                result.mark_failure("❌ callee 未执行 respond")
                return result

            if not RpcAssertions.expect_success(result, respond_response):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def test_request_timeout_then_late_respond_expired(self):
        """REQ-002/003: request 超时 + 迟到 respond 过期"""
        result = TestResult("REQ-002/003 request 超时与迟到响应")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = self._new_app_id("request-timeout-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-timeout")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24002,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            poll_holder = {}

            def callee_poll_only():
                poll_response = client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                poll_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if items:
                    poll_holder["invocation_id"] = items[0].get("invocationId")
                    poll_holder["lease_token"] = items[0].get("delivery", {}).get("leaseToken")

            worker = threading.Thread(target=callee_poll_only, daemon=True)
            worker.start()

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.slow",
                args={"x": 1},
                options={
                    "ttlMs": 5000,
                    "waitTimeoutMs": 300,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-timeout",
            )

            worker.join(timeout=3)

            if not RpcAssertions.expect_error(result, request_response, -32012, "invocation_timeout"):
                return result

            timeout_data = request_response.get("error", {}).get("data", {})
            invocation_id = timeout_data.get("invocationId")
            if not invocation_id:
                result.mark_failure(f"❌ timeout 响应缺少 invocationId: {request_response}")
                return result

            elapsed_ms = timeout_data.get("elapsedMs")
            if not isinstance(elapsed_ms, int):
                result.mark_failure(f"❌ timeout 响应缺少 elapsedMs: {request_response}")
                return result

            late_respond = client.respond_value(callee_instance_id, invocation_id, {"ok": True}, lease_token=poll_holder.get("lease_token"))
            if not RpcAssertions.expect_error(result, late_respond, -32011, "invocation_expired"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def test_request_ttl_expired_should_return_invocation_expired(self):
        """REQ-007: TTL 到期时 caller 应收到 invocation_expired"""
        result = TestResult("REQ-007 request TTL 到期返回 invocation_expired")
        definition_path = None

        try:
            app_id = self._new_app_id("request-ttl-expired-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id=app_id,
                method="asset.ttl-expired",
                args={"x": 1},
                options={
                    "ttlMs": 1000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-ttl-expired",
            )

            if not RpcAssertions.expect_error(result, response, -32011, "invocation_expired"):
                return result

            error_data = response.get("error", {}).get("data", {})
            invocation_id = error_data.get("invocationId")
            if not isinstance(invocation_id, str) or not invocation_id:
                result.mark_failure(f"❌ invocation_expired 缺少 invocationId: {response}")
                return result

            elapsed_ms = error_data.get("elapsedMs")
            if not isinstance(elapsed_ms, int):
                result.mark_failure(f"❌ invocation_expired 缺少整数 elapsedMs: {response}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_request_client_cancel_then_respond_can_still_succeed(self):
        """REQ-004: caller 中断后 request 收口，但 invocation 仍可在预算内完成"""
        result = TestResult("REQ-004 caller 中断后 invocation 仍可完成")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = self._new_app_id("request-cancel-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-cancel")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24003,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            request_payload = {
                "jsonrpc": "2.0",
                "id": "request-cancel",
                "method": "hub.invoke.request",
                "params": {
                    "appId": app_id,
                    "target": {
                        "scope": "",
                        "instanceId": None,
                    },
                    "method": "asset.cancel",
                    "args": {"x": 2},
                    "options": {
                        "ttlMs": 5000,
                        "waitTimeoutMs": 3000,
                        "queueIfOffline": True,
                        "autoLaunch": False,
                    },
                },
            }

            request_error_holder = {}
            poll_holder = {}

            def callee_poll_worker():
                poll_client = RpcClient(base_url, token)
                poll_response = poll_client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                poll_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if len(items) == 1:
                    poll_holder["invocationId"] = items[0].get("invocationId")
                    poll_holder["leaseToken"] = items[0].get("delivery", {}).get("leaseToken")

            def caller_worker():
                try:
                    _, response = client.post_json(request_payload, timeout=0.15)
                    request_error_holder["response"] = response
                except Exception as exc:
                    request_error_holder["error"] = str(exc)

            poll_thread = threading.Thread(target=callee_poll_worker, daemon=True)
            caller_thread = threading.Thread(target=caller_worker, daemon=True)
            poll_thread.start()
            caller_thread.start()
            caller_thread.join(timeout=2)
            poll_thread.join(timeout=3)

            if "response" in request_error_holder:
                result.mark_failure(f"❌ caller 中断场景不应收到同步响应: {request_error_holder['response']}")
                return result

            if "error" not in request_error_holder:
                result.mark_failure(f"❌ caller 中断场景未出现超时类异常: {request_error_holder}")
                return result

            poll_response = poll_holder.get("poll")
            if not isinstance(poll_response, dict):
                result.mark_failure(f"❌ callee poll 未返回有效响应: {poll_holder}")
                return result
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if len(items) != 1:
                result.mark_failure(f"❌ caller 中断后未稳定拉取到唯一 invocation: {poll_response}")
                return result

            invocation_id = items[0].get("invocationId")
            if not invocation_id:
                result.mark_failure(f"❌ poll 返回缺少 invocationId: {poll_response}")
                return result

            time.sleep(0.35)

            late_respond = client.respond_value(callee_instance_id, invocation_id, {"ok": True}, lease_token=poll_holder.get("leaseToken"))
            if not RpcAssertions.expect_success(result, late_respond):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def test_request_client_cancel_then_timeout_still_expires(self):
        """REQ-004-REG: caller 中断后 waitTimeout 仍独立生效"""
        result = TestResult("REQ-004-REG caller 中断后 waitTimeout 仍生效")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = self._new_app_id("request-cancel-timeout-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-cancel-timeout")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24004,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            request_payload = {
                "jsonrpc": "2.0",
                "id": "request-cancel-timeout",
                "method": "hub.invoke.request",
                "params": {
                    "appId": app_id,
                    "target": {
                        "scope": "",
                        "instanceId": None,
                    },
                    "method": "asset.cancel-timeout",
                    "args": {"x": 3},
                    "options": {
                        "ttlMs": 5000,
                        "waitTimeoutMs": 300,
                        "queueIfOffline": True,
                        "autoLaunch": False,
                    },
                },
            }

            request_error_holder = {}
            poll_holder = {}

            def callee_poll_worker():
                poll_client = RpcClient(base_url, token)
                poll_response = poll_client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                poll_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if len(items) == 1:
                    poll_holder["invocationId"] = items[0].get("invocationId")
                    poll_holder["leaseToken"] = items[0].get("delivery", {}).get("leaseToken")

            def caller_worker():
                try:
                    _, response = client.post_json(request_payload, timeout=0.15)
                    request_error_holder["response"] = response
                except Exception as exc:
                    request_error_holder["error"] = str(exc)

            poll_thread = threading.Thread(target=callee_poll_worker, daemon=True)
            caller_thread = threading.Thread(target=caller_worker, daemon=True)
            poll_thread.start()
            caller_thread.start()
            caller_thread.join(timeout=2)
            poll_thread.join(timeout=3)

            if "response" in request_error_holder:
                result.mark_failure(f"❌ caller 中断场景不应收到同步响应: {request_error_holder['response']}")
                return result

            if "error" not in request_error_holder:
                result.mark_failure(f"❌ caller 中断场景未出现超时类异常: {request_error_holder}")
                return result

            poll_response = poll_holder.get("poll")
            if not isinstance(poll_response, dict):
                result.mark_failure(f"❌ callee poll 未返回有效响应: {poll_holder}")
                return result
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if len(items) != 1:
                result.mark_failure(f"❌ caller 中断后未稳定拉取到唯一 invocation: {poll_response}")
                return result

            invocation_id = items[0].get("invocationId")
            if not invocation_id:
                result.mark_failure(f"❌ poll 返回缺少 invocationId: {poll_response}")
                return result

            time.sleep(0.45)

            late_respond = client.respond_value(callee_instance_id, invocation_id, {"ok": True}, lease_token=poll_holder.get("leaseToken"))
            if not RpcAssertions.expect_error(result, late_respond, -32011, "invocation_expired"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def test_request_invalid_waittimeout_gt_ttl(self):
        """request 参数边界: waitTimeoutMs > ttlMs"""
        result = TestResult("request 参数边界 waitTimeoutMs > ttlMs")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id="request-invalid-app",
                method="asset.build",
                args={},
                options={
                    "ttlMs": 1000,
                    "waitTimeoutMs": 1001,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-invalid-wait",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_request_invalid_target_instance_with_autolaunch_true(self):
        """request 参数边界: target.instanceId + autoLaunch=true"""
        result = TestResult("request 参数边界 target.instanceId + autoLaunch=true")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id="request-invalid-app",
                method="asset.build",
                args={},
                target_instance_id="inst-target",
                options={
                    "ttlMs": 2000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": True,
                    "autoLaunch": True,
                },
                request_id="request-invalid-target",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_request_offline_without_queue_should_fail(self):
        """request 参数边界: queueIfOffline=false 且无在线实例"""
        result = TestResult("request 参数边界 queueIfOffline=false")
        definition_path = None

        try:
            app_id = self._new_app_id("request-offline-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                args={},
                options={
                    "ttlMs": 3000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="request-offline-noqueue",
            )

            if not RpcAssertions.expect_error(result, response, -32010, "instance_not_found"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "offline_no_queue"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_request_target_instance_missing_should_return_specific_reason(self):
        """request 指定 target.instanceId 且不可达时返回 target_instance_missing"""
        result = TestResult("request target.instanceId 缺失返回 target_instance_missing")
        definition_path = None

        try:
            app_id = self._new_app_id("request-target-missing-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                args={},
                target_instance_id="inst-target-not-found",
                options={
                    "ttlMs": 3000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="request-target-missing",
            )

            if not RpcAssertions.expect_error(result, response, -32010, "instance_not_found"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "target_instance_missing"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_request_overlong_target_instance_id_should_return_invalid_params(self):
        """request 超长 target.instanceId 在路由前返回 invalid_params"""
        result = TestResult("request 超长 target.instanceId 返回 invalid_params")
        definition_path = None

        try:
            app_id = self._new_app_id("request-overlong-target-id")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                target_instance_id="a" * 257,
                options={
                    "ttlMs": 300000,
                    "waitTimeoutMs": 120000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="request-overlong-target-id",
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "invalid_target_instance"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_request_callee_error_should_return_invocation_failed(self):
        """REQ-005: callee respond_error 时 caller 返回 invocation_failed。"""
        result = TestResult("REQ-005 request callee error 返回 invocation_failed")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = self._new_app_id("request-failed-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-failed")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24004,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            callee_error = {
                "code": 1001,
                "message": "app_error",
                "data": {"reason": "mock"}
            }

            def callee_worker():
                poll_response = client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if not items:
                    return

                invocation_id = items[0].get("invocationId")
                lease_token = items[0].get("delivery", {}).get("leaseToken")
                client.respond_error(callee_instance_id, invocation_id, callee_error, lease_token=lease_token)

            worker = threading.Thread(target=callee_worker, daemon=True)
            worker.start()

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.fail",
                args={"x": 3},
                options={
                    "ttlMs": 5000,
                    "waitTimeoutMs": 2000,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-failed",
            )

            worker.join(timeout=3)

            if not RpcAssertions.expect_error(result, request_response, -32050, "invocation_failed"):
                return result

            error_data = request_response.get("error", {}).get("data", {})
            if not isinstance(error_data.get("invocationId"), str):
                result.mark_failure(f"❌ invocation_failed 缺少 invocationId: {request_response}")
                return result

            callee_error_data = error_data.get("calleeError")
            if not isinstance(callee_error_data, dict):
                result.mark_failure(f"❌ invocation_failed 缺少 calleeError 对象: {request_response}")
                return result

            if callee_error_data.get("code") != 1001 or callee_error_data.get("message") != "app_error":
                result.mark_failure(f"❌ calleeError code/message 不匹配: {callee_error_data}")
                return result

            if callee_error_data.get("data", {}).get("reason") != "mock":
                result.mark_failure(f"❌ calleeError.data.reason 不匹配: {callee_error_data}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def test_request_rpc_disabled_should_forbidden(self):
        """REQ-006: capabilities.rpc=false 时 request 返回 forbidden/rpc_disabled。"""
        result = TestResult("REQ-006 request rpc_disabled 门禁")
        definition_path = None

        try:
            app_id = self._new_app_id("request-rpc-disabled-app")
            definition_path = self._create_definition(app_id, rpc=False)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_request(
                app_id=app_id,
                method="asset.blocked",
                args={},
                options={
                    "ttlMs": 3000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": True,
                    "autoLaunch": False,
                },
                request_id="request-rpc-disabled",
            )

            if not RpcAssertions.expect_error(result, response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "rpc_disabled"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_request_defaults_should_follow_spec_when_options_omitted(self):
        """request 默认值: 省略 options 时应采用 Spec 默认语义。"""
        result = TestResult("request 默认值语义校验")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = f"request-defaults-app-{uuid.uuid4().hex[:8]}"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("request-defaults")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=24011,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            request_result_holder = {}

            def callee_worker():
                poll_response = client.poll_once(callee_instance_id, max_count=1, wait_ms=1500)
                request_result_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if not items:
                    return

                item = items[0]
                request_result_holder["item"] = item
                invocation_id = item.get("invocationId")
                lease_token = item.get("delivery", {}).get("leaseToken")
                if invocation_id:
                    request_result_holder["respond"] = client.respond_value(
                        callee_instance_id,
                        invocation_id,
                        {"handledBy": "defaults"},
                        lease_token=lease_token,
                    )

            worker = threading.Thread(target=callee_worker, daemon=True)
            worker.start()

            payload_no_options = {
                "jsonrpc": "2.0",
                "id": "request-defaults-no-options",
                "method": "hub.invoke.request",
                "params": {
                    "appId": app_id,
                    "target": {"scope": ""},
                    "method": "asset.defaults.request",
                    "args": {"case": "no-options"}
                }
            }
            _, request_response = client.post_json(payload_no_options, timeout=30)

            worker.join(timeout=5)

            if not RpcAssertions.expect_success(result, request_response, ["invocationId", "value"]):
                return result

            value = request_response.get("result", {}).get("value", {})
            if value.get("handledBy") != "defaults":
                result.mark_failure(f"❌ 省略 options 的 request 未正确完成: {request_response}")
                return result

            item = request_result_holder.get("item")
            if not isinstance(item, dict):
                result.mark_failure(f"❌ 未捕获 request poll 条目: {request_result_holder}")
                return result

            ttl_ms = ((item.get("options") or {}).get("ttlMs"))
            if ttl_ms != 300000:
                result.mark_failure(f"❌ request 默认 ttlMs 非 300000: item={item}")
                return result

            respond_response = request_result_holder.get("respond")
            if not RpcAssertions.expect_success(result, respond_response or {}):
                return result

            missing_target_instance_id = self._instance_id("request-defaults-missing")

            payload_target_instance_no_autolaunch_option = {
                "jsonrpc": "2.0",
                "id": "request-defaults-target-instance-no-autolaunch-option",
                "method": "hub.invoke.request",
                "params": {
                    "appId": app_id,
                    "target": {
                        "scope": "",
                        "instanceId": missing_target_instance_id,
                    },
                    "method": "asset.defaults.target-instance",
                    "args": {"case": "target-instance-no-autolaunch-option"},
                    "options": {
                        "ttlMs": 2000,
                        "waitTimeoutMs": 1000,
                        "queueIfOffline": False,
                    }
                }
            }
            _, response_target_instance_no_autolaunch_option = client.post_json(
                payload_target_instance_no_autolaunch_option,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response_target_instance_no_autolaunch_option, -32010, "instance_not_found"):
                return result

            if not RpcAssertions.expect_error_data_fields(
                result,
                response_target_instance_no_autolaunch_option,
                {"reason": "target_instance_missing"},
            ):
                return result

            payload_target_instance_autolaunch_true = {
                "jsonrpc": "2.0",
                "id": "request-defaults-target-instance-autolaunch-true",
                "method": "hub.invoke.request",
                "params": {
                    "appId": app_id,
                    "target": {
                        "scope": "",
                        "instanceId": missing_target_instance_id,
                    },
                    "method": "asset.defaults.target-instance-auto",
                    "args": {"case": "target-instance-autolaunch-true"},
                    "options": {
                        "autoLaunch": True,
                        "queueIfOffline": False,
                    }
                }
            }
            _, response_target_instance_autolaunch_true = client.post_json(payload_target_instance_autolaunch_true, timeout=30)
            if not RpcAssertions.expect_error(result, response_target_instance_autolaunch_true, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([callee_instance_id])
            safe_remove(definition_path)

        return result

    def run_all_tests(self, full=False):
        return [
            self.test_request_roundtrip_success(),
            self.test_request_timeout_then_late_respond_expired(),
            self.test_request_ttl_expired_should_return_invocation_expired(),
            self.test_request_client_cancel_then_respond_can_still_succeed(),
            self.test_request_client_cancel_then_timeout_still_expires(),
            self.test_request_callee_error_should_return_invocation_failed(),
            self.test_request_rpc_disabled_should_forbidden(),
            self.test_request_defaults_should_follow_spec_when_options_omitted(),
            self.test_request_invalid_waittimeout_gt_ttl(),
            self.test_request_invalid_target_instance_with_autolaunch_true(),
            self.test_request_offline_without_queue_should_fail(),
            self.test_request_target_instance_missing_should_return_specific_reason(),
            self.test_request_overlong_target_instance_id_should_return_invalid_params(),
        ]


if __name__ == "__main__":
    test = TestInvocationRequest()
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
