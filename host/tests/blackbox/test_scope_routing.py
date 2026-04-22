#!/usr/bin/env python3
"""
DevHub 作用域路由专项测试
"""

import os
import uuid
import json
import time
import threading
import unittest


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcClient,
    RpcAssertions,
    TestResult,
    get_shared_test_asset_path,
    get_test_python_executable,
    new_instance_id,
    safe_remove,
    sleep_with_long_wait_status,
    write_app_definition,
)


class TestScopeRouting(unittest.TestCase):
    """作用域路由测试类"""

    def _create_definition(self, app_id, include_launch=True, dedupe_key_template=None, scope=""):
        launch_config = None
        if include_launch:
            launch_config = {
                "exePath": get_test_python_executable(),
                "argsTemplate": get_shared_test_asset_path("launch_noop.py"),
            }
            if dedupe_key_template is not None:
                launch_config["dedupeKeyTemplate"] = dedupe_key_template

        return write_app_definition(
            app_id,
            scope=scope,
            rpc=True,
            events=False,
            launch=launch_config,
        )

    @staticmethod
    def _instance_id(prefix):
        return new_instance_id(prefix)

    @staticmethod
    def _app_id(suffix):
        return f"scope-{suffix}-{uuid.uuid4().hex[:6]}"

    @staticmethod
    def _extract_invocation_ids(items):
        return [item.get("invocationId") for item in items if item.get("invocationId")]

    def _poll_invocation_ids(self, client, instance_id, wait_ms=100):
        poll_response = client.poll_once(instance_id, max_count=10, wait_ms=wait_ms)
        return poll_response, self._extract_invocation_ids(poll_response.get("result", {}).get("items", []))

    def test_scope_001_register_omitted_and_null_should_be_rejected(self):
        """SCOPE-001: register scope omitted/null 必须返回 invalid_params"""
        result = TestResult("SCOPE-001 register omitted/null rejected")
        app_id = self._app_id("001")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            omitted_instance = self._instance_id("scope-omitted")
            null_instance = self._instance_id("scope-null")

            omitted_response = client.call("hub.apps.registerInstance", {
                "password": "scope-001-password",
                "instance": {
                    "instanceId": omitted_instance,
                    "appId": app_id,
                    "pid": 31001,
                    "invoke": {"poll": True, "respond": True}
                }
            }, request_id="scope-001-omitted")
            if not RpcAssertions.expect_error(result, omitted_response, -32602, "invalid_params"):
                return result

            null_response = client.register_instance(
                instance_id=null_instance,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=31002,
            )
            if not RpcAssertions.expect_error(result, null_response, -32602, "invalid_params"):
                return result

            list_response = client.call(
                "hub.apps.listInstances",
                {"appId": app_id, "scope": None},
                request_id="scope-001-list-unfiltered",
            )
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            instances = list_response["result"].get("instances", [])
            ids = {item.get("instanceId") for item in instances}
            if omitted_instance in ids or null_instance in ids:
                result.mark_failure(f"❌ 被拒绝的 register 请求仍然留下实例: {ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_scope_002_register_scope_should_match_exactly(self):
        """SCOPE-002: register 显式 scope 精确匹配"""
        result = TestResult("SCOPE-002 register 显式 scope 精确匹配")
        app_id = self._app_id("002")

        upper_instance = None
        lower_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            upper_instance = self._instance_id("scope-upper")
            lower_instance = self._instance_id("scope-lower")

            upper_register = client.register_instance(
                instance_id=upper_instance,
                app_id=app_id,
                scope="workspace-A",
                poll=True,
                respond=True,
                pid=31101,
            )
            if not RpcAssertions.expect_success(result, upper_register, ["instance"]):
                return result

            lower_register = client.register_instance(
                instance_id=lower_instance,
                app_id=app_id,
                scope="workspace-a",
                poll=True,
                respond=True,
                pid=31102,
            )
            if not RpcAssertions.expect_success(result, lower_register, ["instance"]):
                return result

            upper_list = client.call("hub.apps.listInstances", {"appId": app_id, "scope": "workspace-A"}, request_id="scope-002-upper")
            if not RpcAssertions.expect_success(result, upper_list, ["instances"]):
                return result

            lower_list = client.call("hub.apps.listInstances", {"appId": app_id, "scope": "workspace-a"}, request_id="scope-002-lower")
            if not RpcAssertions.expect_success(result, lower_list, ["instances"]):
                return result

            upper_ids = {item.get("instanceId") for item in upper_list["result"].get("instances", [])}
            lower_ids = {item.get("instanceId") for item in lower_list["result"].get("instances", [])}

            if upper_instance not in upper_ids or lower_instance in upper_ids:
                result.mark_failure(f"❌ workspace-A 过滤结果异常: {upper_ids}")
                return result

            if lower_instance not in lower_ids or upper_instance in lower_ids:
                result.mark_failure(f"❌ workspace-a 过滤结果异常: {lower_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if upper_instance:
                    cleanup_client.unregister_instance(upper_instance)
                if lower_instance:
                    cleanup_client.unregister_instance(lower_instance)
            except Exception:
                pass

        return result

    def test_scope_003_empty_scope_should_be_global_equivalent(self):
        """SCOPE-003: register/list/launch scope='' 与 Global 等价"""
        result = TestResult("SCOPE-003 scope='' 等价 Global")
        app_id = self._app_id("003")
        definition_path = None
        empty_instance = None
        invalid_null_instance = None

        try:
            definition_path = self._create_definition(app_id, include_launch=True)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            empty_instance = self._instance_id("scope-empty")
            invalid_null_instance = self._instance_id("scope-null")

            register_empty_response = client.register_instance(
                instance_id=empty_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31201,
            )
            if not RpcAssertions.expect_success(result, register_empty_response, ["instance"]):
                return result

            register_null_response = client.register_instance(
                instance_id=invalid_null_instance,
                app_id=app_id,
                scope=None,
                poll=True,
                respond=True,
                pid=31202,
            )
            if not RpcAssertions.expect_error(result, register_null_response, -32602, "invalid_params"):
                return result

            empty_registered_scope = register_empty_response["result"]["instance"].get("scope")
            if empty_registered_scope != "":
                result.mark_failure(f"❌ scope='' 注册响应未保持空字符串: {register_empty_response}")
                return result

            list_empty_scope = client.call("hub.apps.listInstances", {"appId": app_id, "scope": ""}, request_id="scope-003-list-empty")
            if not RpcAssertions.expect_success(result, list_empty_scope, ["instances"]):
                return result

            empty_scope_ids = {item.get("instanceId") for item in list_empty_scope["result"].get("instances", [])}
            if empty_instance not in empty_scope_ids or invalid_null_instance in empty_scope_ids:
                result.mark_failure(f"❌ scope='' 查询未按新 Global 契约处理: {empty_scope_ids}")
                return result

            launch_empty_scope = client.launch_app(
                app_id=app_id,
                scope="",
                wait_for_register_ms=0,
                request_id="scope-003-launch-empty",
            )
            if not RpcAssertions.expect_success(result, launch_empty_scope, ["status", "launchId"]):
                return result

            launch_null_scope = client.launch_app(
                app_id=app_id,
                scope=None,
                wait_for_register_ms=0,
                request_id="scope-003-launch-null",
            )
            if not RpcAssertions.expect_error(result, launch_null_scope, -32602, "invalid_params"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if empty_instance:
                    cleanup_client.unregister_instance(empty_instance)
                if invalid_null_instance:
                    cleanup_client.unregister_instance(invalid_null_instance)
            except Exception:
                pass

            safe_remove(definition_path)

        return result

    def test_scope_004_global_literal_should_be_explicit_scope(self):
        """SCOPE-004: scope='global' 作为显式作用域，不回退默认 Global"""
        result = TestResult("SCOPE-004 scope='global' 显式作用域")
        app_id = self._app_id("004")
        definition_paths = []
        scoped_global_instance = None
        global_instance = None

        try:
            definition_paths.append(self._create_definition(app_id, include_launch=True, scope=""))
            definition_paths.append(self._create_definition(app_id, include_launch=True, scope="global"))
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            scoped_global_instance = self._instance_id("scope-global")
            global_instance = self._instance_id("scope-global-default")

            register_global_literal = client.register_instance(
                instance_id=scoped_global_instance,
                app_id=app_id,
                scope="global",
                poll=True,
                respond=True,
                pid=31301,
            )
            if not RpcAssertions.expect_success(result, register_global_literal, ["instance"]):
                return result

            register_global = client.register_instance(
                instance_id=global_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31302,
            )
            if not RpcAssertions.expect_success(result, register_global, ["instance"]):
                return result

            list_response = client.call("hub.apps.listInstances", {"appId": app_id, "scope": "global"}, request_id="scope-004-list")
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            scoped_global_ids = {item.get("instanceId") for item in list_response["result"].get("instances", [])}
            if scoped_global_instance not in scoped_global_ids:
                result.mark_failure(f"❌ scope='global' 查询未命中显式作用域实例: {scoped_global_ids}")
                return result
            if global_instance in scoped_global_ids:
                result.mark_failure(f"❌ scope='global' 查询错误回退到默认 Global: {scoped_global_ids}")
                return result

            default_list_response = client.call("hub.apps.listInstances", {"appId": app_id, "scope": ""}, request_id="scope-004-list-default")
            if not RpcAssertions.expect_success(result, default_list_response, ["instances"]):
                return result

            default_ids = {item.get("instanceId") for item in default_list_response["result"].get("instances", [])}
            if global_instance not in default_ids:
                result.mark_failure(f"❌ 默认 Global 查询未命中 Global 实例: {default_ids}")
                return result
            if scoped_global_instance in default_ids:
                result.mark_failure(f"❌ 默认 Global 查询错误命中 scope='global' 实例: {default_ids}")
                return result

            launch_response = client.launch_app(
                app_id=app_id,
                scope="global",
                wait_for_register_ms=0,
                request_id="scope-004-launch",
            )
            if not RpcAssertions.expect_success(result, launch_response, ["status", "launchId"]):
                return result

            launch_global_default = client.launch_app(
                app_id=app_id,
                scope="",
                wait_for_register_ms=0,
                request_id="scope-004-launch-default",
            )
            if not RpcAssertions.expect_success(result, launch_global_default, ["status", "launchId"]):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if scoped_global_instance:
                    cleanup_client.unregister_instance(scoped_global_instance)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
            except Exception:
                pass

            for definition_path in definition_paths:
                safe_remove(definition_path)

        return result

    def test_scope_005_notify_request_empty_scope_should_only_hit_global(self):
        """SCOPE-005: notify/request target.scope='' 仅命中 Global"""
        result = TestResult("SCOPE-005 notify/request empty-scope Global routing")
        app_id = self._app_id("005")

        global_instance = None
        scoped_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_instance = self._instance_id("scope-global")
            scoped_instance = self._instance_id("scope-scoped")

            register_global = client.register_instance(
                instance_id=global_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31401,
            )
            if not RpcAssertions.expect_success(result, register_global, ["instance"]):
                return result

            register_scoped = client.register_instance(
                instance_id=scoped_instance,
                app_id=app_id,
                scope="workspace-A",
                poll=True,
                respond=True,
                pid=31402,
            )
            if not RpcAssertions.expect_success(result, register_scoped, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"case": "empty-global-notify"},
                target_scope="",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-005-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            notify_invocation_id = notify_response["result"]["invocationId"]

            global_poll_response, global_ids = self._poll_invocation_ids(client, global_instance)
            if not RpcAssertions.expect_success(result, global_poll_response, ["items"]):
                return result

            scoped_poll_response, scoped_ids = self._poll_invocation_ids(client, scoped_instance)
            if not RpcAssertions.expect_success(result, scoped_poll_response, ["items"]):
                return result

            if notify_invocation_id not in global_ids:
                result.mark_failure(f"❌ empty-scope Global notify 未被 Global 实例拉取: {global_ids}")
                return result
            if notify_invocation_id in scoped_ids:
                result.mark_failure(f"❌ empty-scope Global notify 被 scoped 实例误拉取: {scoped_ids}")
                return result

            request_holder = {}
            scoped_request_poll_holder = {}

            def poll_global_and_respond_for_request():
                poll_client = RpcClient(base_url, token)
                poll_response = poll_client.poll_once(global_instance, max_count=1, wait_ms=1500)
                request_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if not items:
                    return

                invocation_id = items[0].get("invocationId")
                request_holder["invocationId"] = invocation_id
                if invocation_id:
                    request_holder["respond"] = poll_client.respond_value(
                        global_instance,
                        invocation_id,
                        {"handledBy": "global"},
                    )

            def poll_scoped_for_request():
                scoped_poll_client = RpcClient(base_url, token)
                scoped_poll = scoped_poll_client.poll_once(scoped_instance, max_count=10, wait_ms=500)
                scoped_request_poll_holder["poll"] = scoped_poll
                if "error" in scoped_poll:
                    return
                scoped_request_poll_holder["ids"] = self._extract_invocation_ids(
                    scoped_poll.get("result", {}).get("items", [])
                )

            global_worker = threading.Thread(target=poll_global_and_respond_for_request, daemon=True)
            scoped_worker = threading.Thread(target=poll_scoped_for_request, daemon=True)
            global_worker.start()
            scoped_worker.start()

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                args={"case": "empty-global-request"},
                target_scope="",
                options={
                    "ttlMs": 4000,
                    "waitTimeoutMs": 3000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="scope-005-request",
            )

            global_worker.join(timeout=3)
            scoped_worker.join(timeout=3)

            if not RpcAssertions.expect_success(result, request_response, ["value"]):
                return result

            value = request_response.get("result", {}).get("value", {})
            if value.get("handledBy") != "global":
                result.mark_failure(f"❌ empty-scope Global request 未由 Global 实例处理: {request_response}")
                return result

            global_request_invocation_id = request_holder.get("invocationId")
            if not global_request_invocation_id:
                result.mark_failure(f"❌ empty-scope Global request 未被 Global 实例 poll 到: {request_holder}")
                return result

            scoped_request_ids = scoped_request_poll_holder.get("ids", [])
            if global_request_invocation_id in scoped_request_ids:
                result.mark_failure(f"❌ empty-scope Global request 被 scoped 实例误拉取: {scoped_request_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
                if scoped_instance:
                    cleanup_client.unregister_instance(scoped_instance)
            except Exception:
                pass

        return result

    def test_scope_006_explicit_scope_should_not_fallback_to_global(self):
        """SCOPE-006: notify/request 显式 scope 不回退 Global"""
        result = TestResult("SCOPE-006 显式 scope 不回退 Global")
        app_id = self._app_id("006")
        definition_path = None
        global_instance = None

        try:
            definition_path = self._create_definition(app_id, include_launch=False)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_instance = self._instance_id("scope-only-global")
            register_response = client.register_instance(
                instance_id=global_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31501,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"scope": "workspace-A"},
                target_scope="workspace-A",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-006-notify",
            )
            if not RpcAssertions.expect_error(result, notify_response, -32010, "instance_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, notify_response, {"reason": "offline_no_queue"}):
                return result

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                args={"scope": "workspace-A"},
                target_scope="workspace-A",
                options={
                    "ttlMs": 3000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="scope-006-request",
            )
            if not RpcAssertions.expect_error(result, request_response, -32010, "instance_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, request_response, {"reason": "offline_no_queue"}):
                return result

            poll_response, invocation_ids = self._poll_invocation_ids(client, global_instance)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result
            if invocation_ids:
                result.mark_failure(f"❌ 显式 scope 调用错误回退到 Global: {invocation_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
            except Exception:
                pass

            safe_remove(definition_path)

        return result

    def test_scope_007_invalid_target_scope_type_should_return_invalid_params(self):
        """SCOPE-007: target.scope 类型非法 -> -32602 invalid_params"""
        result = TestResult("SCOPE-007 target.scope 类型非法")
        app_id = self._app_id("007")
        definition_path = None

        try:
            definition_path = self._create_definition(app_id, include_launch=False)
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            invalid_notify_cases = [
                ("scope-007-notify-int", 123),
                ("scope-007-notify-bool", True),
                ("scope-007-notify-object", {"scope": "workspace-a"}),
            ]
            for request_id, target_scope in invalid_notify_cases:
                notify_response = client.invoke_notify(
                    app_id=app_id,
                    method="asset.rebuild",
                    target_scope=target_scope,
                    queue_if_offline=True,
                    auto_launch=False,
                    request_id=request_id,
                )
                if not RpcAssertions.expect_error(result, notify_response, -32602, "invalid_params"):
                    return result

            invalid_request_cases = [
                ("scope-007-request-int", 123),
                ("scope-007-request-bool", True),
                ("scope-007-request-object", {"scope": "workspace-a"}),
            ]
            for request_id, target_scope in invalid_request_cases:
                request_response = client.invoke_request(
                    app_id=app_id,
                    method="asset.build",
                    target_scope=target_scope,
                    options={
                        "ttlMs": 2000,
                        "waitTimeoutMs": 1000,
                        "queueIfOffline": True,
                        "autoLaunch": False,
                    },
                    request_id=request_id,
                )
                if not RpcAssertions.expect_error(result, request_response, -32602, "invalid_params"):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(definition_path)

        return result

    def test_scope_008_scope_match_should_be_case_sensitive(self):
        """SCOPE-008: case-sensitive 精确匹配"""
        result = TestResult("SCOPE-008 scope 大小写敏感")
        app_id = self._app_id("008")

        upper_instance = None
        lower_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            upper_instance = self._instance_id("scope-upper")
            lower_instance = self._instance_id("scope-lower")

            upper_register = client.register_instance(
                instance_id=upper_instance,
                app_id=app_id,
                scope="workspace-A",
                poll=True,
                respond=True,
                pid=31601,
            )
            if not RpcAssertions.expect_success(result, upper_register, ["instance"]):
                return result

            lower_register = client.register_instance(
                instance_id=lower_instance,
                app_id=app_id,
                scope="workspace-a",
                poll=True,
                respond=True,
                pid=31602,
            )
            if not RpcAssertions.expect_success(result, lower_register, ["instance"]):
                return result

            notify_upper = client.invoke_notify(
                app_id=app_id,
                method="asset.upper",
                args={"scope": "workspace-A"},
                target_scope="workspace-A",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-008-upper",
            )
            if not RpcAssertions.expect_success(result, notify_upper, ["invocationId"]):
                return result

            notify_lower = client.invoke_notify(
                app_id=app_id,
                method="asset.lower",
                args={"scope": "workspace-a"},
                target_scope="workspace-a",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-008-lower",
            )
            if not RpcAssertions.expect_success(result, notify_lower, ["invocationId"]):
                return result

            upper_invocation = notify_upper["result"]["invocationId"]
            lower_invocation = notify_lower["result"]["invocationId"]

            upper_poll, upper_ids = self._poll_invocation_ids(client, upper_instance)
            if not RpcAssertions.expect_success(result, upper_poll, ["items"]):
                return result

            lower_poll, lower_ids = self._poll_invocation_ids(client, lower_instance)
            if not RpcAssertions.expect_success(result, lower_poll, ["items"]):
                return result

            if upper_invocation not in upper_ids or upper_invocation in lower_ids:
                result.mark_failure(f"❌ workspace-A invocation 路由异常, upper={upper_ids}, lower={lower_ids}")
                return result

            if lower_invocation not in lower_ids or lower_invocation in upper_ids:
                result.mark_failure(f"❌ workspace-a invocation 路由异常, upper={upper_ids}, lower={lower_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if upper_instance:
                    cleanup_client.unregister_instance(upper_instance)
                if lower_instance:
                    cleanup_client.unregister_instance(lower_instance)
            except Exception:
                pass

        return result

    def test_scope_008_ws_whitespace_scope_should_match_exactly_without_trim(self):
        """SCOPE-008-WS: 空白字符串 scope 按原值精确匹配（不 trim）"""
        result = TestResult("SCOPE-008-WS 空白 scope 精确匹配")
        app_id = self._app_id("008ws")

        global_instance = None
        whitespace_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_instance = self._instance_id("scope-ws-global")
            whitespace_instance = self._instance_id("scope-ws-space")

            register_global = client.register_instance(
                instance_id=global_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31611,
            )
            if not RpcAssertions.expect_success(result, register_global, ["instance"]):
                return result

            register_whitespace = client.register_instance(
                instance_id=whitespace_instance,
                app_id=app_id,
                scope="   ",
                poll=True,
                respond=True,
                pid=31612,
            )
            if not RpcAssertions.expect_success(result, register_whitespace, ["instance"]):
                return result

            scoped_list = client.call(
                "hub.apps.listInstances",
                {"appId": app_id, "scope": "   "},
                request_id="scope-008ws-list",
            )
            if not RpcAssertions.expect_success(result, scoped_list, ["instances"]):
                return result

            listed_instances = scoped_list["result"].get("instances", [])
            if not RpcAssertions.assert_items_all_match_scope(result, listed_instances, "   "):
                return result

            listed_ids = {item.get("instanceId") for item in listed_instances}
            if whitespace_instance not in listed_ids or global_instance in listed_ids:
                result.mark_failure(f"❌ 空白 scope listInstances 过滤异常: {listed_ids}")
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.whitespace.notify",
                args={"case": "whitespace-notify"},
                target_scope="   ",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-008ws-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            notify_invocation_id = notify_response["result"]["invocationId"]

            whitespace_notify_poll, whitespace_notify_ids = self._poll_invocation_ids(client, whitespace_instance)
            if not RpcAssertions.expect_success(result, whitespace_notify_poll, ["items"]):
                return result

            global_notify_poll, global_notify_ids = self._poll_invocation_ids(client, global_instance)
            if not RpcAssertions.expect_success(result, global_notify_poll, ["items"]):
                return result

            if notify_invocation_id not in whitespace_notify_ids:
                result.mark_failure(f"❌ 空白 scope notify 未命中空白 scope 实例: {whitespace_notify_ids}")
                return result

            if notify_invocation_id in global_notify_ids:
                result.mark_failure(f"❌ 空白 scope notify 错误命中 Global 实例: {global_notify_ids}")
                return result

            request_holder = {}
            global_request_poll_holder = {}

            def poll_whitespace_and_respond():
                poll_client = RpcClient(base_url, token)
                poll_response = poll_client.poll_once(whitespace_instance, max_count=1, wait_ms=1500)
                request_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                items = poll_response.get("result", {}).get("items", [])
                if not items:
                    return

                invocation_id = items[0].get("invocationId")
                request_holder["invocationId"] = invocation_id
                if invocation_id:
                    request_holder["respond"] = poll_client.respond_value(
                        whitespace_instance,
                        invocation_id,
                        {"handledBy": "whitespace-scope"},
                    )

            def poll_global_for_request():
                poll_client = RpcClient(base_url, token)
                poll_response = poll_client.poll_once(global_instance, max_count=10, wait_ms=500)
                global_request_poll_holder["poll"] = poll_response
                if "error" in poll_response:
                    return

                global_request_poll_holder["ids"] = self._extract_invocation_ids(
                    poll_response.get("result", {}).get("items", [])
                )

            whitespace_worker = threading.Thread(target=poll_whitespace_and_respond, daemon=True)
            global_worker = threading.Thread(target=poll_global_for_request, daemon=True)
            whitespace_worker.start()
            global_worker.start()

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.whitespace.request",
                args={"case": "whitespace-request"},
                target_scope="   ",
                options={
                    "ttlMs": 4000,
                    "waitTimeoutMs": 3000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="scope-008ws-request",
            )

            whitespace_worker.join(timeout=3)
            global_worker.join(timeout=3)

            if not RpcAssertions.expect_success(result, request_response, ["value"]):
                return result

            value = request_response.get("result", {}).get("value", {})
            if value.get("handledBy") != "whitespace-scope":
                result.mark_failure(f"❌ 空白 scope request 未由空白 scope 实例处理: {request_response}")
                return result

            whitespace_request_invocation_id = request_holder.get("invocationId")
            if not whitespace_request_invocation_id:
                result.mark_failure(f"❌ 空白 scope request 未被空白 scope 实例 poll 到: {request_holder}")
                return result

            global_request_ids = global_request_poll_holder.get("ids", [])
            if whitespace_request_invocation_id in global_request_ids:
                result.mark_failure(f"❌ 空白 scope request 被 Global 实例误拉取: {global_request_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
                if whitespace_instance:
                    cleanup_client.unregister_instance(whitespace_instance)
            except Exception:
                pass

        return result

    def test_scope_009_target_instance_id_should_take_precedence(self):
        """SCOPE-009: target.instanceId 优先且不回退"""
        result = TestResult("SCOPE-009 target.instanceId 优先")
        app_id = self._app_id("009")

        available_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            available_instance = self._instance_id("scope-available")
            register_response = client.register_instance(
                instance_id=available_instance,
                app_id=app_id,
                scope="workspace-A",
                poll=True,
                respond=True,
                pid=31701,
            )
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            missing_target = self._instance_id("scope-missing")

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                target_scope="workspace-A",
                target_instance_id=missing_target,
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-009-notify",
            )
            if not RpcAssertions.expect_error(result, notify_response, -32010, "instance_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, notify_response, {"reason": "target_instance_missing"}):
                return result

            request_response = client.invoke_request(
                app_id=app_id,
                method="asset.build",
                target_scope="workspace-A",
                target_instance_id=missing_target,
                options={
                    "ttlMs": 2000,
                    "waitTimeoutMs": 1000,
                    "queueIfOffline": False,
                    "autoLaunch": False,
                },
                request_id="scope-009-request",
            )
            if not RpcAssertions.expect_error(result, request_response, -32010, "instance_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, request_response, {"reason": "target_instance_missing"}):
                return result

            poll_response, invocation_ids = self._poll_invocation_ids(client, available_instance)
            if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                return result
            if invocation_ids:
                result.mark_failure(f"❌ target.instanceId 缺失时错误回退投递: {invocation_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if available_instance:
                    cleanup_client.unregister_instance(available_instance)
            except Exception:
                pass

        return result

    def test_scope_010_offline_matrix_should_be_consistent_across_scopes(self):
        """SCOPE-010: offline matrix 在 Global 与显式 scope 下一致"""
        result = TestResult("SCOPE-010 offline matrix scope 一致性")

        definition_paths = []
        registered_instances = []

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            scope_cases = [
                (None, "global", "global"),
                ("workspace-A", "workspace-A", "scoped"),
            ]

            for target_scope, scope_name, scope_tag in scope_cases:
                queue_off_app = self._app_id(f"010-{scope_tag}-noqueue")
                queue_off_resp = client.invoke_notify(
                    app_id=queue_off_app,
                    method="asset.rebuild",
                    args={"case": f"{scope_name}-queue-false"},
                    target_scope=target_scope,
                    queue_if_offline=False,
                    auto_launch=False,
                    request_id=f"scope-010-{scope_tag}-noqueue",
                )
                if not RpcAssertions.expect_error(result, queue_off_resp, -32010, "instance_not_found"):
                    return result
                if not RpcAssertions.expect_error_data_fields(result, queue_off_resp, {"reason": "offline_no_queue"}):
                    return result

                pending_app = self._app_id(f"010-{scope_tag}-pending")
                definition_paths.append(self._create_definition(
                    pending_app,
                    include_launch=False,
                    scope=target_scope,
                ))
                pending_resp = client.invoke_notify(
                    app_id=pending_app,
                    method="asset.rebuild",
                    args={"case": f"{scope_name}-queue-true-autolaunch-false"},
                    target_scope=target_scope,
                    queue_if_offline=True,
                    auto_launch=False,
                    request_id=f"scope-010-{scope_tag}-pending",
                )
                if not RpcAssertions.expect_success(result, pending_resp, ["invocationId"]):
                    return result

                pending_invocation_id = pending_resp["result"]["invocationId"]
                pending_instance = self._instance_id(f"scope-010-{scope_tag}-pending")
                registered_instances.append(pending_instance)

                register_pending = client.register_instance(
                    instance_id=pending_instance,
                    app_id=pending_app,
                    scope=target_scope,
                    poll=True,
                    respond=True,
                    pid=31901,
                )
                if not RpcAssertions.expect_success(result, register_pending, ["instance"]):
                    return result

                pending_poll_resp, pending_ids = self._poll_invocation_ids(client, pending_instance, wait_ms=500)
                if not RpcAssertions.expect_success(result, pending_poll_resp, ["items"]):
                    return result
                if pending_invocation_id not in pending_ids:
                    result.mark_failure(
                        f"❌ {scope_name} 下 queueIfOffline=true, autoLaunch=false 未进入待投递链路: {pending_ids}")
                    return result

                autolaunch_app = self._app_id(f"010-{scope_tag}-autolaunch")
                definition_paths.append(self._create_definition(
                    autolaunch_app,
                    include_launch=True,
                    dedupe_key_template="{appId}:{scopeOrGlobal}",
                    scope=target_scope,
                ))

                autolaunch_resp = client.invoke_notify(
                    app_id=autolaunch_app,
                    method="asset.rebuild",
                    args={"case": f"{scope_name}-queue-true-autolaunch-true"},
                    target_scope=target_scope,
                    queue_if_offline=True,
                    auto_launch=True,
                    request_id=f"scope-010-{scope_tag}-autolaunch",
                )
                if not RpcAssertions.expect_success(result, autolaunch_resp, ["invocationId"]):
                    return result

                launch_after_autolaunch = client.launch_app(
                    app_id=autolaunch_app,
                    scope=target_scope,
                    wait_for_register_ms=0,
                    request_id=f"scope-010-{scope_tag}-launch-check",
                )
                if not RpcAssertions.expect_success(result, launch_after_autolaunch, ["status", "launchId"]):
                    return result

                launch_status = launch_after_autolaunch["result"].get("status")
                if launch_status != "already_running":
                    result.mark_failure(
                        f"❌ {scope_name} 下 autoLaunch 未按同 scope 透传去重: {launch_after_autolaunch}")
                    return result

                missing_def_app = self._app_id(f"010-{scope_tag}-nodef")
                no_definition_resp = client.invoke_notify(
                    app_id=missing_def_app,
                    method="asset.rebuild",
                    args={"case": f"{scope_name}-no-definition"},
                    target_scope=target_scope,
                    queue_if_offline=True,
                    auto_launch=False,
                    request_id=f"scope-010-{scope_tag}-nodef",
                )
                if not RpcAssertions.expect_error(result, no_definition_resp, -32010, "instance_not_found"):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                for instance_id in registered_instances:
                    cleanup_client.unregister_instance(instance_id)
            except Exception:
                pass

            for definition_path in definition_paths:
                safe_remove(definition_path)

        return result

    def test_scope_012_poll_should_not_leak_between_scopes(self):
        """SCOPE-012: poll 投递不跨 scope 泄漏"""
        result = TestResult("SCOPE-012 poll 不跨 scope 泄漏")
        app_id = self._app_id("012")

        global_instance = None
        scoped_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            global_instance = self._instance_id("scope-global")
            scoped_instance = self._instance_id("scope-workspace")

            register_global = client.register_instance(
                instance_id=global_instance,
                app_id=app_id,
                scope="",
                poll=True,
                respond=True,
                pid=31801,
            )
            if not RpcAssertions.expect_success(result, register_global, ["instance"]):
                return result

            register_scoped = client.register_instance(
                instance_id=scoped_instance,
                app_id=app_id,
                scope="workspace-A",
                poll=True,
                respond=True,
                pid=31802,
            )
            if not RpcAssertions.expect_success(result, register_scoped, ["instance"]):
                return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"case": "scope-leak-check"},
                target_scope="workspace-A",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-012-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            target_invocation_id = notify_response["result"]["invocationId"]

            global_poll_response, global_ids = self._poll_invocation_ids(client, global_instance)
            if not RpcAssertions.expect_success(result, global_poll_response, ["items"]):
                return result
            if target_invocation_id in global_ids:
                result.mark_failure(f"❌ Global 实例误拉取 scoped invocation: {global_ids}")
                return result

            scoped_poll_response, scoped_ids = self._poll_invocation_ids(client, scoped_instance)
            if not RpcAssertions.expect_success(result, scoped_poll_response, ["items"]):
                return result
            if target_invocation_id not in scoped_ids:
                result.mark_failure(f"❌ scoped 实例未拉取到目标 invocation: {scoped_ids}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
                if scoped_instance:
                    cleanup_client.unregister_instance(scoped_instance)
            except Exception:
                pass

        return result

    def test_scope_full_lease_redelivery_should_respect_scope(self):
        """full-only: lease 到期重投递后仍严格遵守 scope 过滤"""
        result = TestResult("SCOPE-FULL lease 重投递 scope 过滤")
        app_id = self._app_id("010-full-lease")

        holder_instance = None
        same_scope_receiver = None
        global_instance = None
        other_scope_instance = None

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            holder_instance = self._instance_id("scope-full-holder")
            same_scope_receiver = self._instance_id("scope-full-receiver")
            global_instance = self._instance_id("scope-full-global")
            other_scope_instance = self._instance_id("scope-full-other")

            for instance_id, scope, pid in [
                (holder_instance, "workspace-A", 31911),
                (same_scope_receiver, "workspace-A", 31912),
                (global_instance, "", 31913),
                (other_scope_instance, "workspace-B", 31914),
            ]:
                register_response = client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope=scope,
                    poll=True,
                    respond=True,
                    pid=pid,
                )
                if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                    return result

            notify_response = client.invoke_notify(
                app_id=app_id,
                method="asset.rebuild",
                args={"case": "full-lease-redelivery"},
                target_scope="workspace-A",
                queue_if_offline=False,
                auto_launch=False,
                request_id="scope-full-lease-notify",
            )
            if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                return result

            invocation_id = notify_response["result"]["invocationId"]

            first_poll = client.poll_once(holder_instance, max_count=10, wait_ms=200)
            if not RpcAssertions.expect_success(result, first_poll, ["items"]):
                return result

            first_item = None
            for item in first_poll.get("result", {}).get("items", []):
                if item.get("invocationId") == invocation_id:
                    first_item = item
                    break

            if not first_item:
                result.mark_failure("❌ 首次 poll 未拿到目标 scoped invocation")
                return result

            first_delivery = first_item.get("delivery", {})
            first_attempt = first_delivery.get("attempt")
            if first_attempt != 1:
                result.mark_failure(f"❌ 首次 delivery.attempt 非 1: {first_delivery}")
                return result

            lease_seconds = first_delivery.get("leaseSeconds")
            if not isinstance(lease_seconds, int) or lease_seconds < 1:
                result.mark_failure(f"❌ 首次 delivery.leaseSeconds 非法: {first_delivery}")
                return result

            sleep_with_long_wait_status(lease_seconds + 1.0, "等待 scoped invocation lease 到期后重投递")

            global_poll, global_ids = self._poll_invocation_ids(client, global_instance, wait_ms=500)
            if not RpcAssertions.expect_success(result, global_poll, ["items"]):
                return result
            if invocation_id in global_ids:
                result.mark_failure(f"❌ lease 重投递后 Global 实例误拉取 scoped invocation: {global_ids}")
                return result

            other_scope_poll, other_scope_ids = self._poll_invocation_ids(client, other_scope_instance, wait_ms=500)
            if not RpcAssertions.expect_success(result, other_scope_poll, ["items"]):
                return result
            if invocation_id in other_scope_ids:
                result.mark_failure(f"❌ lease 重投递后非匹配 scope 实例误拉取: {other_scope_ids}")
                return result

            receiver_poll = client.poll_once(same_scope_receiver, max_count=10, wait_ms=1000)
            if not RpcAssertions.expect_success(result, receiver_poll, ["items"]):
                return result

            receiver_item = None
            for item in receiver_poll.get("result", {}).get("items", []):
                if item.get("invocationId") == invocation_id:
                    receiver_item = item
                    break

            if not receiver_item:
                result.mark_failure("❌ lease 到期后同 scope 实例未接收到重投递 invocation")
                return result

            second_attempt = receiver_item.get("delivery", {}).get("attempt")
            if not isinstance(second_attempt, int) or second_attempt < 2:
                result.mark_failure(f"❌ 重投递 delivery.attempt 未递增: {receiver_item.get('delivery')}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                base_url, token = DiscoveryService.get_hub_info()
                cleanup_client = RpcClient(base_url, token)
                if holder_instance:
                    cleanup_client.unregister_instance(holder_instance)
                if same_scope_receiver:
                    cleanup_client.unregister_instance(same_scope_receiver)
                if global_instance:
                    cleanup_client.unregister_instance(global_instance)
                if other_scope_instance:
                    cleanup_client.unregister_instance(other_scope_instance)
            except Exception:
                pass

        return result

    def run_all_tests(self, full=False):
        """运行所有 scope 路由测试。"""
        results = [
            self.test_scope_001_register_omitted_and_null_should_be_rejected(),
            self.test_scope_002_register_scope_should_match_exactly(),
            self.test_scope_003_empty_scope_should_be_global_equivalent(),
            self.test_scope_004_global_literal_should_be_explicit_scope(),
            self.test_scope_005_notify_request_empty_scope_should_only_hit_global(),
            self.test_scope_006_explicit_scope_should_not_fallback_to_global(),
            self.test_scope_007_invalid_target_scope_type_should_return_invalid_params(),
            self.test_scope_008_scope_match_should_be_case_sensitive(),
            self.test_scope_008_ws_whitespace_scope_should_match_exactly_without_trim(),
            self.test_scope_009_target_instance_id_should_take_precedence(),
            self.test_scope_010_offline_matrix_should_be_consistent_across_scopes(),
            self.test_scope_012_poll_should_not_leak_between_scopes(),
        ]

        if full:
            results.append(self.test_scope_full_lease_redelivery_should_respect_scope())

        return results


if __name__ == "__main__":
    test = TestScopeRouting()
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
