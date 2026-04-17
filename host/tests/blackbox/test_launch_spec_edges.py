#!/usr/bin/env python3
"""
DevHub 启动规范边界补充测试
"""

import os
import uuid
import json
import time
import tempfile
import unittest


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcClient,
    RpcAssertions,
    TestResult,
    get_shared_test_asset_path,
    get_test_python_executable,
    safe_remove,
    write_app_definition,
)


class TestLaunchSpecEdges(unittest.TestCase):
    """Launch 规范边界测试类。"""

    @staticmethod
    def _new_app_id(suffix):
        return f"launch-edge-{suffix}-{uuid.uuid4().hex[:6]}"

    def _launch_script_path(self):
        return get_shared_test_asset_path("launch_noop.py")

    def _build_launch_config(self, dedupe_key_template=None):
        launch_config = {
            "exePath": get_test_python_executable(),
            "argsTemplate": self._launch_script_path(),
        }
        if dedupe_key_template is not None:
            launch_config["dedupeKeyTemplate"] = dedupe_key_template
        return launch_config

    def _create_definition(self, app_id, launch_config):
        return write_app_definition(
            app_id,
            rpc=True,
            events=False,
            launch=launch_config,
        )

    def test_launch_edge_001_default_dedupe_template_should_apply(self):
        """LAUNCH-EDGE-001: 未配置 dedupeKeyTemplate 时使用默认模板。"""
        result = TestResult("LAUNCH-EDGE-001 默认 dedupe 模板")
        definition_path = None

        try:
            app_id = self._new_app_id("default-dedupe")
            definition_path = self._create_definition(
                app_id,
                self._build_launch_config(),
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            first = client.launch_app(
                app_id=app_id,
                scope=None,
                wait_for_register_ms=0,
                request_id="launch-edge-001-first",
            )
            if not RpcAssertions.expect_success(result, first, ["status", "launchId"]):
                return result

            second = client.launch_app(
                app_id=app_id,
                scope=None,
                wait_for_register_ms=0,
                request_id="launch-edge-001-second",
            )
            if not RpcAssertions.expect_success(result, second, ["status", "launchId"]):
                return result

            first_status = first["result"].get("status")
            second_status = second["result"].get("status")
            first_launch_id = first["result"].get("launchId")
            second_launch_id = second["result"].get("launchId")

            if first_status != "started":
                result.mark_failure(f"❌ 首次 launch status 异常: {first}")
                return result

            if second_status != "already_running":
                result.mark_failure(f"❌ 默认 dedupe 未生效，二次 launch 非 already_running: {second}")
                return result

            if first_launch_id != second_launch_id:
                result.add_detail(
                    f"⚠️ 默认 dedupe 未复用 launchId（非阻断，Spec 未强制）: first={first_launch_id}, second={second_launch_id}"
                )

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_edge_002_explicit_dedupe_key_should_take_effect(self):
        """LAUNCH-EDGE-002: 显式 dedupeKey 相同时应去重。"""
        result = TestResult("LAUNCH-EDGE-002 显式 dedupeKey 去重")
        definition_path = None

        try:
            app_id = self._new_app_id("explicit-dedupe")
            definition_path = self._create_definition(
                app_id,
                self._build_launch_config("{appId}:{scopeOrGlobal}:{httpBaseUrl}"),
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            first = client.launch_app(
                app_id=app_id,
                scope="workspace-explicit",
                dedupe_key="edge-explicit-key",
                wait_for_register_ms=0,
                request_id="launch-edge-002-first",
            )
            if not RpcAssertions.expect_success(result, first, ["status", "launchId"]):
                return result

            second = client.launch_app(
                app_id=app_id,
                scope="workspace-explicit",
                dedupe_key="edge-explicit-key",
                wait_for_register_ms=0,
                request_id="launch-edge-002-second",
            )
            if not RpcAssertions.expect_success(result, second, ["status", "launchId"]):
                return result

            if second["result"].get("status") != "already_running":
                result.mark_failure(f"❌ 显式 dedupeKey 未生效: {second}")
                return result

            if first["result"].get("launchId") != second["result"].get("launchId"):
                result.add_detail(
                    f"⚠️ 显式 dedupeKey 未复用 launchId（非阻断，Spec 未强制）: first={first}, second={second}"
                )

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_edge_003_wait_for_register_positive_should_return_started_or_starting(self):
        """LAUNCH-EDGE-003: waitForRegisterMs>0 时状态需符合 Spec。"""
        result = TestResult("LAUNCH-EDGE-003 waitForRegisterMs 正值状态")
        definition_path = None

        try:
            app_id = self._new_app_id("wait-positive")
            definition_path = self._create_definition(
                app_id,
                self._build_launch_config(),
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            start_ts = time.monotonic()
            response = client.launch_app(
                app_id=app_id,
                scope=None,
                wait_for_register_ms=1200,
                request_id="launch-edge-003",
            )
            elapsed_ms = int((time.monotonic() - start_ts) * 1000)

            if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                return result

            status = response["result"].get("status")
            if status != "starting":
                result.mark_failure(f"❌ waitForRegisterMs>0 超时后应返回 starting: {response}")
                return result

            if elapsed_ms < 500:
                result.add_detail(f"⚠️ 等待时长较短（{elapsed_ms}ms），但返回状态已为 {status}")
            else:
                result.add_detail(f"✅ waitForRegisterMs 分支耗时 {elapsed_ms}ms，状态 {status}")

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_edge_004_dedupe_template_scope_placeholders_should_isolate(self):
        """SCOPE-LAUNCH-EDGE-004: dedupeKeyTemplate 作用域占位符应隔离。"""
        result = TestResult("SCOPE-LAUNCH-EDGE-004 dedupe 模板 scope 隔离")
        definition_path = None

        try:
            app_id = self._new_app_id("scope-template")
            definition_path = self._create_definition(
                app_id,
                self._build_launch_config("{appId}:{scope}:{scopeOrGlobal}"),
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_launch = client.launch_app(
                app_id=app_id,
                scope=None,
                wait_for_register_ms=0,
                request_id="launch-edge-004-global",
            )
            if not RpcAssertions.expect_success(result, global_launch, ["status", "launchId"]):
                return result

            scoped_launch = client.launch_app(
                app_id=app_id,
                scope="workspace-a",
                wait_for_register_ms=0,
                request_id="launch-edge-004-scoped",
            )
            if not RpcAssertions.expect_success(result, scoped_launch, ["status", "launchId"]):
                return result

            global_id = global_launch["result"].get("launchId")
            scoped_id = scoped_launch["result"].get("launchId")
            if global_id == scoped_id:
                result.add_detail(
                    f"⚠️ 不同 scope 返回相同 launchId（非阻断，Spec 未强制 launchId 全局唯一）: {global_id}"
                )

            scoped_second = client.launch_app(
                app_id=app_id,
                scope="workspace-a",
                wait_for_register_ms=0,
                request_id="launch-edge-004-scoped-second",
            )
            if not RpcAssertions.expect_success(result, scoped_second, ["status", "launchId"]):
                return result

            if scoped_second["result"].get("status") != "already_running":
                result.mark_failure(f"❌ 同 scope 二次 launch 未去重: {scoped_second}")
                return result

            if scoped_second["result"].get("launchId") != scoped_id:
                result.add_detail(
                    f"⚠️ 同 scope 二次 launch 未复用 launchId（非阻断，Spec 未强制）: first={scoped_id}, second={scoped_second}"
                )

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_launch_edge_005_undocumented_placeholder_should_remain_literal(self):
        """LAUNCH-EDGE-005: argsTemplate 中未文档化占位符必须保持字面量。"""
        result = TestResult("LAUNCH-EDGE-005 未文档化占位符保持字面量")
        definition_path = None
        script_path = None
        capture_file = None

        try:
            app_id = self._new_app_id("literal-placeholder")
            temp_dir = tempfile.mkdtemp(prefix="devhub-launch-edge-")
            script_path = os.path.join(temp_dir, "capture_args.py")
            capture_file = os.path.join(temp_dir, "captured.txt")

            with open(script_path, "w", encoding="utf-8") as handle:
                handle.write(
                    "import pathlib, sys\n"
                    "pathlib.Path(sys.argv[1]).write_text('|'.join(sys.argv[2:]), encoding='utf-8')\n"
                )

            definition_path = self._create_definition(
                app_id,
                {
                    "exePath": get_test_python_executable(),
                    "argsTemplate": f'"{script_path}" "{capture_file}" "{{dedupeKey}}" "{{appId}}"',
                },
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                scope=None,
                dedupe_key="manual-key",
                wait_for_register_ms=0,
                request_id="launch-edge-005",
            )
            if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                return result

            deadline = time.time() + 3
            while time.time() < deadline and not os.path.exists(capture_file):
                time.sleep(0.05)

            if not os.path.exists(capture_file):
                result.mark_failure("❌ 启动进程未写出参数捕获文件")
                return result

            with open(capture_file, "r", encoding="utf-8") as handle:
                captured = handle.read().strip()

            expected = f"{{dedupeKey}}|{app_id}"
            if captured != expected:
                result.mark_failure(f"❌ 未文档化占位符被替换或参数异常: expected={expected}, actual={captured}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)
            safe_remove(capture_file)
            safe_remove(script_path)
            if script_path:
                safe_remove(os.path.dirname(script_path))

        return result

    def run_all_tests(self, full=False):
        return [
            self.test_launch_edge_001_default_dedupe_template_should_apply(),
            self.test_launch_edge_002_explicit_dedupe_key_should_take_effect(),
            self.test_launch_edge_003_wait_for_register_positive_should_return_started_or_starting(),
            self.test_launch_edge_004_dedupe_template_scope_placeholders_should_isolate(),
            self.test_launch_edge_005_undocumented_placeholder_should_remain_literal(),
        ]


if __name__ == "__main__":
    test = TestLaunchSpecEdges()
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
