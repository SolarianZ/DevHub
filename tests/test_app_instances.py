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

            if "result" in register_response and register_response["result"].get("ok"):
                result.add_detail("✅ 实例注册成功")

                # 列出实例
                list_response = client.call("hub.apps.listInstances")

                if "result" in list_response and "ok" in list_response["result"] and list_response["result"]["ok"] == True and "instances" in list_response["result"]:
                    instances = list_response["result"]["instances"]
                    found = any(inst.get("instanceId") == instance_id for inst in instances)

                    if found:
                        result.add_detail("✅ 实例在列表中可见")
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

            if "result" in heartbeat1_response and "ok" in heartbeat1_response["result"] and heartbeat1_response["result"]["ok"] == True and "lastSeenUtc" in heartbeat1_response["result"]:
                last_seen1 = heartbeat1_response["result"]["lastSeenUtc"]
                result.add_detail(f"✅ 第一次心跳成功，最后见过时间: {last_seen1}")

                # 等待一段时间
                time.sleep(2)

                # 第二次心跳
                heartbeat2_response = client.call("hub.apps.heartbeat", {"instanceId": instance_id})

                if "result" in heartbeat2_response and "ok" in heartbeat2_response["result"] and heartbeat2_response["result"]["ok"] == True and "lastSeenUtc" in heartbeat2_response["result"]:
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
                if "result" in list_response and "ok" in list_response["result"] and list_response["result"]["ok"] == True and "instances" in list_response["result"]:
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

            if "result" not in register_response or not register_response["result"].get("ok"):
                result.mark_failure(f"❌ 实例注册失败: {register_response.get('error', {})}")
                return result

            result.add_detail("✅ 实例注册成功")

            # 立即列出实例，应该能看到（在线）
            list_response = client.call("hub.apps.listInstances")
            if "result" in list_response and "ok" in list_response["result"] and list_response["result"]["ok"] == True and "instances" in list_response["result"]:
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
            if "result" in list_response2 and "ok" in list_response2["result"] and list_response2["result"]["ok"] == True and "instances" in list_response2["result"]:
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

    def run_all_tests(self):
        """运行所有 AppInstance 测试"""
        return [
            self.test_register_and_list_instances(),
            self.test_heartbeat_updates_last_seen(),
            self.test_unregister_instance(),
            self.test_instance_offline_after_30s_no_heartbeat()
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
