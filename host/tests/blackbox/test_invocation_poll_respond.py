#!/usr/bin/env python3
"""
DevHub 调用轮询与响应冒烟测试
"""

import os
import uuid
import json
import unittest


from tests.blackbox.test_base import (
    call_with_long_wait_status,
    DiscoveryService,
    delete_definitions,
    RpcClient,
    RpcAssertions,
    TestResult,
    new_instance_id,
    resolve_instance_session_token,
    unregister_instances,
    upsert_app_definition,
)


class TestInvocationPollRespond(unittest.TestCase):
    """Invocation poll/respond 测试类"""

    def _create_definition(self, app_id):
        return upsert_app_definition(app_id, rpc=True, events=False)

    @staticmethod
    def _new_app_id(prefix):
        return f"{prefix}-{uuid.uuid4().hex[:8]}"

    @staticmethod
    def _instance_id(prefix):
        return new_instance_id(prefix)

    def test_poll_unregistered_instance(self):
        """POLL-001: 未注册实例 poll 返回 instance_not_found"""
        result = TestResult("POLL-001 未注册实例 poll")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.poll_once(self._instance_id("unknown"), max_count=10, wait_ms=10)

            if not RpcAssertions.expect_error(result, response, -32010, "instance_not_found"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_poll_with_invalid_max_count_should_fail(self):
        """poll 参数边界: maxCount=0 或 maxCount>100"""
        result = TestResult("poll 参数边界 maxCount 范围")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response_zero = client.poll_once(self._instance_id("invalid-max-0"), max_count=0, wait_ms=0)
            if not RpcAssertions.expect_error(result, response_zero, -32602, "invalid_params"):
                return result

            response_over = client.poll_once(self._instance_id("invalid-max-101"), max_count=101, wait_ms=0)
            if not RpcAssertions.expect_error(result, response_over, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_poll_with_poll_disabled_instance_should_fail(self):
        """poll 能力门禁: invoke.poll=false"""
        result = TestResult("poll 能力门禁 invoke.poll=false")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("poll-disabled-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("poll-disabled")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=False,
                respond=True,
                pid=23005,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            poll_response = client.poll_once(instance_id, max_count=1, wait_ms=0)
            if not RpcAssertions.expect_error(result, poll_response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, poll_response, {"reason": "poll_not_enabled"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_respond_with_respond_disabled_instance_should_fail(self):
        """respond 能力门禁: invoke.respond=false"""
        result = TestResult("respond 能力门禁 invoke.respond=false")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("respond-disabled-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("respond-disabled")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=False,
                pid=23006,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            respond_response = client.respond_value(instance_id, "invk-not-exists", {"ok": True}, lease_token="missing-lease-token")
            if not RpcAssertions.expect_error(result, respond_response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, respond_response, {"reason": "respond_not_enabled"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_poll_with_wrong_instance_session_token_should_fail(self):
        """POLL-OWN-001: 错误 instanceSessionToken 的 poll 必须被拒绝"""
        result = TestResult("POLL-OWN-001 错误 instanceSessionToken 的 poll 被拒绝")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("poll-token-guard-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("poll-token-guard")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23007,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance", "instanceSessionToken"]):
                return result

            poll_response = client.poll_once(
                instance_id,
                max_count=1,
                wait_ms=0,
                instance_session_token="wrong-instance-session-token",
            )
            if not RpcAssertions.expect_error(result, poll_response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, poll_response, {"reason": "instance_session_token_mismatch"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_respond_with_wrong_instance_session_token_should_fail(self):
        """RESP-OWN-001: 错误 instanceSessionToken 的 respond 必须被拒绝"""
        result = TestResult("RESP-OWN-001 错误 instanceSessionToken 的 respond 被拒绝")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("respond-token-guard-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("respond-token-guard")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23008,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance", "instanceSessionToken"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.refresh",
                args={"mode": "token-guard"},
                auto_launch=False,
                request_id="respond-token-guard-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            poll_response = client.poll_once(instance_id, max_count=1, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if len(items) != 1:
                result.mark_failure(f"❌ 未拿到待 respond 的 invocation: {poll_response}")
                return result

            invocation_id = items[0].get("invocationId")
            lease_token = items[0].get("delivery", {}).get("leaseToken")
            respond_response = client.respond_value(
                instance_id,
                invocation_id,
                {"ok": True},
                instance_session_token="wrong-instance-session-token",
                lease_token=lease_token,
            )
            if not RpcAssertions.expect_error(result, respond_response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, respond_response, {"reason": "instance_session_token_mismatch"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_respond_duplicate_should_conflict(self):
        """RESP-001: 同一 invocation 重复 respond 返回冲突"""
        result = TestResult("RESP-001 重复 respond 返回冲突")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("poll-respond-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("poll-resp")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23001,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.refresh",
                args={"k": "v"},
                auto_launch=False,
                request_id="poll-resp-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            poll_response = client.poll_once(instance_id, max_count=10, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result
            items = poll_response.get("result", {}).get("items", [])
            lease_token = next((item.get("delivery", {}).get("leaseToken") for item in items if item.get("invocationId") == invocation_id), None)

            first_respond = client.respond_value(instance_id, invocation_id, {"ok": True}, lease_token=lease_token)
            if not RpcAssertions.expect_success(result, first_respond):
                return result

            second_respond = client.respond_value(instance_id, invocation_id, {"ok": True}, lease_token=lease_token)
            if not RpcAssertions.expect_error(result, second_respond, -32030, "delivery_conflict"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if instance_id:
                    base_url, token = DiscoveryService.get_hub_info()
                    RpcClient(base_url, token).unregister_instance(instance_id)
            except Exception:
                pass

            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_respond_by_non_lease_holder_should_conflict(self):
        """RESP-001: 非 lease holder respond 返回冲突"""
        result = TestResult("RESP-001 越权 respond 返回冲突")
        definition_path = None
        instance_a = None
        instance_b = None

        try:
            app_id = self._new_app_id("poll-respond-non-holder-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_a = self._instance_id("poll-holder-a")
            instance_b = self._instance_id("poll-holder-b")

            register_a = client.register_instance(
                instance_id=instance_a,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23011,
            )
            if not RpcAssertions.expect_success(result, register_a, ["instance"]):
                return result

            register_b = client.register_instance(
                instance_id=instance_b,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23012,
            )
            if not RpcAssertions.expect_success(result, register_b, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.refresh",
                args={"k": "v"},
                auto_launch=False,
                request_id="poll-non-holder-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]
            poll_response = client.poll_once(instance_a, max_count=10, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            holder_item = next((item for item in items if item.get("invocationId") == invocation_id), None)
            if holder_item is None:
                result.mark_failure("❌ lease holder poll 未拉取到 invocation")
                return result

            lease_token = holder_item.get("delivery", {}).get("leaseToken")
            non_holder_respond = client.respond_value(instance_b, invocation_id, {"ok": True}, lease_token=lease_token)
            if not RpcAssertions.expect_error(result, non_holder_respond, -32030, "delivery_conflict"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_a, instance_b])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_respond_value_error_xor_validation_should_invalid_params(self):
        """RESP-002: respond 的 value/error 必须二选一。"""
        result = TestResult("RESP-002 respond value/error XOR 校验")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("respond-xor-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("respond-xor")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23031,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            # 子场景1：value 与 error 同时存在
            notify_response_1 = client.invoke_notify(
                app_id=app_id,
                method="asset.xor1",
                args={},
                auto_launch=False,
                request_id="respond-xor-notify-1",
            )
            if not RpcAssertions.expect_success(result, notify_response_1, ["invocationId"]):
                return result

            poll_response_1 = client.poll_once(instance_id, max_count=1, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response_1, ["items"]):
                return result

            items_1 = poll_response_1.get("result", {}).get("items", [])
            if len(items_1) != 1:
                result.mark_failure(f"❌ XOR 子场景1未取到 invocation: {poll_response_1}")
                return result

            invocation_id_1 = items_1[0].get("invocationId")
            lease_token_1 = items_1[0].get("delivery", {}).get("leaseToken")
            if not isinstance(lease_token_1, str) or not lease_token_1:
                result.mark_failure(f"❌ XOR 子场景1缺少有效 leaseToken: {items_1[0]}")
                return result

            response_both = client.call("hub.invoke.respond", {
                "instanceId": instance_id,
                "instanceSessionToken": resolve_instance_session_token(instance_id),
                "invocationId": invocation_id_1,
                "leaseToken": lease_token_1,
                "value": {"ok": True},
                "error": {"code": 1001, "message": "app_error"}
            }, request_id="respond-xor-both")
            if not RpcAssertions.expect_error(result, response_both, -32602, "invalid_params"):
                return result

            # 子场景2：value 与 error 同时缺失
            notify_response_2 = client.invoke_notify(
                app_id=app_id,
                method="asset.xor2",
                args={},
                auto_launch=False,
                request_id="respond-xor-notify-2",
            )
            if not RpcAssertions.expect_success(result, notify_response_2, ["invocationId"]):
                return result

            poll_response_2 = client.poll_once(instance_id, max_count=1, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response_2, ["items"]):
                return result

            items_2 = poll_response_2.get("result", {}).get("items", [])
            if len(items_2) != 1:
                result.mark_failure(f"❌ XOR 子场景2未取到 invocation: {poll_response_2}")
                return result

            invocation_id_2 = items_2[0].get("invocationId")
            lease_token_2 = items_2[0].get("delivery", {}).get("leaseToken")
            if not isinstance(lease_token_2, str) or not lease_token_2:
                result.mark_failure(f"❌ XOR 子场景2缺少有效 leaseToken: {items_2[0]}")
                return result

            response_none = client.call("hub.invoke.respond", {
                "instanceId": instance_id,
                "instanceSessionToken": resolve_instance_session_token(instance_id),
                "invocationId": invocation_id_2,
                "leaseToken": lease_token_2,
            }, request_id="respond-xor-none")
            if not RpcAssertions.expect_error(result, response_none, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_lease_expired_should_redeliver_with_attempt_incremented_lightweight(self):
        """LEASE-001-LITE: default 轻量 lease 到期重投递"""
        result = TestResult("LEASE-001-LITE lease 到期重投递（default 轻量）")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("lease-redelivery-lite-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("lease-redelivery-lite")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23020,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"mode": "lease-redelivery-lite"},
                auto_launch=False,
                request_id="lease-redelivery-lite-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            first_poll = client.poll_once(instance_id, max_count=1, wait_ms=100)
            if not RpcAssertions.expect_success(result, first_poll, ["items"]):
                return result

            first_items = first_poll.get("result", {}).get("items", [])
            if len(first_items) != 1 or first_items[0].get("invocationId") != invocation_id:
                result.mark_failure(f"❌ 首次 poll 未拿到目标 invocation: {first_poll}")
                return result

            first_item = first_items[0]
            first_attempt = first_item.get("delivery", {}).get("attempt")
            if first_attempt != 1:
                result.mark_failure(f"❌ 首次 attempt 不是 1: {first_item.get('delivery')}")
                return result

            lease_seconds = first_item.get("delivery", {}).get("leaseSeconds")
            if not isinstance(lease_seconds, int) or lease_seconds < 1:
                result.mark_failure(f"❌ leaseSeconds 非法: {first_item.get('delivery')}")
                return result

            second_wait_ms = lease_seconds * 1000 + 1500
            second_poll = call_with_long_wait_status(
                "等待 lease 到期后重投递到同一实例",
                second_wait_ms / 1000,
                lambda: client.poll_once(instance_id, max_count=1, wait_ms=second_wait_ms),
            )
            if not RpcAssertions.expect_success(result, second_poll, ["items"]):
                return result

            second_items = second_poll.get("result", {}).get("items", [])
            if len(second_items) != 1 or second_items[0].get("invocationId") != invocation_id:
                result.mark_failure("❌ lease 到期后未重投递到同一实例")
                return result

            second_attempt = second_items[0].get("delivery", {}).get("attempt")
            if not isinstance(second_attempt, int) or second_attempt < 2:
                result.mark_failure(f"❌ 重投递 attempt 未递增: {second_items[0].get('delivery')}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_lease_expired_should_redeliver_with_attempt_incremented(self):
        """LEASE-001: lease 到期后重投递且 attempt 递增（full）"""
        result = TestResult("LEASE-001 lease 到期重投递 attempt 递增（full）")
        definition_path = None
        instance_a = None
        instance_b = None

        try:
            app_id = self._new_app_id("lease-redelivery-app")
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_a = self._instance_id("lease-redelivery-a")
            instance_b = self._instance_id("lease-redelivery-b")

            register_a = client.register_instance(
                instance_id=instance_a,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23021,
            )
            if not RpcAssertions.expect_success(result, register_a, ["instance"]):
                return result

            register_b = client.register_instance(
                instance_id=instance_b,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=23022,
            )
            if not RpcAssertions.expect_success(result, register_b, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"mode": "lease-redelivery"},
                auto_launch=False,
                request_id="lease-redelivery-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            first_poll = client.poll_once(instance_a, max_count=10, wait_ms=100)
            if not RpcAssertions.expect_success(result, first_poll, ["items"]):
                return result

            first_item = None
            for item in first_poll.get("result", {}).get("items", []):
                if item.get("invocationId") == invocation_id:
                    first_item = item
                    break

            if not first_item:
                result.mark_failure("❌ 首次 poll 未拿到目标 invocation")
                return result

            first_attempt = first_item.get("delivery", {}).get("attempt")
            if first_attempt != 1:
                result.mark_failure(f"❌ 首次 attempt 不是 1: {first_item.get('delivery')}")
                return result

            lease_seconds = first_item.get("delivery", {}).get("leaseSeconds")
            if not isinstance(lease_seconds, int) or lease_seconds < 1:
                result.mark_failure(f"❌ leaseSeconds 非法: {first_item.get('delivery')}")
                return result

            second_wait_ms = lease_seconds * 1000 + 1500
            second_poll = call_with_long_wait_status(
                "等待 lease 到期后重投递到第二实例",
                second_wait_ms / 1000,
                lambda: client.poll_once(instance_b, max_count=10, wait_ms=second_wait_ms),
            )
            if not RpcAssertions.expect_success(result, second_poll, ["items"]):
                return result

            second_item = None
            for item in second_poll.get("result", {}).get("items", []):
                if item.get("invocationId") == invocation_id:
                    second_item = item
                    break

            if not second_item:
                result.mark_failure("❌ lease 到期后未重投递到第二实例")
                return result

            second_attempt = second_item.get("delivery", {}).get("attempt")
            if not isinstance(second_attempt, int) or second_attempt < 2:
                result.mark_failure(f"❌ 重投递 attempt 未递增: {second_item.get('delivery')}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_a, instance_b])
            delete_definitions([definition_path] if definition_path else [])

        return result

    def run_all_tests(self, full=False, fast=False):
        results = [
            self.test_poll_unregistered_instance(),
            self.test_poll_with_invalid_max_count_should_fail(),
            self.test_poll_with_poll_disabled_instance_should_fail(),
            self.test_respond_with_respond_disabled_instance_should_fail(),
            self.test_poll_with_wrong_instance_session_token_should_fail(),
            self.test_respond_with_wrong_instance_session_token_should_fail(),
            self.test_respond_duplicate_should_conflict(),
            self.test_respond_by_non_lease_holder_should_conflict(),
            self.test_respond_value_error_xor_validation_should_invalid_params(),
        ]

        if full:
            results.append(self.test_lease_expired_should_redeliver_with_attempt_incremented())
        elif not fast:
            results.append(self.test_lease_expired_should_redeliver_with_attempt_incremented_lightweight())

        return results


if __name__ == "__main__":
    test = TestInvocationPollRespond()
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
