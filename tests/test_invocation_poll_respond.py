#!/usr/bin/env python3
"""
DevHub M2 Invocation Poll/Respond 冒烟测试
"""

import os
import sys
import time
import uuid
import json
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import (
    DiscoveryService,
    RpcClient,
    RpcAssertions,
    TestResult,
    new_instance_id,
    safe_remove,
    unregister_instances,
    write_app_definition,
)


class TestInvocationPollRespond(unittest.TestCase):
    """Invocation poll/respond 测试类"""

    def _create_definition(self, app_id):
        return write_app_definition(app_id, rpc=True, events=False)

    @staticmethod
    def _instance_id(prefix):
        return new_instance_id(prefix)

    def test_poll_unregistered_instance(self):
        """M2-POLL-001: 未注册实例 poll 返回 instance_not_found"""
        result = TestResult("M2-POLL-001 未注册实例 poll")

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
            app_id = "m2-poll-disabled-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("poll-disabled")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
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
            safe_remove(definition_path)

        return result

    def test_respond_with_respond_disabled_instance_should_fail(self):
        """respond 能力门禁: invoke.respond=false"""
        result = TestResult("respond 能力门禁 invoke.respond=false")
        definition_path = None
        instance_id = None

        try:
            app_id = "m2-respond-disabled-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("respond-disabled")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=False,
                pid=23006,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            respond_response = client.respond_value(instance_id, "invk-not-exists", {"ok": True})
            if not RpcAssertions.expect_error(result, respond_response, -32002, "forbidden"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, respond_response, {"reason": "respond_not_enabled"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            safe_remove(definition_path)

        return result

    def test_respond_duplicate_should_conflict(self):
        """M2-RESP-001: 同一 invocation 重复 respond 返回冲突"""
        result = TestResult("M2-RESP-001 重复 respond 返回冲突")
        definition_path = None
        instance_id = None

        try:
            app_id = "m2-poll-respond-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("poll-resp")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
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

            first_respond = client.respond_value(instance_id, invocation_id, {"ok": True})
            if not RpcAssertions.expect_success(result, first_respond):
                return result

            second_respond = client.respond_value(instance_id, invocation_id, {"ok": True})
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

            safe_remove(definition_path)

        return result

    def test_respond_by_non_lease_holder_should_conflict(self):
        """M2-RESP-001: 非 lease holder respond 返回冲突"""
        result = TestResult("M2-RESP-001 越权 respond 返回冲突")
        definition_path = None
        instance_a = None
        instance_b = None

        try:
            app_id = "m2-poll-respond-non-holder-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_a = self._instance_id("poll-holder-a")
            instance_b = self._instance_id("poll-holder-b")

            register_a = client.register_instance(
                instance_id=instance_a,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=23011,
            )
            if not RpcAssertions.expect_success(result, register_a, ["instance"]):
                return result

            register_b = client.register_instance(
                instance_id=instance_b,
                app_id=app_id,
                scope=None,
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
            if not any(item.get("invocationId") == invocation_id for item in items):
                result.mark_failure("❌ lease holder poll 未拉取到 invocation")
                return result

            non_holder_respond = client.respond_value(instance_b, invocation_id, {"ok": True})
            if not RpcAssertions.expect_error(result, non_holder_respond, -32030, "delivery_conflict"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_a, instance_b])
            safe_remove(definition_path)

        return result

    def test_respond_value_error_xor_validation_should_invalid_params(self):
        """M2-RESP-002: respond 的 value/error 必须二选一。"""
        result = TestResult("M2-RESP-002 respond value/error XOR 校验")
        definition_path = None
        instance_id = None

        try:
            app_id = "m2-respond-xor-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._instance_id("respond-xor")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
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
            response_both = client.call("hub.invoke.respond", {
                "instanceId": instance_id,
                "invocationId": invocation_id_1,
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
            response_none = client.call("hub.invoke.respond", {
                "instanceId": instance_id,
                "invocationId": invocation_id_2,
            }, request_id="respond-xor-none")
            if not RpcAssertions.expect_error(result, response_none, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            safe_remove(definition_path)

        return result

    def test_lease_expired_should_redeliver_with_attempt_incremented(self):
        """M2-LEASE-001: lease 到期后重投递且 attempt 递增（full-only）"""
        result = TestResult("M2-LEASE-001 lease 到期重投递 attempt 递增")
        definition_path = None
        instance_a = None
        instance_b = None

        try:
            app_id = "m2-lease-redelivery-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_a = self._instance_id("lease-redelivery-a")
            instance_b = self._instance_id("lease-redelivery-b")

            register_a = client.register_instance(
                instance_id=instance_a,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=23021,
            )
            if not RpcAssertions.expect_success(result, register_a, ["instance"]):
                return result

            register_b = client.register_instance(
                instance_id=instance_b,
                app_id=app_id,
                scope=None,
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

            time.sleep(31.0)

            second_poll = client.poll_once(instance_b, max_count=10, wait_ms=500)
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
            safe_remove(definition_path)

        return result

    def run_all_tests(self, full=False):
        results = [
            self.test_poll_unregistered_instance(),
            self.test_poll_with_invalid_max_count_should_fail(),
            self.test_poll_with_poll_disabled_instance_should_fail(),
            self.test_respond_with_respond_disabled_instance_should_fail(),
            self.test_respond_duplicate_should_conflict(),
            self.test_respond_by_non_lease_holder_should_conflict(),
            self.test_respond_value_error_xor_validation_should_invalid_params(),
        ]

        if full:
            results.append(self.test_lease_expired_should_redeliver_with_attempt_incremented())

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
