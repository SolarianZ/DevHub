#!/usr/bin/env python3
"""
DevHub 应用定义测试
"""

import os
import unittest
import json
import uuid


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcAssertions,
    RpcClient,
    TestResult,
    build_app_definition,
    build_definition_file_name,
    build_definition_identity_params,
    get_definitions_dir,
    safe_remove,
    write_app_definition,
)


class TestAppDefinitions(unittest.TestCase):
    """AppDefinition 测试类"""

    @staticmethod
    def _new_app_id(prefix: str) -> str:
        return f"{prefix}-{uuid.uuid4().hex[:8]}"

    def create_test_app_definition(self):
        """创建测试应用程序定义"""
        app_id = self._new_app_id("test-app")
        test_app_path = write_app_definition(
            app_id,
            display_name="Test Application",
            description="This is a test application",
            rpc=True,
            events=False,
            launch={
                "exePath": "echo",
                "argsTemplate": "Hello from Test Application",
            },
        )
        return app_id, test_app_path

    def test_list_definitions(self):
        """测试列出所有应用程序定义"""
        result = TestResult("测试列出所有应用程序定义")
        test_app_path = None

        try:
            app_id, test_app_path = self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.listDefinitions", {"scope": None})
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            result.add_detail(f"✅ 返回 {len(definitions)} 个应用程序定义")

            found_test_app = any(d.get("appId") == app_id for d in definitions)
            if not found_test_app:
                result.mark_failure("❌ 未找到测试应用程序定义")
                return result

            result.add_detail("✅ 测试应用程序定义在返回列表中")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(test_app_path)

        return result

    def test_get_definition(self):
        """测试获取单个应用程序定义"""
        result = TestResult("测试获取单个应用程序定义")
        test_app_path = None

        try:
            app_id, test_app_path = self.create_test_app_definition()

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_success(result, response, ["definition"]):
                return result

            definition = response["result"]["definition"]
            if definition.get("appId") != app_id:
                result.mark_failure("❌ 返回的应用程序定义 ID 不匹配")
                return result

            result.add_detail("✅ 获取应用程序定义成功")
            result.add_detail(f"应用程序名称: {definition.get('displayName')}")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(test_app_path)

        return result

    def test_get_nonexistent_definition(self):
        """测试获取不存在的应用程序定义"""
        result = TestResult("测试获取不存在的应用程序定义")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", build_definition_identity_params("non-existent-app"))
            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32014,
                expected_message="app_definition_not_found"
            ):
                return result

            error_data = response.get("error", {}).get("data", {})
            if error_data and error_data.get("appId") != "non-existent-app":
                result.mark_failure(f"❌ 错误数据中 appId 不正确: {error_data}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_invalid_app_definition(self):
        """测试无效格式的应用程序定义文件"""
        result = TestResult("测试无效格式的应用程序定义文件")

        try:
            definitions_dir = get_definitions_dir()
            invalid_app_id = f"invalid-app-{uuid.uuid4().hex[:8]}"
            invalid_filename = build_definition_file_name(invalid_app_id)
            invalid_app_path = os.path.join(definitions_dir, invalid_filename)

            # 缺少 appId/displayName
            with open(invalid_app_path, "w", encoding="utf-8") as f:
                f.write('{"invalid_field": "value"}')

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions", {"scope": None})
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found_invalid_app = any(d.get("appId") == invalid_app_id for d in definitions)
            if found_invalid_app:
                result.mark_failure("❌ DevHub 错误地加载了无效定义")
                return result

            result.add_detail("✅ DevHub 正确忽略了无效定义")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(locals().get("invalid_app_path"))

        return result

    def test_app_definition_appid_format_validation(self):
        """测试应用程序定义 appId 格式验证"""
        result = TestResult("测试应用程序定义 appId 格式验证")

        try:
            definitions_dir = get_definitions_dir()
            invalid_app_id = f"invalid app id {uuid.uuid4().hex[:6]}"
            invalid_app_path = os.path.join(definitions_dir, build_definition_file_name(invalid_app_id))
            invalid_app = build_app_definition(
                invalid_app_id,
                display_name="Invalid AppId Application",
            )

            with open(invalid_app_path, "w", encoding="utf-8") as f:
                json.dump(invalid_app, f, ensure_ascii=False, indent=2)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions", {"scope": None})
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found_invalid_app = any(d.get("appId") == invalid_app_id for d in definitions)
            if found_invalid_app:
                result.mark_failure("❌ DevHub 错误地加载了 appId 格式无效的定义")
                return result

            result.add_detail("✅ DevHub 正确忽略 appId 格式无效定义")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(locals().get("invalid_app_path"))

        return result

    def test_definition_filename_must_match_appid(self):
        """测试文件名与 appId 不一致时应被忽略"""
        result = TestResult("测试文件名与 appId 不一致时应被忽略")

        try:
            definitions_dir = get_definitions_dir()
            mismatch_path = os.path.join(definitions_dir, f"mismatch-name-{uuid.uuid4().hex[:8]}.json")
            real_app_id = f"real-app-id-{uuid.uuid4().hex[:8]}"
            mismatch_app = build_app_definition(
                real_app_id,
                display_name="Mismatch Name Application",
            )

            with open(mismatch_path, "w", encoding="utf-8") as f:
                json.dump(mismatch_app, f, ensure_ascii=False, indent=2)

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.call("hub.apps.listDefinitions", {"scope": None})
            if not RpcAssertions.expect_success(result, response, ["definitions"]):
                return result

            definitions = response["result"]["definitions"]
            found = any(d.get("appId") == real_app_id for d in definitions)
            if found:
                result.mark_failure("❌ 文件名与 appId 不一致的定义不应被加载")
                return result

            result.add_detail("✅ 文件名与 appId 不一致的定义被正确忽略")
            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            safe_remove(locals().get("mismatch_path"))

        return result

    def test_validate_definition(self):
        """测试 validateDefinition 返回结构化校验结果"""
        result = TestResult("测试 validateDefinition 返回结构化校验结果")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            valid_app_id = self._new_app_id("validate-app")
            valid_response = client.call("hub.apps.validateDefinition", {
                "definition": {
                    "appId": valid_app_id,
                    "scope": "",
                    "displayName": "Validate App",
                    "launch": {
                        "exePath": "echo",
                        "argsTemplate": "hello"
                    }
                }
            })
            if not RpcAssertions.expect_success(result, valid_response, ["valid", "errors"]):
                return result

            if valid_response["result"].get("valid") is not True or valid_response["result"].get("errors") != []:
                result.mark_failure(f"❌ 合法定义校验结果不正确: {valid_response}")
                return result

            blank_launch_app_id = self._new_app_id("blank-launch-validate")
            blank_launch_response = client.call("hub.apps.validateDefinition", {
                "definition": {
                    "appId": blank_launch_app_id,
                    "scope": "",
                    "displayName": "Blank Launch Validate App",
                    "launch": {
                        "exePath": "   ",
                        "argsTemplate": "hello"
                    }
                }
            })
            if not RpcAssertions.expect_success(result, blank_launch_response, ["valid", "errors"]):
                return result

            if blank_launch_response["result"].get("valid") is not True or blank_launch_response["result"].get("errors") != []:
                result.mark_failure(f"❌ 空白 launch.exePath 应在定义校验阶段通过: {blank_launch_response}")
                return result

            invalid_response = client.call("hub.apps.validateDefinition", {
                "definition": {
                    "appId": "Invalid App",
                    "scope": "",
                }
            })
            if not RpcAssertions.expect_success(result, invalid_response, ["valid", "errors"]):
                return result

            if invalid_response["result"].get("valid") is not False:
                result.mark_failure(f"❌ 非法定义应返回 valid=false: {invalid_response}")
                return result

            errors = invalid_response["result"].get("errors", [])
            if not any(issue.get("path") == "definition.appId" for issue in errors):
                result.mark_failure(f"❌ 非法定义缺少 appId 校验问题: {invalid_response}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_blank_launch_exepath_should_store_definition(self):
        """测试空白 launch.exePath 可通过 upsertDefinition 持久化"""
        result = TestResult("测试空白 launch.exePath 可持久化")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            app_id = self._new_app_id("blank-launch-store")

            upsert_response = client.call("hub.apps.upsertDefinition", {
                "definition": {
                    "appId": app_id,
                    "scope": "",
                    "displayName": "Blank Launch Store App",
                    "launch": {
                        "exePath": "   ",
                        "argsTemplate": "managed"
                    }
                }
            })
            if not RpcAssertions.expect_success(result, upsert_response, ["definition"]):
                return result

            definition = upsert_response["result"]["definition"]
            if definition.get("launch", {}).get("exePath") != "   ":
                result.mark_failure(f"❌ upsertDefinition 未保留空白 launch.exePath: {definition}")
                return result

            get_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_success(result, get_response, ["definition"]):
                return result

            stored_definition = get_response["result"]["definition"]
            if stored_definition.get("launch", {}).get("exePath") != "   ":
                result.mark_failure(f"❌ getDefinition 未返回空白 launch.exePath: {stored_definition}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "client" in locals() and "app_id" in locals():
                    client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id))
            except Exception:
                pass

        return result

    def test_upsert_definition_and_delete_definition(self):
        """测试 upsertDefinition / getDefinition / deleteDefinition 完整生命周期"""
        result = TestResult("测试 upsertDefinition / getDefinition / deleteDefinition 完整生命周期")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            app_id = self._new_app_id("managed-app")

            upsert_response = client.call("hub.apps.upsertDefinition", {
                "definition": {
                    "appId": app_id,
                    "scope": "",
                    "displayName": "Managed App",
                    "description": "Managed from blackbox test",
                    "launch": {
                        "exePath": "echo",
                        "argsTemplate": "managed"
                    }
                }
            })
            if not RpcAssertions.expect_success(result, upsert_response, ["definition"]):
                return result

            definition = upsert_response["result"]["definition"]
            if definition.get("appId") != app_id:
                result.mark_failure(f"❌ upsert 返回定义 appId 不匹配: {definition}")
                return result

            get_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_success(result, get_response, ["definition"]):
                return result

            if get_response["result"]["definition"].get("description") != "Managed from blackbox test":
                result.mark_failure(f"❌ getDefinition 未返回最新定义: {get_response}")
                return result

            delete_response = client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_success(result, delete_response):
                return result

            get_missing_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_error(result, get_missing_response, -32014, "app_definition_not_found"):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "client" in locals() and "app_id" in locals():
                    client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id))
            except Exception:
                pass

        return result

    def test_scoped_definitions_should_use_composite_identity(self):
        """测试同一 appId 的多份 scoped Definition 可并存并按复合键读删"""
        result = TestResult("测试 AppDefinition 复合身份读删")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            app_id = self._new_app_id("managed-scoped-app")

            for scope, display_name, description in [
                ("", "Managed Scoped App Global", "global-definition"),
                ("workspace-a", "Managed Scoped App Workspace A", "workspace-a-definition"),
            ]:
                upsert_response = client.call("hub.apps.upsertDefinition", {
                    "definition": {
                        "appId": app_id,
                        "scope": scope,
                        "displayName": display_name,
                        "description": description,
                    }
                })
                if not RpcAssertions.expect_success(result, upsert_response, ["definition"]):
                    return result

            list_response = client.call("hub.apps.listDefinitions", {"scope": None})
            if not RpcAssertions.expect_success(result, list_response, ["definitions"]):
                return result

            matching_definitions = [
                definition
                for definition in list_response["result"]["definitions"]
                if definition.get("appId") == app_id
            ]
            if len(matching_definitions) != 2:
                result.mark_failure(f"❌ 同一 appId 的 scoped Definition 未全部返回: {matching_definitions}")
                return result

            scopes = {definition.get("scope") for definition in matching_definitions}
            if scopes != {"", "workspace-a"}:
                result.mark_failure(f"❌ Definition scope 集合不正确: {matching_definitions}")
                return result

            global_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id, ""))
            if not RpcAssertions.expect_success(result, global_response, ["definition"]):
                return result

            scoped_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id, "workspace-a"))
            if not RpcAssertions.expect_success(result, scoped_response, ["definition"]):
                return result

            global_definition = global_response["result"]["definition"]
            scoped_definition = scoped_response["result"]["definition"]
            if global_definition.get("description") != "global-definition":
                result.mark_failure(f"❌ Global Definition 读取结果不正确: {global_definition}")
                return result
            if scoped_definition.get("description") != "workspace-a-definition":
                result.mark_failure(f"❌ Scoped Definition 读取结果不正确: {scoped_definition}")
                return result

            delete_global_response = client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id, ""))
            if not RpcAssertions.expect_success(result, delete_global_response):
                return result

            get_deleted_global_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id, ""))
            if not RpcAssertions.expect_error(result, get_deleted_global_response, -32014, "app_definition_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, get_deleted_global_response, {"appId": app_id, "scope": ""}):
                return result

            scoped_after_delete_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id, "workspace-a"))
            if not RpcAssertions.expect_success(result, scoped_after_delete_response, ["definition"]):
                return result

            remaining_definition = scoped_after_delete_response["result"]["definition"]
            if remaining_definition.get("scope") != "workspace-a":
                result.mark_failure(f"❌ 删除 Global Definition 后 scoped Definition 不应丢失: {remaining_definition}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "client" in locals() and "app_id" in locals():
                    client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id, ""))
                    client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id, "workspace-a"))
            except Exception:
                pass

        return result

    def test_explicit_global_scope_should_use_scope_global_filename_and_echo_canonical_identifiers(self):
        """测试 scope='global' 使用独立 scopeKey 且按原值回显"""
        result = TestResult("测试 scope='global' 文件名与 canonical 回显")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            app_id = f"Sample.App_{uuid.uuid4().hex[:8]}"

            upsert_response = client.call("hub.apps.upsertDefinition", {
                "definition": {
                    "appId": app_id,
                    "scope": "global",
                    "displayName": "Explicit Global App",
                }
            })
            if not RpcAssertions.expect_success(result, upsert_response, ["definition"]):
                return result

            definition = upsert_response["result"]["definition"]
            if definition.get("appId") != app_id or definition.get("scope") != "global":
                result.mark_failure(f"❌ upsertDefinition 未按原值回显 canonical 标识符: {definition}")
                return result

            explicit_global_path = os.path.join(get_definitions_dir(), build_definition_file_name(app_id, "global"))
            default_global_path = os.path.join(get_definitions_dir(), build_definition_file_name(app_id, ""))
            if not os.path.exists(explicit_global_path):
                result.mark_failure(f"❌ 未生成 scope='global' 的独立 Definition 文件: {explicit_global_path}")
                return result
            if os.path.exists(default_global_path):
                result.mark_failure(f"❌ scope='global' 错误覆盖了 Global Definition 文件: {default_global_path}")
                return result

            get_response = client.call("hub.apps.getDefinition", build_definition_identity_params(app_id, "global"))
            if not RpcAssertions.expect_success(result, get_response, ["definition"]):
                return result
            if get_response["result"]["definition"].get("scope") != "global":
                result.mark_failure(f"❌ getDefinition 未回显 scope='global': {get_response}")
                return result

            list_global_response = client.call("hub.apps.listDefinitions", {"appId": app_id, "scope": ""})
            if not RpcAssertions.expect_success(result, list_global_response, ["definitions"]):
                return result
            if list_global_response["result"]["definitions"]:
                result.mark_failure(f"❌ Global 过滤错误命中了 scope='global' 定义: {list_global_response}")
                return result

            list_explicit_response = client.call("hub.apps.listDefinitions", {"appId": app_id, "scope": "global"})
            if not RpcAssertions.expect_success(result, list_explicit_response, ["definitions"]):
                return result
            explicit_definitions = list_explicit_response["result"]["definitions"]
            if len(explicit_definitions) != 1 or explicit_definitions[0].get("scope") != "global":
                result.mark_failure(f"❌ scope='global' 过滤结果不正确: {list_explicit_response}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "client" in locals() and "app_id" in locals():
                    client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id, "global"))
                    safe_remove(os.path.join(get_definitions_dir(), build_definition_file_name(app_id, "")))
            except Exception:
                pass

        return result

    def test_upsert_invalid_definition(self):
        """测试 upsertDefinition 对非法定义返回 definition_invalid"""
        result = TestResult("测试 upsertDefinition 对非法定义返回 definition_invalid")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.upsertDefinition", {
                "definition": {
                    "appId": "Invalid App",
                    "scope": "",
                    "displayName": ""
                }
            })
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "definition_invalid"}):
                return result

            errors = response.get("error", {}).get("data", {}).get("errors", [])
            if not any(issue.get("path") == "definition.appId" for issue in errors):
                result.mark_failure(f"❌ definition_invalid 缺少 appId 错误项: {response}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_register_instance_should_allow_undeclared_scope_for_definition_managed_app(self):
        """测试 Definition 管理下的 appId 仍可自主注册到未声明 scope"""
        result = TestResult("测试 Definition 管理 appId 允许未声明 scope 自主注册")
        definition_path = None
        instance_id = self._new_app_id("managed-scope-instance")

        try:
            app_id = self._new_app_id("managed-scope-app")
            definition_path = write_app_definition(
                app_id,
                scope="workspace-a",
                display_name="Managed Scope App",
            )

            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            response = client.register_instance(
                instance_id=instance_id,
                app_id=app_id,
                scope="workspace-b",
                poll=True,
                respond=True,
                pid=33001,
            )

            if not RpcAssertions.expect_success(result, response, ["instance"]):
                return result

            instance = response["result"]["instance"]
            if instance.get("appId") != app_id or instance.get("scope") != "workspace-b":
                result.mark_failure(f"❌ registerInstance 返回的实例信息不正确: {instance}")
                return result

            list_response = client.call("hub.apps.listInstances", {
                "appId": app_id,
                "scope": None,
                "includeOffline": True,
            })
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            instances = list_response["result"]["instances"]
            if not any(
                candidate.get("instanceId") == instance_id and candidate.get("scope") == "workspace-b"
                for candidate in instances
            ):
                result.mark_failure(f"❌ listInstances 未返回未声明 scope 的自主注册实例: {instances}")
                return result

            get_response = client.call("hub.apps.getInstance", {"instanceId": instance_id})
            if not RpcAssertions.expect_success(result, get_response, ["instance"]):
                return result

            fetched_instance = get_response["result"]["instance"]
            if fetched_instance.get("appId") != app_id or fetched_instance.get("scope") != "workspace-b":
                result.mark_failure(f"❌ getInstance 未返回正确的未声明 scope 实例: {fetched_instance}")
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                if "client" in locals():
                    client.unregister_instance(instance_id)
            except Exception:
                pass

            safe_remove(definition_path)

        return result

    def test_delete_nonexistent_definition(self):
        """测试 deleteDefinition 删除不存在定义时返回 app_definition_not_found"""
        result = TestResult("测试 deleteDefinition 删除不存在定义时返回 app_definition_not_found")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            app_id = self._new_app_id("missing-delete-app")

            response = client.call("hub.apps.deleteDefinition", build_definition_identity_params(app_id))
            if not RpcAssertions.expect_error(result, response, -32014, "app_definition_not_found"):
                return result
            if not RpcAssertions.expect_error_data_fields(result, response, {"appId": app_id}):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
        """运行所有 AppDefinition 测试"""
        return [
            self.test_list_definitions(),
            self.test_get_definition(),
            self.test_get_nonexistent_definition(),
            self.test_invalid_app_definition(),
            self.test_app_definition_appid_format_validation(),
            self.test_definition_filename_must_match_appid(),
            self.test_validate_definition(),
            self.test_blank_launch_exepath_should_store_definition(),
            self.test_upsert_definition_and_delete_definition(),
            self.test_scoped_definitions_should_use_composite_identity(),
            self.test_explicit_global_scope_should_use_scope_global_filename_and_echo_canonical_identifiers(),
            self.test_upsert_invalid_definition(),
            self.test_register_instance_should_allow_undeclared_scope_for_definition_managed_app(),
            self.test_delete_nonexistent_definition()
        ]


if __name__ == "__main__":
    # 运行测试
    test = TestAppDefinitions()
    results = test.run_all_tests()

    # 输出结果
    for result in results:
        status = "✅ 通过" if result.success else "❌ 失败"
        print(f"{status}: {result.test_name}")

        if result.details:
            for detail in result.details:
                print(f"  - {detail}")

        if result.error_message:
            print(f"  错误: {result.error_message}")

        print()
