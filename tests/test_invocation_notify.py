#!/usr/bin/env python3
"""
DevHub M2 Invocation Notify 冒烟测试
"""

import os
import sys
import time
import uuid
import json
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestInvocationNotify(unittest.TestCase):
    """Invocation notify 测试类"""

    def _definitions_dir(self):
        if "DEVHUB_APPDEFS_DIR" in os.environ:
            definitions_dir = os.environ["DEVHUB_APPDEFS_DIR"]
        else:
            runtime_dir = DiscoveryService.get_runtime_directory()
            definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))

        os.makedirs(definitions_dir, exist_ok=True)
        return definitions_dir

    def _create_definition(self, app_id, rpc=True):
        path = os.path.join(self._definitions_dir(), f"{app_id}.json")
        payload = {
            "appId": app_id,
            "displayName": app_id,
            "capabilities": {
                "rpc": rpc,
                "events": False
            }
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        return path

    @staticmethod
    def _instance_id(prefix):
        return f"{prefix}-{uuid.uuid4().hex[:10]}"

    def test_notify_online_delivery(self):
        """M2-NOTIFY-001: 在线 notify 后可被 poll 拉取"""
        result = TestResult("M2-NOTIFY-001 notify 在线投递")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = "m2-notify-online-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            callee_instance_id = self._instance_id("notify-online")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=22001,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"trigger": "manual"},
                auto_launch=False,
                request_id="m2-notify-online",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]
            poll_response = client.poll_once(callee_instance_id, max_count=10, wait_ms=50)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response["result"]["items"]
            if len(items) < 1:
                result.mark_failure("❌ poll 未返回 invocation 项")
                return result

            found = None
            for item in items:
                if item.get("invocationId") == invocation_id:
                    found = item
                    break

            if found is None:
                result.mark_failure("❌ poll 返回中未找到目标 invocation")
                return result

            if found.get("method") != "asset.rebuild":
                result.mark_failure(f"❌ method 不匹配: {found.get('method')}")
                return result

            delivery = found.get("delivery", {})
            if delivery.get("leaseSeconds") != 30:
                result.mark_failure(f"❌ leaseSeconds 非 30: {delivery}")
                return result

            if delivery.get("attempt") != 1:
                result.mark_failure(f"❌ attempt 非 1: {delivery}")
                return result

            caller = found.get("caller", {})
            if caller.get("clientId") != client.headers.get("X-DevHub-ClientId"):
                result.mark_failure(f"❌ caller.clientId 不匹配: {caller}")
                return result

            if caller.get("clientSessionId") != client.headers.get("X-DevHub-ClientSessionId"):
                result.mark_failure(f"❌ caller.clientSessionId 不匹配: {caller}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if callee_instance_id:
                    base_url, token = DiscoveryService.get_hub_info()
                    RpcClient(base_url, token).unregister_instance(callee_instance_id)
            except Exception:
                pass

            try:
                if definition_path and os.path.exists(definition_path):
                    os.remove(definition_path)
            except Exception:
                pass

        return result

    def test_notify_pending_then_deliver(self):
        """M2-NOTIFY-002: 离线入 Pending，实例上线后可投递"""
        result = TestResult("M2-NOTIFY-002 notify 离线入队后投递")
        definition_path = None
        callee_instance_id = None

        try:
            app_id = "m2-notify-pending-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"trigger": "offline"},
                queue_if_offline=True,
                auto_launch=False,
                request_id="m2-notify-pending",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            callee_instance_id = self._instance_id("notify-pending")
            register_response = client.register_instance(
                instance_id=callee_instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=22002,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            time.sleep(0.1)
            poll_response = client.poll_once(callee_instance_id, max_count=10, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response["result"]["items"]
            found = any(item.get("invocationId") == invocation_id for item in items)
            if not found:
                result.mark_failure("❌ 实例上线后未拉取到 Pending invocation")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if callee_instance_id:
                    base_url, token = DiscoveryService.get_hub_info()
                    RpcClient(base_url, token).unregister_instance(callee_instance_id)
            except Exception:
                pass

            try:
                if definition_path and os.path.exists(definition_path):
                    os.remove(definition_path)
            except Exception:
                pass

        return result

    def test_notify_with_target_instance_and_autolaunch_true_should_fail(self):
        """指定 target.instanceId 且 autoLaunch=true 必须 invalid_params"""
        result = TestResult("notify 参数校验 target.instanceId + autoLaunch=true")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.invoke_notify(
                app_id="m2-invalid-app",
                method="asset.rebuild",
                target_instance_id="inst-target",
                auto_launch=True,
                request_id="notify-invalid-1",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_notify_with_autolaunch_true_and_queue_false_should_fail(self):
        """notify 参数校验: autoLaunch=true 且 queueIfOffline=false"""
        result = TestResult("notify 参数校验 autoLaunch=true + queueIfOffline=false")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.invoke_notify(
                app_id="m2-invalid-app",
                method="asset.rebuild",
                queue_if_offline=False,
                auto_launch=True,
                request_id="notify-invalid-2",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_notify_with_ttl_less_than_1000_should_fail(self):
        """notify 参数校验: ttlMs < 1000"""
        result = TestResult("notify 参数校验 ttlMs<1000")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.invoke_notify(
                app_id="m2-invalid-app",
                method="asset.rebuild",
                ttl_ms=999,
                auto_launch=False,
                request_id="notify-invalid-3",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_notify_target_instance_missing_should_return_specific_reason(self):
        """notify 指定 target.instanceId 且不可达时返回 target_instance_missing"""
        result = TestResult("notify target.instanceId 缺失返回 target_instance_missing")
        definition_path = None

        try:
            app_id = "m2-notify-target-missing-app"
            definition_path = self._create_definition(app_id)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                target_instance_id="inst-target-not-found",
                queue_if_offline=False,
                auto_launch=False,
                request_id="notify-target-missing",
            )

            if not RpcAssertions.expect_error(result, response, -32010, "instance_not_found"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "target_instance_missing"}):
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

    def run_all_tests(self, full=False):
        return [
            self.test_notify_online_delivery(),
            self.test_notify_pending_then_deliver(),
            self.test_notify_with_target_instance_and_autolaunch_true_should_fail(),
            self.test_notify_with_autolaunch_true_and_queue_false_should_fail(),
            self.test_notify_with_ttl_less_than_1000_should_fail(),
            self.test_notify_target_instance_missing_should_return_specific_reason(),
        ]


if __name__ == "__main__":
    test = TestInvocationNotify()
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
