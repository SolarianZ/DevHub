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
    TEST_HUB_ENV_JSON_ENV_VAR,
    PENDING_WAIT_STATUS,
    delete_definitions,
    RpcClient,
    RpcAssertions,
    TestResult,
    get_shared_test_asset_path,
    get_test_python_executable,
    safe_remove,
    start_isolated_hub_process,
    temporary_env_var,
    poll_until_deadline_with_long_wait_status,
    upsert_app_definition,
)


class TestLaunchSpecEdges(unittest.TestCase):
    """Launch 规范边界测试类。"""

    @staticmethod
    def _new_app_id(suffix):
        return f"launch-edge-{suffix}-{uuid.uuid4().hex[:6]}"

    def _launch_script_path(self):
        return get_shared_test_asset_path("launch_noop.py")

    def _capture_argv_script_path(self):
        return get_shared_test_asset_path("launch_capture_argv.py")

    def _build_launch_config(self, dedupe_key_template=None):
        launch_config = {
            "exePath": get_test_python_executable(),
            "args": [self._launch_script_path()],
        }
        if dedupe_key_template is not None:
            launch_config["dedupeKeyTemplate"] = dedupe_key_template
        return launch_config

    def _create_definition(self, app_id, launch_config, scope=""):
        return upsert_app_definition(
            app_id,
            scope=scope,
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
                scope="",
                wait_for_register_ms=0,
                request_id="launch-edge-001-first",
            )
            if not RpcAssertions.expect_success(result, first, ["status", "launchId"]):
                return result

            second = client.launch_app(
                app_id=app_id,
                scope="",
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
            delete_definitions([definition_path] if definition_path else [])

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
                scope="workspace-explicit",
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
            delete_definitions([definition_path] if definition_path else [])

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
                scope="",
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
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_launch_edge_003b_wait_budget_should_return_register_timeout_at_effective_deadline(self):
        """LAUNCH-EDGE-003B: 到达有效注册截止时间时返回 launch_register_timeout。"""
        result = TestResult("LAUNCH-EDGE-003B waitForRegisterMs 到达有效注册截止返回 launch_register_timeout")
        definition_path = None
        temp_root = None
        process = None
        log_file = None

        try:
            temp_root = tempfile.mkdtemp(prefix="devhub-launch-wait-budget-")
            data_dir = os.path.join(temp_root, "data")
            log_path = os.path.join(temp_root, "host.log")

            overrides = json.dumps({
                "DEVHUB_LAUNCH_REGISTER_TIMEOUT_SECONDS": "1"
            }, ensure_ascii=False)

            with temporary_env_var(TEST_HUB_ENV_JSON_ENV_VAR, overrides):
                with open(log_path, "w+", encoding="utf-8") as log_file:
                    process = start_isolated_hub_process(data_dir, log_file)

                    def wait_for_ping():
                        if process.poll() is not None:
                            return False

                        try:
                            with temporary_env_var("DEVHUB_DATA_DIR", data_dir):
                                base_url, token = DiscoveryService.get_hub_info()
                                response = RpcClient(base_url, token).call("hub.ping")
                                if response.get("result", {}).get("ok") is True:
                                    return True
                        except Exception:
                            return PENDING_WAIT_STATUS

                        return PENDING_WAIT_STATUS

                    poll_until_deadline_with_long_wait_status(
                        label="等待隔离 Host 就绪",
                        timeout_seconds=20,
                        poll_interval_seconds=0.2,
                        poll_once=wait_for_ping,
                        on_timeout=lambda: False,
                    )

                    with temporary_env_var("DEVHUB_DATA_DIR", data_dir):
                        app_id = self._new_app_id("wait-budget")
                        definition_path = self._create_definition(
                            app_id,
                            {
                                "exePath": get_test_python_executable(),
                                "args": [self._launch_script_path(), "7"],
                            },
                        )

                        base_url, token = DiscoveryService.get_hub_info()
                        client = RpcClient(base_url, token)

                        start_ts = time.monotonic()
                        response = client.launch_app(
                            app_id=app_id,
                            scope="",
                            wait_for_register_ms=5000,
                            request_id="launch-edge-003b",
                        )
                        elapsed_ms = int((time.monotonic() - start_ts) * 1000)

                    if not RpcAssertions.expect_error(result, response, -32020, "launch_failed"):
                        return result

                    if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "launch_register_timeout"}):
                        return result

                    if elapsed_ms < 900:
                        result.mark_failure(f"❌ launch 过早结束，未等待有效注册截止: elapsed={elapsed_ms}ms, response={response}")
                        return result

                    result.add_detail(f"✅ 有效注册截止耗时 {elapsed_ms}ms，返回 launch_register_timeout")
                    result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            delete_definitions([definition_path] if definition_path else [])
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except Exception:
                    process.kill()
                    process.wait(timeout=5)
            if log_file is not None:
                log_file.close()
            if temp_root and os.path.isdir(temp_root):
                try:
                    for root, dirs, files in os.walk(temp_root, topdown=False):
                        for file_name in files:
                            os.remove(os.path.join(root, file_name))
                        for dir_name in dirs:
                            os.rmdir(os.path.join(root, dir_name))
                    os.rmdir(temp_root)
                except Exception:
                    pass

        return result

    def test_launch_edge_004_dedupe_template_scope_placeholders_should_isolate(self):
        """SCOPE-LAUNCH-EDGE-004: dedupeKeyTemplate 作用域占位符应隔离。"""
        result = TestResult("SCOPE-LAUNCH-EDGE-004 dedupe 模板 scope 隔离")
        definition_paths = []

        try:
            app_id = self._new_app_id("scope-template")
            definition_paths.append(self._create_definition(
                app_id,
                self._build_launch_config("{appId}:{scope}:{scopeOrGlobal}"),
                scope="",
            ))
            definition_paths.append(self._create_definition(
                app_id,
                self._build_launch_config("{appId}:{scope}:{scopeOrGlobal}"),
                scope="workspace-a",
            ))

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_launch = client.launch_app(
                app_id=app_id,
                scope="",
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
            for definition_path in definition_paths:
                delete_definitions([definition_path] if definition_path else [])

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
                    "args": [script_path, capture_file, "{dedupeKey}", "{appId}"],
                },
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                scope="",
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
            delete_definitions([definition_path] if definition_path else [])
            safe_remove(capture_file)
            safe_remove(script_path)
            if script_path:
                safe_remove(os.path.dirname(script_path))

        return result

    def test_launch_edge_006_blank_exepath_should_fail_at_launch_stage(self):
        """LAUNCH-EDGE-006: 空白 launch.exePath 在 launch 阶段返回 launch_config_missing。"""
        result = TestResult("LAUNCH-EDGE-006 空白 exePath 启动失败阶段")
        definition_path = None

        try:
            app_id = self._new_app_id("blank-exepath")
            definition_path = self._create_definition(
                app_id,
                {
                    "exePath": "   ",
                    "argsTemplate": "ignored",
                },
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                scope="",
                wait_for_register_ms=0,
                request_id="launch-edge-006",
            )
            if not RpcAssertions.expect_error(result, response, -32020, "launch_failed"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "launch_config_missing"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_launch_edge_007_argstemplate_should_split_quotes_and_escapes(self):
        """LAUNCH-EDGE-007: argsTemplate 必须按 Spec 拆分引号与转义。"""
        result = TestResult("LAUNCH-EDGE-007 argsTemplate 引号与转义拆分")
        definition_path = None
        script_path = None
        capture_file = None

        try:
            app_id = self._new_app_id("args-template-split")
            temp_dir = tempfile.mkdtemp(prefix="devhub-launch-edge-")
            script_path = os.path.join(temp_dir, "capture_argv.py")
            capture_file = os.path.join(temp_dir, "captured.json")

            with open(script_path, "w", encoding="utf-8") as handle:
                handle.write(
                    "import json, pathlib, sys\n"
                    "pathlib.Path(sys.argv[1]).write_text(json.dumps(sys.argv[2:], ensure_ascii=False), encoding='utf-8')\n"
                )

            definition_path = self._create_definition(
                app_id,
                {
                    "exePath": get_test_python_executable(),
                    "argsTemplate": f"\"{script_path}\" \"{capture_file}\" --name \"hello world\" '--literal value' plain\\ value",
                },
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                scope="",
                wait_for_register_ms=0,
                request_id="launch-edge-007",
            )
            if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                return result

            deadline = time.time() + 3
            while time.time() < deadline and not os.path.exists(capture_file):
                time.sleep(0.05)

            if not os.path.exists(capture_file):
                result.mark_failure("❌ 启动进程未写出 argv 捕获文件")
                return result

            with open(capture_file, "r", encoding="utf-8") as handle:
                captured = json.load(handle)

            expected = ["--name", "hello world", "--literal value", "plain value"]
            if captured != expected:
                result.mark_failure(f"❌ argsTemplate argv 拆分不符合预期: expected={expected}, actual={captured}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            delete_definitions([definition_path] if definition_path else [])
            safe_remove(capture_file)
            safe_remove(script_path)
            if script_path:
                safe_remove(os.path.dirname(script_path))

        return result

    def test_launch_edge_008_invalid_argstemplate_should_return_invalid_params(self):
        """LAUNCH-EDGE-008: 非法 argsTemplate 必须返回 invalid_launch_args_template。"""
        result = TestResult("LAUNCH-EDGE-008 非法 argsTemplate 返回 invalid_params")
        definition_path = None

        try:
            app_id = self._new_app_id("invalid-args-template")
            definition_path = self._create_definition(
                app_id,
                {
                    "exePath": get_test_python_executable(),
                    "argsTemplate": "\"unterminated",
                },
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.launch_app(
                app_id=app_id,
                scope="",
                wait_for_register_ms=0,
                request_id="launch-edge-008",
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "invalid_launch_args_template"}):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            delete_definitions([definition_path] if definition_path else [])

        return result

    def test_launch_edge_009_shell_metacharacters_should_remain_argv_literals(self):
        """LAUNCH-EDGE-009: shell 元字符必须作为普通 argv 传递。"""
        result = TestResult("LAUNCH-EDGE-009 shell 元字符不经 shell 执行")
        definition_paths = []
        temp_dir = None

        try:
            temp_dir = tempfile.mkdtemp(prefix="devhub-launch-edge-shell-")
            shell_probe_file = os.path.join(temp_dir, "shell-executed.txt")
            template_capture_file = os.path.join(temp_dir, "template-argv.json")
            structured_capture_file = os.path.join(temp_dir, "structured-argv.json")
            capture_script_path = self._capture_argv_script_path()
            meta_args = [
                ";",
                "|",
                f">{shell_probe_file}",
                f"$(touch {shell_probe_file})",
                f"`touch {shell_probe_file}`",
            ]

            template_app_id = self._new_app_id("shell-template")
            definition_paths.append(self._create_definition(
                template_app_id,
                {
                    "exePath": get_test_python_executable(),
                    "argsTemplate": (
                        f"\"{capture_script_path}\" \"{template_capture_file}\" "
                        f"{meta_args[0]} {meta_args[1]} {meta_args[2]} "
                        f"\"{meta_args[3]}\" \"{meta_args[4]}\""
                    ),
                },
            ))

            structured_app_id = self._new_app_id("shell-argv")
            definition_paths.append(self._create_definition(
                structured_app_id,
                {
                    "exePath": get_test_python_executable(),
                    "args": [capture_script_path, structured_capture_file, *meta_args],
                },
            ))

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            for app_id, request_id in (
                (template_app_id, "launch-edge-009-template"),
                (structured_app_id, "launch-edge-009-argv"),
            ):
                response = client.launch_app(
                    app_id=app_id,
                    scope="",
                    wait_for_register_ms=0,
                    request_id=request_id,
                )
                if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                    return result

            deadline = time.time() + 3
            while time.time() < deadline and (
                not os.path.exists(template_capture_file)
                or not os.path.exists(structured_capture_file)
            ):
                time.sleep(0.05)

            if os.path.exists(shell_probe_file):
                result.mark_failure(f"❌ shell 元字符被执行并创建了哨兵文件: {shell_probe_file}")
                return result

            missing_files = [
                path for path in (template_capture_file, structured_capture_file)
                if not os.path.exists(path)
            ]
            if missing_files:
                result.mark_failure(f"❌ 启动进程未写出 argv 捕获文件: {missing_files}")
                return result

            with open(template_capture_file, "r", encoding="utf-8") as handle:
                template_captured = json.load(handle)
            with open(structured_capture_file, "r", encoding="utf-8") as handle:
                structured_captured = json.load(handle)

            if template_captured != meta_args:
                result.mark_failure(
                    f"❌ argsTemplate shell 元字符未按字面 argv 传递: expected={meta_args}, actual={template_captured}"
                )
                return result

            if structured_captured != meta_args:
                result.mark_failure(
                    f"❌ launch.args shell 元字符未按字面 argv 传递: expected={meta_args}, actual={structured_captured}"
                )
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            for definition_path in definition_paths:
                delete_definitions([definition_path] if definition_path else [])
            if temp_dir and os.path.isdir(temp_dir):
                try:
                    for root, dirs, files in os.walk(temp_dir, topdown=False):
                        for file_name in files:
                            os.remove(os.path.join(root, file_name))
                        for dir_name in dirs:
                            os.rmdir(os.path.join(root, dir_name))
                    os.rmdir(temp_dir)
                except Exception:
                    pass

        return result

    def test_launch_edge_010_structured_args_should_render_templates_and_override_args_template(self):
        """LAUNCH-EDGE-010: launch.args 必须渲染模板并优先于 argsTemplate。"""
        result = TestResult("LAUNCH-EDGE-010 launch.args 模板渲染与优先级")
        definition_path = None
        temp_dir = None

        try:
            app_id = self._new_app_id("structured-args")
            scope = "workspace-structured"
            temp_dir = tempfile.mkdtemp(prefix="devhub-launch-edge-args-")
            structured_capture_file = os.path.join(temp_dir, "structured-argv.json")
            template_capture_file = os.path.join(temp_dir, "template-argv.json")
            capture_script_path = self._capture_argv_script_path()

            definition_path = self._create_definition(
                app_id,
                {
                    "exePath": get_test_python_executable(),
                    "args": [
                        capture_script_path,
                        structured_capture_file,
                        "{appId}",
                        "{scope}",
                        "{scopeOrGlobal}",
                        "{httpBaseUrl}",
                    ],
                    "argsTemplate": f'"{capture_script_path}" "{template_capture_file}" args-template-should-be-ignored',
                },
                scope=scope,
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.launch_app(
                app_id=app_id,
                scope=scope,
                wait_for_register_ms=0,
                request_id="launch-edge-010",
            )
            if not RpcAssertions.expect_success(result, response, ["status", "launchId"]):
                return result

            deadline = time.time() + 3
            while time.time() < deadline and not os.path.exists(structured_capture_file):
                time.sleep(0.05)

            if not os.path.exists(structured_capture_file):
                result.mark_failure("❌ launch.args 启动进程未写出 argv 捕获文件")
                return result

            if os.path.exists(template_capture_file):
                result.mark_failure("❌ launch.args 存在时仍执行了 argsTemplate")
                return result

            with open(structured_capture_file, "r", encoding="utf-8") as handle:
                captured = json.load(handle)

            expected = [app_id, scope, scope, base_url]
            if captured != expected:
                result.mark_failure(f"❌ launch.args 模板渲染不符合预期: expected={expected}, actual={captured}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            delete_definitions([definition_path] if definition_path else [])
            if temp_dir and os.path.isdir(temp_dir):
                try:
                    for root, dirs, files in os.walk(temp_dir, topdown=False):
                        for file_name in files:
                            os.remove(os.path.join(root, file_name))
                        for dir_name in dirs:
                            os.rmdir(os.path.join(root, dir_name))
                    os.rmdir(temp_dir)
                except Exception:
                    pass

        return result

    def run_all_tests(self, full=False):
        return [
            self.test_launch_edge_001_default_dedupe_template_should_apply(),
            self.test_launch_edge_002_explicit_dedupe_key_should_take_effect(),
            self.test_launch_edge_003_wait_for_register_positive_should_return_started_or_starting(),
            self.test_launch_edge_003b_wait_budget_should_return_register_timeout_at_effective_deadline(),
            self.test_launch_edge_004_dedupe_template_scope_placeholders_should_isolate(),
            self.test_launch_edge_005_undocumented_placeholder_should_remain_literal(),
            self.test_launch_edge_006_blank_exepath_should_fail_at_launch_stage(),
            self.test_launch_edge_007_argstemplate_should_split_quotes_and_escapes(),
            self.test_launch_edge_008_invalid_argstemplate_should_return_invalid_params(),
            self.test_launch_edge_009_shell_metacharacters_should_remain_argv_literals(),
            self.test_launch_edge_010_structured_args_should_render_templates_and_override_args_template(),
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
