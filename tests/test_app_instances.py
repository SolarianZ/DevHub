#!/usr/bin/env python3
"""
DevHub M1 AppInstance 测试
"""

import os
import sys
import unittest
import time

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestAppInstances(unittest.TestCase):
    """AppInstance 测试类"""

    def generate_unique_instance_id(self):
        """生成唯一的实例 ID"""
        import uuid
        return f"test-instance-{uuid.uuid4().hex[:8]}"

    def test_register_and_list_instances(self):
        """测试注册实例并列出实例"""
        result = TestResult("测试注册实例并列出实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            # 注册实例
            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            if "result" in register_response and register_response["result"].get("ok") and "instance" in register_response["result"]:
                result.add_detail("✅ 实例注册成功")

                # 验证返回的实例包含所有必备字段
                instance = register_response["result"]["instance"]
                if not self._validate_app_instance_fields(result, instance):
                    return result

                # 列出实例
                list_response = client.call("hub.apps.listInstances")

                if "result" in list_response and list_response["result"].get("ok") and "instances" in list_response["result"]:
                    instances = list_response["result"]["instances"]
                    found = any(inst.get("instanceId") == instance_id for inst in instances)

                    if found:
                        result.add_detail("✅ 实例在列表中可见")

                        # 验证列出的实例包含所有必备字段
                        for inst in instances:
                            if inst.get("instanceId") == instance_id:
                                if not self._validate_app_instance_fields(result, inst):
                                    return result

                        result.mark_success()
                    else:
                        result.mark_failure("❌ 注册的实例未在列表中找到")
                else:
                    result.mark_failure("❌ 列出实例响应格式不正确")
            else:
                result.mark_failure(f"❌ 实例注册失败: {register_response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_heartbeat_updates_last_seen(self):
        """测试心跳更新最后一次见过的时间"""
        result = TestResult("测试心跳更新最后一次见过的时间")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            # 注册实例
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            # 第一次心跳
            heartbeat1_response = client.call("hub.apps.heartbeat", {"instanceId": instance_id})

            if "result" in heartbeat1_response and heartbeat1_response["result"].get("ok") and "lastSeenUtc" in heartbeat1_response["result"]:
                last_seen1 = heartbeat1_response["result"]["lastSeenUtc"]
                result.add_detail(f"✅ 第一次心跳成功，最后见过时间: {last_seen1}")

                # 等待一段时间
                time.sleep(2)

                # 第二次心跳
                heartbeat2_response = client.call("hub.apps.heartbeat", {"instanceId": instance_id})

                if "result" in heartbeat2_response and heartbeat2_response["result"].get("ok") and "lastSeenUtc" in heartbeat2_response["result"]:
                    last_seen2 = heartbeat2_response["result"]["lastSeenUtc"]
                    result.add_detail(f"✅ 第二次心跳成功，最后见过时间: {last_seen2}")

                    # 验证时间已更新
                    if last_seen2 > last_seen1:
                        result.add_detail("✅ 最后见过时间已正确更新")
                        result.mark_success()
                    else:
                        result.mark_failure("❌ 最后见过时间未更新")
                else:
                    result.mark_failure("❌ 第二次心跳响应格式不正确")
            else:
                result.mark_failure("❌ 第一次心跳响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_unregister_nonexistent_instance(self):
        """测试注销不存在的实例（幂等性）"""
        result = TestResult("测试注销不存在的实例（幂等性）")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            nonexistent_instance_id = f"nonexistent-instance-{self.generate_unique_instance_id()}"

            response = client.call("hub.apps.unregisterInstance", {"instanceId": nonexistent_instance_id})

            if "result" in response and response["result"].get("ok"):
                result.add_detail("✅ 注销不存在的实例仍返回成功（幂等性）")
                result.mark_success()
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

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

            # 注册实例
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            # 注销实例
            unregister_response = client.call("hub.apps.unregisterInstance", {"instanceId": instance_id})

            if "result" in unregister_response and unregister_response["result"].get("ok"):
                result.add_detail("✅ 实例注销成功")

                # 验证实例不再列出
                list_response = client.call("hub.apps.listInstances")
                if "result" in list_response and list_response["result"].get("ok") and "instances" in list_response["result"]:
                    instances = list_response["result"]["instances"]
                    found = any(inst.get("instanceId") == instance_id for inst in instances)

                    if not found:
                        result.add_detail("✅ 实例已从列表中移除")
                        result.mark_success()
                    else:
                        result.mark_failure("❌ 实例仍然在列表中")
                else:
                    result.mark_failure("❌ 列出实例响应格式不正确")
            else:
                result.mark_failure(f"❌ 实例注销失败: {unregister_response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_instance_offline_after_30s_no_heartbeat(self):
        """测试超过30s不发送心跳，实例应变为离线状态"""
        result = TestResult("测试30s无心跳后实例变为离线")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            # 注册实例
            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            if "result" not in register_response:
                result.mark_failure(f"❌ 实例注册失败: {register_response.get('error', {})}")
                return result

            result.add_detail("✅ 实例注册成功")

            # 立即列出实例，应该能看到（在线）
            list_response = client.call("hub.apps.listInstances")
            if "result" in list_response and "instances" in list_response["result"]:
                instances = list_response["result"]["instances"]
                found_before = any(inst.get("instanceId") == instance_id for inst in instances)

                if found_before:
                    result.add_detail("✅ 注册后实例在线可见")
                else:
                    result.mark_failure("❌ 注册后实例未在线显示")
                    return result
            else:
                result.mark_failure("❌ 列出实例响应格式不正确")
                return result

            # 等待 30+ 秒（在线判定阈值）
            wait_seconds = 35
            result.add_detail(f"⏳ 等待 {wait_seconds} 秒，让实例超时离线...")

            # 显示进度条
            import sys
            progress_width = 40
            for i in range(wait_seconds + 1):
                if i > 0:
                    time.sleep(1)
                percent = int((i / wait_seconds) * 100)
                filled = int((i / wait_seconds) * progress_width)
                bar = "█" * filled + "░" * (progress_width - filled)
                sys.stdout.write(f"\r    [{bar}] {percent}% ({i}/{wait_seconds}s)")
                sys.stdout.flush()
            sys.stdout.write("\n")
            sys.stdout.flush()

            result.add_detail("✅ 等待完成，继续验证实例状态")

            # 再次列出实例，不应看到该实例（已离线）
            list_response2 = client.call("hub.apps.listInstances")
            if "result" in list_response2 and "instances" in list_response2["result"]:
                instances2 = list_response2["result"]["instances"]
                found_after = any(inst.get("instanceId") == instance_id for inst in instances2)

                if not found_after:
                    result.add_detail("✅ 超过30s无心跳后，实例已离线不可见")
                    result.mark_success()
                else:
                    result.mark_failure("❌ 超过30s无心跳后，实例仍然在线显示（不符合预期）")
            else:
                result.mark_failure("❌ 第二次列出实例响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_heartbeat_nonexistent_instance(self):
        """测试对不存在的实例发送心跳"""
        result = TestResult("测试对不存在的实例发送心跳")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            nonexistent_instance_id = f"nonexistent-instance-{self.generate_unique_instance_id()}"

            response = client.call("hub.apps.heartbeat", {"instanceId": nonexistent_instance_id})

            if "error" in response and response["error"]["code"] == -32010:
                if response["error"]["message"] == "instance_not_found":
                    if "data" in response["error"] and response["error"]["data"].get("reason") == "instance_not_found":
                        result.add_detail("✅ 正确返回实例不存在错误")
                        result.mark_success()
                    else:
                        result.mark_failure(f"❌ 错误数据中 reason 不正确: {response['error'].get('data', {})}")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_list_instances_with_params(self):
        """测试使用参数列出实例"""
        result = TestResult("测试使用参数列出实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id1 = self.generate_unique_instance_id()
            instance_id2 = self.generate_unique_instance_id()

            # 注册两个不同appId的实例
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id1,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id2,
                    "appId": "test-app-2",
                    "scope": "test-scope",
                    "pid": 67890,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            # 测试按appId过滤
            response_by_app = client.call("hub.apps.listInstances", {"appId": "test-app-1"})
            if "result" in response_by_app and "instances" in response_by_app["result"]:
                instances_by_app = response_by_app["result"]["instances"]
                found = any(inst.get("instanceId") == instance_id1 for inst in instances_by_app)
                not_found = any(inst.get("instanceId") == instance_id2 for inst in instances_by_app)

                if found and not not_found:
                    result.add_detail("✅ 按appId过滤测试成功")
                else:
                    result.mark_failure("❌ 按appId过滤测试失败")
                    return result

            # 测试按scope过滤
            response_by_scope = client.call("hub.apps.listInstances", {"scope": "test-scope"})
            if "result" in response_by_scope and "instances" in response_by_scope["result"]:
                instances_by_scope = response_by_scope["result"]["instances"]
                found = any(inst.get("instanceId") == instance_id2 for inst in instances_by_scope)
                not_found = any(inst.get("instanceId") == instance_id1 for inst in instances_by_scope)

                if found and not not_found:
                    result.add_detail("✅ 按scope过滤测试成功")
                else:
                    result.mark_failure("❌ 按scope过滤测试失败")
                    return result

            # 测试 includeAllScopes 参数（必须忽略 scope 参数）
            response_all_scopes = client.call("hub.apps.listInstances", {
                "scope": "invalid-scope",
                "includeAllScopes": True
            })
            if "result" in response_all_scopes and "instances" in response_all_scopes["result"]:
                instances_all_scopes = response_all_scopes["result"]["instances"]
                found1 = any(inst.get("instanceId") == instance_id1 for inst in instances_all_scopes)
                found2 = any(inst.get("instanceId") == instance_id2 for inst in instances_all_scopes)

                if found1 and found2:
                    result.add_detail("✅ includeAllScopes 参数测试成功：忽略 scope 参数，返回所有范围的实例")
                else:
                    result.mark_failure("❌ includeAllScopes 参数测试失败")
                    return result

            # 验证返回的实例包含所有必备字段
            for instance in instances_all_scopes:
                self._validate_app_instance_fields(result, instance)

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        finally:
            # 清理测试实例
            try:
                base_url, token = DiscoveryService.get_hub_info()
                client = RpcClient(base_url, token)
                # 这里不直接引用上面的变量，因为可能在异常情况下没有定义
                # 简单的清理方式是列出所有实例并尝试注销
                list_response = client.call("hub.apps.listInstances", {"includeAllScopes": True, "includeOffline": True})
                if "result" in list_response and list_response["result"].get("ok") and "instances" in list_response["result"]:
                    for instance in list_response["result"]["instances"]:
                        if instance.get("appId") in ["test-app-1", "test-app-2"]:
                            client.call("hub.apps.unregisterInstance", {"instanceId": instance.get("instanceId")})
            except:
                pass

        return result

    def _validate_app_instance_fields(self, result, instance):
        """验证 AppInstance 包含所有必备字段"""
        required_fields = ["instanceId", "appId", "pid", "registeredAtUtc", "lastSeenUtc", "invoke"]

        for field in required_fields:
            if field not in instance:
                result.mark_failure(f"❌ 实例缺少必备字段: {field}")
                return False

        # 验证时间格式是 RFC3339
        import re
        rfc3339_pattern = r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z'
        if not re.match(rfc3339_pattern, instance["registeredAtUtc"]):
            result.mark_failure(f"❌ registeredAtUtc 格式不符合 RFC3339: {instance['registeredAtUtc']}")
            return False

        if not re.match(rfc3339_pattern, instance["lastSeenUtc"]):
            result.mark_failure(f"❌ lastSeenUtc 格式不符合 RFC3339: {instance['lastSeenUtc']}")
            return False

        # 验证 invoke 字段包含 poll 和 respond 属性
        if "poll" not in instance["invoke"] or "respond" not in instance["invoke"]:
            result.mark_failure("❌ 实例的invoke字段缺少poll或respond属性")
            return False

        if not isinstance(instance["invoke"]["poll"], bool) or not isinstance(instance["invoke"]["respond"], bool):
            result.mark_failure("❌ poll或respond属性不是布尔值")
            return False

        # 验证 pid 是正整数
        if not isinstance(instance["pid"], int) or instance["pid"] < 1:
            result.mark_failure(f"❌ pid 必须是正整数: {instance['pid']}")
            return False

        return True

    def test_register_instance_with_global_scope(self):
        """测试注册 scope 为 \"global\" 的实例（禁止值）"""
        result = TestResult("测试注册 scope 为 \"global\" 的实例")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": "global",  # 禁止值
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            if "error" in response and response["error"]["code"] == -32002:
                if response["error"]["message"] == "forbidden":
                    if "data" in response["error"] and response["error"]["data"].get("reason") == "scope_policy_violation":
                        result.add_detail("✅ 正确返回 -32002 forbidden 错误，reason 为 scope_policy_violation")
                        result.mark_success()
                    else:
                        result.mark_failure(f"❌ 错误数据中 reason 不正确: {response['error'].get('data', {})}")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_list_instances_scope_strict_match(self):
        """测试 listInstances 的 scope 严格匹配逻辑（无 fallback）"""
        result = TestResult("测试 scope 严格匹配逻辑")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id1 = self.generate_unique_instance_id()
            instance_id2 = self.generate_unique_instance_id()

            # 注册两个不同 scope 的实例
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id1,
                    "appId": "test-app-1",
                    "scope": "scope1",
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id2,
                    "appId": "test-app-1",
                    "scope": "scope2",
                    "pid": 67890,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            # 测试按 scope1 过滤，应该只返回 scope1 的实例
            response = client.call("hub.apps.listInstances", {"scope": "scope1"})
            if "result" in response and "instances" in response["result"]:
                instances = response["result"]["instances"]

                found_scope1 = any(inst.get("instanceId") == instance_id1 for inst in instances)
                found_scope2 = any(inst.get("instanceId") == instance_id2 for inst in instances)

                if found_scope1 and not found_scope2:
                    result.add_detail("✅ Scope 严格匹配测试成功：只返回了 scope1 的实例")
                else:
                    result.mark_failure(f"❌ Scope 严格匹配测试失败：找到 scope1={found_scope1}, scope2={found_scope2}")

                # 验证返回的实例包含所有必备字段
                for instance in instances:
                    self._validate_app_instance_fields(result, instance)

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试实例
            try:
                base_url, token = DiscoveryService.get_hub_info()
                client = RpcClient(base_url, token)
                list_response = client.call("hub.apps.listInstances", {"includeAllScopes": True, "includeOffline": True})
                if "result" in list_response and list_response["result"].get("ok") and "instances" in list_response["result"]:
                    for instance in list_response["result"]["instances"]:
                        if instance.get("appId") == "test-app-1" and instance.get("scope") in ["scope1", "scope2"]:
                            client.call("hub.apps.unregisterInstance", {"instanceId": instance.get("instanceId")})
            except:
                pass

        return result

    def test_register_instance_invoke_field_validation(self):
        """测试注册实例时验证invoke字段包含poll和respond属性"""
        result = TestResult("测试验证AppInstance的invoke字段")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            # 注册实例
            register_response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            if "result" in register_response and register_response["result"].get("ok") and "instance" in register_response["result"]:
                instance = register_response["result"]["instance"]

                # 验证invoke字段存在且包含poll和respond属性
                if "invoke" in instance:
                    invoke = instance["invoke"]
                    if "poll" in invoke and "respond" in invoke:
                        result.add_detail("✅ 实例的invoke字段包含poll和respond属性")

                        # 验证poll和respond属性是布尔值
                        if isinstance(invoke["poll"], bool) and isinstance(invoke["respond"], bool):
                            result.add_detail("✅ poll和respond属性是布尔值")
                            result.mark_success()
                        else:
                            result.mark_failure("❌ poll或respond属性不是布尔值")
                    else:
                        result.mark_failure("❌ 实例的invoke字段缺少poll或respond属性")
                else:
                    result.mark_failure("❌ 实例缺少invoke字段")
            else:
                result.mark_failure(f"❌ 实例注册失败: {register_response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_register_instance_empty_scope(self):
        """测试注册 scope 为空字符串的实例（无效值）"""
        result = TestResult("测试注册 scope 为空字符串的实例（无效值）")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-1",
                    "scope": "",  # 测试空字符串 scope（无效值）
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            if "error" in response and response["error"]["code"] == -32002:
                if response["error"]["message"] == "forbidden":
                    if "data" in response["error"] and response["error"]["data"].get("reason") == "scope_policy_violation":
                        result.add_detail("✅ 正确返回 -32002 forbidden 错误，reason 为 scope_policy_violation")
                        result.mark_success()
                    else:
                        result.mark_failure(f"❌ 错误数据中 reason 不正确: {response['error'].get('data', {})}")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
            else:
                result.mark_failure(f"❌ 错误码不正确: {response.get('error', {})}")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_list_instances_include_offline(self):
        """测试 includeOffline 参数（包含离线实例）"""
        result = TestResult("测试 includeOffline 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)
            instance_id = self.generate_unique_instance_id()

            # 注册实例
            client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": instance_id,
                    "appId": "test-app-offline",
                    "scope": None,
                    "pid": 12345,
                    "invoke": { "poll": True, "respond": True }
                }
            })

            # 首先验证实例在线时可见
            list_response = client.call("hub.apps.listInstances", {"appId": "test-app-offline"})
            if "result" in list_response and "instances" in list_response["result"]:
                instances = list_response["result"]["instances"]
                found = any(inst.get("instanceId") == instance_id for inst in instances)

                if found:
                    result.add_detail("✅ 实例在线时可见")
                else:
                    result.mark_failure("❌ 实例在线时未找到")
                    return result

            # 让实例超时变为离线（超过30秒不发送心跳）
            wait_seconds = 35
            result.add_detail(f"⏳ 等待 {wait_seconds} 秒，让实例超时离线...")

            import sys
            progress_width = 40
            for i in range(wait_seconds + 1):
                if i > 0:
                    time.sleep(1)
                percent = int((i / wait_seconds) * 100)
                filled = int((i / wait_seconds) * progress_width)
                bar = "█" * filled + "░" * (progress_width - filled)
                sys.stdout.write(f"\r    [{bar}] {percent}% ({i}/{wait_seconds}s)")
                sys.stdout.flush()
            sys.stdout.write("\n")
            sys.stdout.flush()

            # 验证默认情况下不返回离线实例
            list_response_default = client.call("hub.apps.listInstances", {"appId": "test-app-offline"})
            if "result" in list_response_default and "instances" in list_response_default["result"]:
                instances_default = list_response_default["result"]["instances"]
                found = any(inst.get("instanceId") == instance_id for inst in instances_default)

                if not found:
                    result.add_detail("✅ 默认情况下不返回离线实例")
                else:
                    result.mark_failure("❌ 默认情况下返回了离线实例")
                    return result

            # 验证 includeOffline=true 时返回离线实例
            list_response_offline = client.call("hub.apps.listInstances", {
                "appId": "test-app-offline",
                "includeOffline": True
            })
            if "result" in list_response_offline and "instances" in list_response_offline["result"]:
                instances_offline = list_response_offline["result"]["instances"]
                found = any(inst.get("instanceId") == instance_id for inst in instances_offline)

                if found:
                    result.add_detail("✅ includeOffline=true 时返回离线实例")

                    # 验证返回的实例包含所有必备字段
                    for instance in instances_offline:
                        self._validate_app_instance_fields(result, instance)
                else:
                    result.mark_failure("❌ includeOffline=true 时未返回离线实例")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清理测试实例
            try:
                base_url, token = DiscoveryService.get_hub_info()
                client = RpcClient(base_url, token)
                list_response = client.call("hub.apps.listInstances", {"includeAllScopes": True, "includeOffline": True})
                if "result" in list_response and list_response["result"].get("ok") and "instances" in list_response["result"]:
                    for instance in list_response["result"]["instances"]:
                        if instance.get("appId") == "test-app-offline":
                            client.call("hub.apps.unregisterInstance", {"instanceId": instance.get("instanceId")})
            except:
                pass

        return result

    def run_all_tests(self):
        """运行所有 AppInstance 测试"""
        return [
            self.test_register_and_list_instances(),
            self.test_heartbeat_updates_last_seen(),
            self.test_heartbeat_nonexistent_instance(),
            self.test_unregister_instance(),
            self.test_unregister_nonexistent_instance(),
            self.test_list_instances_with_params(),
            self.test_list_instances_include_offline(),
            self.test_instance_offline_after_30s_no_heartbeat(),
            self.test_register_instance_with_global_scope(),
            self.test_list_instances_scope_strict_match(),
            self.test_register_instance_empty_scope(),
            self.test_register_instance_invoke_field_validation()
        ]


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
