#!/usr/bin/env python3
"""
DevHub M2/M3 Invocation poll/respond 规范边界补充测试
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
    write_definition,
)


class TestInvokePollRespondEdges(unittest.TestCase):
    """Invocation poll/respond 规范边界测试。"""

    def _create_definition(self, app_id):
        payload = {
            "appId": app_id,
            "displayName": app_id,
            "capabilities": {
                "rpc": True,
                "events": False,
            },
        }
        return write_definition(app_id, payload)

    @staticmethod
    def _new_app_id(suffix):
        return f"m2-invoke-edge-{suffix}-{uuid.uuid4().hex[:6]}"

    @staticmethod
    def _new_instance_id(suffix):
        return new_instance_id(f"m2-invoke-edge-{suffix}")

    def test_invoke_edge_001_poll_long_wait_semantics_and_server_time(self):
        """M2-INVOKE-EDGE-001: poll 无可用项时应长轮询并返回 serverTimeUtc。"""
        result = TestResult("M2-INVOKE-EDGE-001 poll 长轮询与 serverTimeUtc")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("poll-wait")
            definition_path = self._create_definition(app_id)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._new_instance_id("poll-wait")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=6401,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            wait_ms = 1200
            start_ts = time.monotonic()
            poll_response = client.poll_once(instance_id, max_count=10, wait_ms=wait_ms)
            elapsed_ms = int((time.monotonic() - start_ts) * 1000)

            if not RpcAssertions.expect_success(result, poll_response, ["items", "serverTimeUtc"]):
                return result

            items = poll_response.get("result", {}).get("items")
            if not isinstance(items, list) or len(items) != 0:
                result.mark_failure(f"❌ 长轮询空队列应返回空数组: {poll_response}")
                return result

            if elapsed_ms < 900:
                result.mark_failure(f"❌ 长轮询等待时长过短: {elapsed_ms}ms (waitMs={wait_ms})")
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

    def test_invoke_edge_002_respond_success_should_refresh_last_seen(self):
        """M2-INVOKE-EDGE-002: respond 成功后应更新实例 lastSeenUtc。"""
        result = TestResult("M2-INVOKE-EDGE-002 respond 刷新 lastSeenUtc")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("respond-lastseen")
            definition_path = self._create_definition(app_id)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._new_instance_id("respond-lastseen")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=6402,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            before_last_seen = register_response["result"]["instance"].get("lastSeenUtc")
            if not isinstance(before_last_seen, str):
                result.mark_failure(f"❌ 注册返回缺少 lastSeenUtc: {register_response}")
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.respond.lastseen",
                args={"case": "respond-lastseen"},
                auto_launch=False,
                request_id="invoke-edge-002-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            poll_response = client.poll_once(instance_id, max_count=1, wait_ms=200)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if len(items) != 1:
                result.mark_failure(f"❌ poll 未取到唯一 invocation: {poll_response}")
                return result

            invocation_id = items[0].get("invocationId")
            if not invocation_id:
                result.mark_failure(f"❌ poll item 缺少 invocationId: {items[0]}")
                return result

            time.sleep(1)
            respond_response = client.respond_value(instance_id, invocation_id, {"ok": True})
            if not RpcAssertions.expect_success(result, respond_response):
                return result

            list_response = client.call(
                "hub.apps.listInstances",
                {"appId": app_id, "includeAllScopes": True, "includeOffline": True},
                request_id="invoke-edge-002-list",
            )
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            instances = list_response.get("result", {}).get("instances", [])
            instance = next((item for item in instances if item.get("instanceId") == instance_id), None)
            if instance is None:
                result.mark_failure(f"❌ listInstances 未返回目标实例: {instances}")
                return result

            after_last_seen = instance.get("lastSeenUtc")
            if not isinstance(after_last_seen, str):
                result.mark_failure(f"❌ listInstances 缺少 lastSeenUtc: {instance}")
                return result

            if after_last_seen <= before_last_seen:
                result.mark_failure(f"❌ respond 未刷新 lastSeenUtc: before={before_last_seen}, after={after_last_seen}")
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

    def test_invoke_edge_003_respond_after_unregister_should_instance_not_found(self):
        """M2-INVOKE-EDGE-003: unregister 后 respond 必须 instance_not_found。"""
        result = TestResult("M2-INVOKE-EDGE-003 unregister 后 respond instance_not_found")
        definition_path = None
        instance_id = None
        respond_instance_id = None

        try:
            app_id = self._new_app_id("respond-after-unregister")
            definition_path = self._create_definition(app_id)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._new_instance_id("respond-after-unregister")
            respond_instance_id = instance_id
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=6403,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.unregister.respond",
                args={"case": "respond-after-unregister"},
                auto_launch=False,
                request_id="invoke-edge-003-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            poll_response = client.poll_once(instance_id, max_count=1, wait_ms=200)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if len(items) != 1:
                result.mark_failure(f"❌ poll 未取到 invocation: {poll_response}")
                return result

            invocation_id = items[0].get("invocationId")
            if not invocation_id:
                result.mark_failure(f"❌ poll item 缺少 invocationId: {items[0]}")
                return result

            unregister_response = client.unregister_instance(instance_id)
            if not RpcAssertions.expect_success(result, unregister_response):
                return result

            instance_id = None

            respond_after_unregister = client.respond_value(
                respond_instance_id,
                invocation_id,
                {"ok": True},
            )
            if not RpcAssertions.expect_error(result, respond_after_unregister, -32010, "instance_not_found"):
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

    def test_invoke_edge_004_poll_success_should_refresh_last_seen(self):
        """M2-INVOKE-EDGE-004: poll 成功后应更新实例 lastSeenUtc。"""
        result = TestResult("M2-INVOKE-EDGE-004 poll 刷新 lastSeenUtc")
        definition_path = None
        instance_id = None

        try:
            app_id = self._new_app_id("poll-lastseen")
            definition_path = self._create_definition(app_id)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            instance_id = self._new_instance_id("poll-lastseen")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=6404,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            before_last_seen = register_response["result"]["instance"].get("lastSeenUtc")
            if not isinstance(before_last_seen, str):
                result.mark_failure(f"❌ 注册返回缺少 lastSeenUtc: {register_response}")
                return result

            time.sleep(1)
            poll_response = client.poll_once(instance_id, max_count=1, wait_ms=100)
            if not RpcAssertions.expect_success(result, poll_response, ["items", "serverTimeUtc"]):
                return result

            list_response = client.call(
                "hub.apps.listInstances",
                {"appId": app_id, "includeAllScopes": True, "includeOffline": True},
                request_id="invoke-edge-004-list",
            )
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            instances = list_response.get("result", {}).get("instances", [])
            instance = next((item for item in instances if item.get("instanceId") == instance_id), None)
            if instance is None:
                result.mark_failure(f"❌ listInstances 未返回目标实例: {instances}")
                return result

            after_last_seen = instance.get("lastSeenUtc")
            if not isinstance(after_last_seen, str):
                result.mark_failure(f"❌ listInstances 缺少 lastSeenUtc: {instance}")
                return result

            if after_last_seen <= before_last_seen:
                result.mark_failure(f"❌ poll 未刷新 lastSeenUtc: before={before_last_seen}, after={after_last_seen}")
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

    def run_all_tests(self, full=False):
        results = [
            self.test_invoke_edge_004_poll_success_should_refresh_last_seen(),
            self.test_invoke_edge_002_respond_success_should_refresh_last_seen(),
            self.test_invoke_edge_003_respond_after_unregister_should_instance_not_found(),
        ]

        if full:
            results.append(self.test_invoke_edge_001_poll_long_wait_semantics_and_server_time())

        return results


if __name__ == "__main__":
    test = TestInvokePollRespondEdges()
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
