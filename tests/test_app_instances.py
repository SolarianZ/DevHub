#!/usr/bin/env python3
"""
DevHub M1 AppInstance 测试
"""

import os
import sys
import time
import uuid
import unittest

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestAppInstances(unittest.TestCase):
    """AppInstance 测试类"""

    @staticmethod
    def _wait_with_progress(total_seconds, label):
        """等待并输出终端进度条。"""
        # 避免终端长时间无反应，测试人员误以为卡死
        bar_width = 30
        for elapsed in range(total_seconds):
            completed = elapsed + 1
            ratio = completed / total_seconds
            filled = int(bar_width * ratio)
            bar = "=" * filled + "-" * (bar_width - filled)
            print(f"\r{label} [{bar}] {completed}/{total_seconds}s", end="", flush=True)
            time.sleep(1)
        print()

    def generate_unique_instance_id(self):
        """生成唯一的实例 ID"""
        return f"test-instance-{uuid.uuid4().hex[:10]}"

    def _cleanup_test_instances(self, app_ids):
        """清理测试实例"""
        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            list_response = client.call("hub.apps.listInstances", {"includeAllScopes": True, "includeOffline": True})
            if "result" not in list_response:
                return
            instances = list_response["result"].get("instances", [])
            for instance in instances:
                if instance.get("appId") in app_ids:
                    client.call("hub.apps.unregisterInstance", {"instanceId": instance.get("instanceId")})
        except Exception:
            pass

    def _validate_app_instance_fields(self, result, instance):
        """验证 AppInstance 包含必备字段"""
        required_fields = ["instanceId", "appId", "pid", "registeredAtUtc", "lastSeenUtc", "invoke"]
        for field in required_fields:
            if field not in instance:
                result.mark_failure(f"❌ 实例缺少必备字段: {field}")
                return False

        import re
        rfc3339_pattern = r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z'
        if not re.match(rfc3339_pattern, instance["registeredAtUtc"]):
            result.mark_failure(f"❌ registeredAtUtc 格式不符合 RFC3339: {instance['registeredAtUtc']}")
            return False

        if not re.match(rfc3339_pattern, instance["lastSeenUtc"]):
            result.mark_failure(f"❌ lastSeenUtc 格式不符合 RFC3339: {instance['lastSeenUtc']}")
            return False

        invoke = instance.get("invoke", {})
        if "poll" not in invoke or "respond" not in invoke:
            result.mark_failure("❌ invoke 字段缺少 poll/respond")
            return False

        if not isinstance(invoke["poll"], bool) or not isinstance(invoke["respond"], bool):
            result.mark_failure("❌ invoke.poll/respond 必须为布尔值")
            return False

        if not isinstance(instance["pid"], int) or instance["pid"] < 1:
            result.mark_failure(f"❌ pid 必须是正整数: {instance['pid']}")
            return False

        return True

    def test_register_and_list_instances(self):
        """测试注册实例并列出实例"""
        result = TestResult("测试注册实例并列出实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-m1-core",
                    "scope": None,
                    "pid": 12345,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            instance = register_response["result"]["instance"]
            if not self._validate_app_instance_fields(result, instance):
                return result

            list_response = client.call("hub.apps.listInstances")
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            instances = list_response["result"]["instances"]
            found = any(inst.get("instanceId") == instance_id for inst in instances)
            if not found:
                result.mark_failure("❌ 注册的实例未在列表中找到")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-m1-core"])

        return result

    def test_register_unknown_appid_is_allowed(self):
        """测试未知 appId 允许注册（M1 要求）"""
        result = TestResult("测试未知 appId 允许注册")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()
            unknown_app_id = "app-not-in-definitions"

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": unknown_app_id,
                    "scope": None,
                    "pid": 12346,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            if not RpcAssertions.expect_success(result, response, ["instance"]):
                return result

            if response["result"]["instance"].get("appId") != unknown_app_id:
                result.mark_failure("❌ 返回实例 appId 与请求不一致")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["app-not-in-definitions"])

        return result

    def test_register_upsert_refresh_last_seen(self):
        """测试同一 instanceId 二次注册触发 upsert 并更新 lastSeen"""
        result = TestResult("测试同一 instanceId 二次注册触发 upsert 并更新 lastSeen")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response1 = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-upsert",
                    "scope": None,
                    "pid": 12347,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, response1, ["instance"]):
                return result

            last_seen_1 = response1["result"]["instance"]["lastSeenUtc"]
            time.sleep(1)

            response2 = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-upsert",
                    "scope": "scope-upsert",
                    "pid": 12348,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, response2, ["instance"]):
                return result

            instance2 = response2["result"]["instance"]
            last_seen_2 = instance2["lastSeenUtc"]

            if last_seen_2 <= last_seen_1:
                result.mark_failure("❌ 二次注册未更新 lastSeenUtc")
                return result

            if instance2.get("scope") != "scope-upsert":
                result.mark_failure("❌ upsert 后 scope 未更新")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-upsert"])

        return result

    def test_heartbeat_updates_last_seen(self):
        """测试心跳更新最后一次见过时间"""
        result = TestResult("测试心跳更新最后一次见过时间")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-heartbeat",
                    "scope": None,
                    "pid": 12349,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            heartbeat1_response = client.call("hub.apps.heartbeat", {"instanceId": instance_id})
            if not RpcAssertions.expect_success(result, heartbeat1_response, ["lastSeenUtc"]):
                return result

            last_seen_1 = heartbeat1_response["result"]["lastSeenUtc"]
            time.sleep(1)

            heartbeat2_response = client.call("hub.apps.heartbeat", {"instanceId": instance_id})
            if not RpcAssertions.expect_success(result, heartbeat2_response, ["lastSeenUtc"]):
                return result

            last_seen_2 = heartbeat2_response["result"]["lastSeenUtc"]
            if last_seen_2 <= last_seen_1:
                result.mark_failure("❌ 第二次心跳未更新 lastSeenUtc")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-heartbeat"])

        return result

    def test_heartbeat_nonexistent_instance(self):
        """测试对不存在实例发送心跳"""
        result = TestResult("测试对不存在实例发送心跳")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            nonexistent_id = f"nonexistent-{self.generate_unique_instance_id()}"

            response = client.call("hub.apps.heartbeat", {"instanceId": nonexistent_id})
            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32010,
                expected_message="instance_not_found"
            ):
                return result

            if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "unknown_instance"}):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_unregister_instance(self):
        """测试注销实例"""
        result = TestResult("测试注销实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-unregister",
                    "scope": None,
                    "pid": 12350,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            unregister_response = client.call("hub.apps.unregisterInstance", {"instanceId": instance_id})
            if not RpcAssertions.expect_success(result, unregister_response):
                return result

            list_response = client.call("hub.apps.listInstances", {"includeAllScopes": True, "includeOffline": True})
            if not RpcAssertions.expect_success(result, list_response, ["instances"]):
                return result

            found = any(inst.get("instanceId") == instance_id for inst in list_response["result"]["instances"])
            if found:
                result.mark_failure("❌ 注销后实例仍在列表中")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-unregister"])

        return result

    def test_unregister_nonexistent_instance(self):
        """测试注销不存在实例（幂等）"""
        result = TestResult("测试注销不存在实例（幂等）")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            nonexistent_id = f"nonexistent-{self.generate_unique_instance_id()}"

            response = client.call("hub.apps.unregisterInstance", {"instanceId": nonexistent_id})
            if not RpcAssertions.expect_success(result, response):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_list_instances_with_params(self):
        """测试 listInstances 的 appId/scope/includeAllScopes 参数"""
        result = TestResult("测试 listInstances 的 appId/scope/includeAllScopes 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id_1 = self.generate_unique_instance_id()
            instance_id_2 = self.generate_unique_instance_id()

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_1,
                    "appId": "test-app-list-1",
                    "scope": None,
                    "pid": 12351,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_2,
                    "appId": "test-app-list-2",
                    "scope": "scope-x",
                    "pid": 12352,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            response_by_app = client.call("hub.apps.listInstances", {"appId": "test-app-list-1"})
            if not RpcAssertions.expect_success(result, response_by_app, ["instances"]):
                return result

            by_app = response_by_app["result"]["instances"]
            if not any(inst.get("instanceId") == instance_id_1 for inst in by_app):
                result.mark_failure("❌ 按 appId 过滤未返回目标实例")
                return result
            if any(inst.get("instanceId") == instance_id_2 for inst in by_app):
                result.mark_failure("❌ 按 appId 过滤错误返回了其他实例")
                return result

            response_by_scope = client.call("hub.apps.listInstances", {"scope": "scope-x"})
            if not RpcAssertions.expect_success(result, response_by_scope, ["instances"]):
                return result

            by_scope = response_by_scope["result"]["instances"]
            if not any(inst.get("instanceId") == instance_id_2 for inst in by_scope):
                result.mark_failure("❌ 按 scope 过滤未返回目标实例")
                return result

            response_all = client.call("hub.apps.listInstances", {"scope": "invalid", "includeAllScopes": True})
            if not RpcAssertions.expect_success(result, response_all, ["instances"]):
                return result

            all_instances = response_all["result"]["instances"]
            if not any(inst.get("instanceId") == instance_id_1 for inst in all_instances):
                result.mark_failure("❌ includeAllScopes=true 未包含 global 实例")
                return result
            if not any(inst.get("instanceId") == instance_id_2 for inst in all_instances):
                result.mark_failure("❌ includeAllScopes=true 未包含 scoped 实例")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-list-1", "test-app-list-2"])

        return result

    def test_list_instances_default_global_scope(self):
        """测试 listInstances 默认仅返回 global(scope=null)"""
        result = TestResult("测试 listInstances 默认仅返回 global(scope=null)")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id_global = self.generate_unique_instance_id()
            instance_id_scoped = self.generate_unique_instance_id()

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_global,
                    "appId": "test-app-default-scope",
                    "scope": None,
                    "pid": 12353,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_scoped,
                    "appId": "test-app-default-scope",
                    "scope": "workspace-a",
                    "pid": 12354,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            response = client.call("hub.apps.listInstances", {"appId": "test-app-default-scope"})
            if not RpcAssertions.expect_success(result, response, ["instances"]):
                return result

            instances = response["result"]["instances"]
            has_global = any(inst.get("instanceId") == instance_id_global for inst in instances)
            has_scoped = any(inst.get("instanceId") == instance_id_scoped for inst in instances)

            if not has_global:
                result.mark_failure("❌ 默认过滤未返回 global 实例")
                return result
            if has_scoped:
                result.mark_failure("❌ 默认过滤错误返回了 scoped 实例")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-default-scope"])

        return result

    def test_register_instance_with_global_scope(self):
        """测试注册 scope='global' 被拒绝"""
        result = TestResult("测试注册 scope='global' 被拒绝")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-invalid-scope",
                    "scope": "global",
                    "pid": 12355,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32602,
                expected_message="invalid_params"
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_register_instance_empty_scope(self):
        """测试注册 scope='' 被拒绝"""
        result = TestResult("测试注册 scope='' 被拒绝")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-invalid-scope",
                    "scope": "",
                    "pid": 12356,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            if not RpcAssertions.expect_error(
                result,
                response,
                expected_code=-32602,
                expected_message="invalid_params"
            ):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_list_instances_scope_strict_match(self):
        """测试 scope 严格匹配（不回退 global）"""
        result = TestResult("测试 scope 严格匹配（不回退 global）")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id_1 = self.generate_unique_instance_id()
            instance_id_2 = self.generate_unique_instance_id()

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_1,
                    "appId": "test-app-scope-match",
                    "scope": "scope-1",
                    "pid": 12357,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id_2,
                    "appId": "test-app-scope-match",
                    "scope": None,
                    "pid": 12358,
                    "invoke": {"poll": True, "respond": True}
                }
            })

            response = client.call("hub.apps.listInstances", {"appId": "test-app-scope-match", "scope": "scope-1"})
            if not RpcAssertions.expect_success(result, response, ["instances"]):
                return result

            instances = response["result"]["instances"]
            has_scope_1 = any(inst.get("instanceId") == instance_id_1 for inst in instances)
            has_global = any(inst.get("instanceId") == instance_id_2 for inst in instances)

            if not has_scope_1:
                result.mark_failure("❌ scope 精确匹配未返回目标实例")
                return result
            if has_global:
                result.mark_failure("❌ scope 精确匹配错误回退到 global 实例")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-scope-match"])

        return result

    def test_register_instance_invoke_field_validation(self):
        """测试注册返回实例 invoke 字段结构"""
        result = TestResult("测试注册返回实例 invoke 字段结构")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-invoke",
                    "scope": None,
                    "pid": 12359,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, response, ["instance"]):
                return result

            instance = response["result"]["instance"]
            if not self._validate_app_instance_fields(result, instance):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-invoke"])

        return result

    def test_instance_offline_after_30s_no_heartbeat(self):
        """测试 30s 无心跳后实例离线"""
        result = TestResult("测试 30s 无心跳后实例离线")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-offline",
                    "scope": None,
                    "pid": 12360,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            list_before = client.call("hub.apps.listInstances", {"appId": "test-app-offline"})
            if not RpcAssertions.expect_success(result, list_before, ["instances"]):
                return result
            if not any(inst.get("instanceId") == instance_id for inst in list_before["result"]["instances"]):
                result.mark_failure("❌ 注册后实例未在线显示")
                return result

            wait_seconds = 35
            result.add_detail(f"⏳ 等待 {wait_seconds}s 触发离线")
            self._wait_with_progress(wait_seconds, "离线判定等待中")

            list_after = client.call("hub.apps.listInstances", {"appId": "test-app-offline"})
            if not RpcAssertions.expect_success(result, list_after, ["instances"]):
                return result

            if any(inst.get("instanceId") == instance_id for inst in list_after["result"]["instances"]):
                result.mark_failure("❌ 超过30s无心跳后实例仍在线")
                return result

            list_after_explicit = client.call("hub.apps.listInstances", {
                "appId": "test-app-offline",
                "includeOffline": False
            })
            if not RpcAssertions.expect_success(result, list_after_explicit, ["instances"]):
                return result

            if any(inst.get("instanceId") == instance_id for inst in list_after_explicit["result"]["instances"]):
                result.mark_failure("❌ includeOffline=false 显式查询不应返回离线实例")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-offline"])

        return result

    def test_list_instances_include_offline(self):
        """测试 includeOffline=true 返回离线实例"""
        result = TestResult("测试 includeOffline=true 返回离线实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-offline-include",
                    "scope": None,
                    "pid": 12361,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if not RpcAssertions.expect_success(result, register_response, ["instance"]):
                return result

            wait_seconds = 35
            result.add_detail(f"⏳ 等待 {wait_seconds}s 触发离线")
            self._wait_with_progress(wait_seconds, "离线实例等待中")

            list_default = client.call("hub.apps.listInstances", {"appId": "test-app-offline-include"})
            if not RpcAssertions.expect_success(result, list_default, ["instances"]):
                return result
            if any(inst.get("instanceId") == instance_id for inst in list_default["result"]["instances"]):
                result.mark_failure("❌ 默认查询不应返回离线实例")
                return result

            list_with_offline = client.call("hub.apps.listInstances", {
                "appId": "test-app-offline-include",
                "includeOffline": True
            })
            if not RpcAssertions.expect_success(result, list_with_offline, ["instances"]):
                return result
            if not any(inst.get("instanceId") == instance_id for inst in list_with_offline["result"]["instances"]):
                result.mark_failure("❌ includeOffline=true 未返回离线实例")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            self._cleanup_test_instances(["test-app-offline-include"])

        return result

    def run_all_tests(self, full=False, run_timeout_tests=True):
        """运行所有 AppInstance 测试"""
        tests = [
            self.test_register_and_list_instances,
            self.test_register_unknown_appid_is_allowed,
            self.test_register_upsert_refresh_last_seen,
            self.test_heartbeat_updates_last_seen,
            self.test_heartbeat_nonexistent_instance,
            self.test_unregister_instance,
            self.test_unregister_nonexistent_instance,
            self.test_list_instances_with_params,
            self.test_list_instances_default_global_scope,
            self.test_register_instance_with_global_scope,
            self.test_register_instance_empty_scope,
            self.test_list_instances_scope_strict_match,
            self.test_register_instance_invoke_field_validation
        ]

        if run_timeout_tests:
            tests.append(self.test_instance_offline_after_30s_no_heartbeat)

        if full:
            tests.extend([
                self.test_list_instances_include_offline
            ])

        return [test() for test in tests]


if __name__ == "__main__":
    # 运行测试
    test = TestAppInstances()
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
