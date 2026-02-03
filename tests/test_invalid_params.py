#!/usr/bin/env python3
"""
DevHub M1 -32602 invalid_params 参数验证测试
"""

import os
import sys
import unittest
import json
import uuid

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult


class TestInvalidParams(unittest.TestCase):
    """-32602 invalid_params 参数验证测试类"""

    def test_params_as_array(self):
        """测试所有 hub.* 方法使用数组参数时返回 invalid_params"""
        result = TestResult("测试参数为数组时返回 invalid_params")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 测试 hub.ping 方法
            response = client.call("hub.ping", ["invalid"])
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ hub.ping 方法正确拒绝数组参数")
                else:
                    result.mark_failure(f"❌ hub.ping 错误消息不正确: {response['error']['message']}")
                    return result
            else:
                result.mark_failure(f"❌ hub.ping 未正确拒绝数组参数: {response.get('error', {})}")
                return result

            # 测试 hub.apps.listDefinitions 方法
            response = client.call("hub.apps.listDefinitions", ["invalid"])
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ hub.apps.listDefinitions 方法正确拒绝数组参数")
                else:
                    result.mark_failure(f"❌ hub.apps.listDefinitions 错误消息不正确: {response['error']['message']}")
                    return result
            else:
                result.mark_failure(f"❌ hub.apps.listDefinitions 未正确拒绝数组参数: {response.get('error', {})}")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_get_definition_missing_appid(self):
        """测试 hub.apps.getDefinition 缺少 appId 参数"""
        result = TestResult("测试 hub.apps.getDefinition 缺少 appId 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {})
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_get_definition_empty_appid(self):
        """测试 hub.apps.getDefinition 使用空字符串 appId"""
        result = TestResult("测试 hub.apps.getDefinition 使用空字符串 appId")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.getDefinition", {"appId": ""})
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_missing_instance(self):
        """测试 hub.apps.registerInstance 缺少 instance 参数"""
        result = TestResult("测试 hub.apps.registerInstance 缺少 instance 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.registerInstance", {})
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_missing_required_fields(self):
        """测试 hub.apps.registerInstance instance 缺少必填字段"""
        result = TestResult("测试 hub.apps.registerInstance instance 缺少必填字段")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # 缺少 instanceId
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "appId": "test-app",
                    "pid": 12345,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 缺少 instanceId 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            # 缺少 appId
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "pid": 12345,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 缺少 appId 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            # 缺少 pid
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 缺少 pid 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            # 缺少 invoke
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "pid": 12345
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 缺少 invoke 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_pid(self):
        """测试 hub.apps.registerInstance 使用无效的 pid"""
        result = TestResult("测试 hub.apps.registerInstance 使用无效的 pid")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # pid 为 0
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "pid": 0,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ pid=0 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            # pid 为负数
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "pid": -1,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ pid 为负数正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_scope(self):
        """测试 hub.apps.registerInstance 使用无效的 scope"""
        result = TestResult("测试 hub.apps.registerInstance 使用无效的 scope")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            # scope 为空字符串
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "scope": "",
                    "pid": 12345,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ scope 为空字符串正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            # scope 为 "global" 字符串
            response = client.call("hub.apps.registerInstance", {
                "instance": {
                    "instanceId": "test-instance",
                    "appId": "test-app",
                    "scope": "global",
                    "pid": 12345,
                    "invoke": {"poll": True, "respond": True}
                }
            })
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ scope 为 'global' 字符串正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_heartbeat_missing_instanceid(self):
        """测试 hub.apps.heartbeat 缺少 instanceId 参数"""
        result = TestResult("测试 hub.apps.heartbeat 缺少 instanceId 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.apps.heartbeat", {})
            if "error" in response and response["error"]["code"] == -32602:
                if response["error"]["message"] == "invalid_params":
                    result.add_detail("✅ 正确返回 invalid_params 错误")
                else:
                    result.mark_failure(f"❌ 错误消息不正确: {response['error']['message']}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result


    def run_all_tests(self):
        """运行所有 invalid_params 测试"""
        return [
            self.test_params_as_array(),
            self.test_hub_apps_get_definition_missing_appid(),
            self.test_hub_apps_get_definition_empty_appid(),
            self.test_hub_apps_register_instance_missing_instance(),
            self.test_hub_apps_register_instance_missing_required_fields(),
            self.test_hub_apps_register_instance_invalid_pid(),
            self.test_hub_apps_register_instance_invalid_scope(),
            self.test_hub_apps_heartbeat_missing_instanceid()
        ]


if __name__ == "__main__":
    # 运行测试
    test = TestInvalidParams()
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
