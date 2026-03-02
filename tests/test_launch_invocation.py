#!/usr/bin/env python3
"""
DevHub M2 Launch + Invocation 冒烟测试
"""

import os
import sys
import uuid
import json
import unittest
from concurrent.futures import ThreadPoolExecutor

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


class TestLaunchInvocation(unittest.TestCase):
    """Launch + Invocation 测试类"""

    def _launch_script_path(self):
        return os.path.abspath(os.path.join(os.path.dirname(__file__), "assets", "launch_noop.py"))

    def _create_definition(self, app_id, include_launch=True, dedupe_key_template=None):
        launch_config = None
        if include_launch:
            launch_config = {
                "exePath": "python3",
                "argsTemplate": self._launch_script_path(),
            }
            if dedupe_key_template is not None:
                launch_config["dedupeKeyTemplate"] = dedupe_key_template

        return write_app_definition(
            app_id,
            rpc=True,
            events=False,
            launch=launch_config,
        )

    @staticmethod
    def _instance_id(prefix):
        return new_instance_id(prefix)

    def test_notify_autolaunch_then_register_poll_success(self):
        """M2-LAUNCH-001: autoLaunch 成功触发后，注册实例可拉取 invocation"""
        result = TestResult("M2-LAUNCH-001 autoLaunch 触发后投递")
        definition_path = None
        instance_id = None

        try:
            app_id = "m2-launch-notify-app"
            definition_path = self._create_definition(app_id, include_launch=True)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"from": "auto-launch"},
                queue_if_offline=True,
                auto_launch=True,
                request_id="m2-launch-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            instance_id = self._instance_id("launch-poll")
            register_response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=24001,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            poll_response = client.poll_once(instance_id, max_count=10, wait_ms=500)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result

            items = poll_response.get("result", {}).get("items", [])
            if not any(item.get("invocationId") == invocation_id for item in items):
                result.mark_failure("❌ autoLaunch 后注册实例未拉取到 invocation")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            unregister_instances([instance_id])
            safe_remove(definition_path)

        return result

    def test_launch_invalid_wait_for_register_should_fail(self):
        """launch 参数校验: waitForRegisterMs < 0"""
        result = TestResult("launch 参数校验 waitForRegisterMs < 0")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id="m2-launch-invalid",
                wait_for_register_ms=-1,
                request_id="m2-launch-invalid-wait",
            )

            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_launch_missing_config_should_fail(self):
        """launch 缺失配置: launch_config_missing"""
        result = TestResult("launch 缺失配置返回 launch_failed")
        definition_path = None

        try:
            app_id = "m2-launch-missing-config"
            definition_path = self._create_definition(app_id, include_launch=False)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                wait_for_register_ms=0,
                request_id="m2-launch-missing-config",
            )

            if not RpcAssertions.expect_error(result, response, -32020, "launch_failed"):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "launch_config_missing"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_missing_definition_should_return_app_definition_not_found(self):
        """launch 缺失定义: app_definition_not_found"""
        result = TestResult("launch 缺失定义返回 app_definition_not_found")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            missing_app_id = f"m2-launch-missing-def-{uuid.uuid4().hex[:8]}"
            response = client.launch_app(
                app_id=missing_app_id,
                wait_for_register_ms=0,
                request_id="m2-launch-missing-definition",
            )

            if not RpcAssertions.expect_error(result, response, -32014, "app_definition_not_found"):
                return result

            error_data = response.get("error", {}).get("data", {})
            if isinstance(error_data, dict) and error_data.get("appId") not in (None, missing_app_id):
                result.mark_failure(f"❌ app_definition_not_found 返回 appId 不匹配: {response}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def _run_launch_dedupe_concurrent_case(self, request_count, case_name):
        result = TestResult(case_name)
        definition_path = None

        try:
            app_id = f"m2-launch-dedupe-app-{request_count}"
            definition_path = self._create_definition(
                app_id,
                include_launch=True,
                dedupe_key_template="{appId}:{scopeOrGlobal}",
            )

            base_url, token = DiscoveryService.get_hub_info()

            def send_launch(index):
                client = RpcClient(base_url, token)
                return client.launch_app(
                    app_id=app_id,
                    wait_for_register_ms=0,
                    request_id=f"m2-launch-dedupe-{request_count}-{index}",
                )

            with ThreadPoolExecutor(max_workers=request_count) as executor:
                responses = list(executor.map(send_launch, range(request_count)))

            if len(responses) != request_count:
                result.mark_failure(f"❌ 并发请求返回数量异常: {len(responses)}")
                return result

            statuses = []
            launch_ids = []

            for response in responses:
                if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                    return result

                status = response["result"].get("status")
                launch_id = response["result"].get("launchId")
                statuses.append(status)
                launch_ids.append(launch_id)

            for index, (status, launch_id) in enumerate(zip(statuses, launch_ids)):
                result.add_detail(f"并发请求[{index}] status={status}, launchId={launch_id}")

            started_count = sum(1 for status in statuses if status in ("started", "starting"))
            already_running_count = sum(1 for status in statuses if status == "already_running")

            if started_count < 1:
                result.mark_failure(f"❌ 未出现 started/starting，statuses={statuses}")
                return result

            if already_running_count != request_count - 1:
                result.mark_failure(
                    f"❌ already_running 数量不符合预期: got={already_running_count}, expected={request_count - 1}, statuses={statuses}")
                return result

            if len(set(launch_ids)) != 1:
                result.mark_failure(f"❌ launchId 未复用: {launch_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_dedupe_concurrent_should_return_already_running_lightweight(self):
        """M2-LAUNCH-002-LITE: default 轻量并发 dedupe"""
        return self._run_launch_dedupe_concurrent_case(
            request_count=3,
            case_name="M2-LAUNCH-002-LITE dedupe 并发去重（default 轻量）",
        )

    def test_launch_dedupe_concurrent_should_return_already_running(self):
        """M2-LAUNCH-002: dedupe 窗口并发去重（full）"""
        return self._run_launch_dedupe_concurrent_case(
            request_count=8,
            case_name="M2-LAUNCH-002 dedupe 窗口并发去重（full）",
        )

    def test_m3_scope_011_launch_dedupe_should_isolate_by_scope(self):
        """M3-SCOPE-011: launch dedupe 在不同 scope 间隔离"""
        result = TestResult("M3-SCOPE-011 launch dedupe scope 隔离")
        definition_path = None

        try:
            app_id = f"m3-launch-scope-dedupe-{uuid.uuid4().hex[:8]}"
            definition_path = self._create_definition(
                app_id,
                include_launch=True,
                dedupe_key_template="{appId}:{scopeOrGlobal}",
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            def launch_with_scope(scope, request_id):
                scoped_client = RpcClient(base_url, token)
                return scoped_client.launch_app(
                    app_id=app_id,
                    scope=scope,
                    wait_for_register_ms=0,
                    request_id=request_id,
                )

            with ThreadPoolExecutor(max_workers=2) as executor:
                scope_a_future = executor.submit(launch_with_scope, "workspace-A", "m3-scope-011-launch-a")
                scope_b_future = executor.submit(launch_with_scope, "workspace-B", "m3-scope-011-launch-b")
                scope_a_response = scope_a_future.result()
                scope_b_response = scope_b_future.result()

            if not RpcAssertions.expect_success(result, scope_a_response, ["status", "launchId"]):
                return result

            if not RpcAssertions.expect_success(result, scope_b_response, ["status", "launchId"]):
                return result

            status_a = scope_a_response["result"].get("status")
            status_b = scope_b_response["result"].get("status")
            launch_id_a = scope_a_response["result"].get("launchId")
            launch_id_b = scope_b_response["result"].get("launchId")

            if status_a == "already_running" or status_b == "already_running":
                result.mark_failure(
                    f"❌ 不同 scope launch 发生错误去重: status_a={status_a}, status_b={status_b}")
                return result

            if launch_id_a == launch_id_b:
                result.mark_failure(f"❌ 不同 scope launchId 不应复用: {launch_id_a}")
                return result

            same_scope_second = client.launch_app(
                app_id=app_id,
                scope="workspace-A",
                wait_for_register_ms=0,
                request_id="m3-scope-011-launch-a-second",
            )
            if not RpcAssertions.expect_success(result, same_scope_second, ["status", "launchId"]):
                return result

            second_status = same_scope_second["result"].get("status")
            second_launch_id = same_scope_second["result"].get("launchId")
            if second_status != "already_running":
                result.mark_failure(f"❌ 同 scope 二次 launch 未返回 already_running: {same_scope_second}")
                return result

            if second_launch_id != launch_id_a:
                result.mark_failure(
                    f"❌ 同 scope 二次 launch 未复用 launchId: first={launch_id_a}, second={second_launch_id}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def run_all_tests(self, full=False, fast=False):
        results = [
            self.test_notify_autolaunch_then_register_poll_success(),
            self.test_launch_invalid_wait_for_register_should_fail(),
            self.test_launch_missing_config_should_fail(),
            self.test_launch_missing_definition_should_return_app_definition_not_found(),
            self.test_m3_scope_011_launch_dedupe_should_isolate_by_scope(),
        ]

        if full:
            results.append(self.test_launch_dedupe_concurrent_should_return_already_running())
        elif not fast:
            results.append(self.test_launch_dedupe_concurrent_should_return_already_running_lightweight())

        return results


if __name__ == "__main__":
    test = TestLaunchInvocation()
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
