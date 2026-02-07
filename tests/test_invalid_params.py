#!/usr/bin/env python3
"""
DevHub M1 -32602 invalid_params 参数验证测试
"""

import os
import sys
import unittest

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class TestInvalidParams(unittest.TestCase):
    """-32602 invalid_params 参数验证测试类"""

    def test_params_as_array(self):
        """测试 hub.* 方法使用数组参数时返回 invalid_params"""
        result = TestResult("测试参数为数组时返回 invalid_params")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                ("hub.ping", ["invalid"]),
                ("hub.apps.listDefinitions", ["invalid"]),
                ("hub.apps.getDefinition", ["invalid"]),
                ("hub.apps.registerInstance", ["invalid"]),
                ("hub.apps.heartbeat", ["invalid"]),
                ("hub.apps.unregisterInstance", ["invalid"]),
                ("hub.apps.listInstances", ["invalid"]),
            ]

            for method, params in cases:
                response = client.call(method, params)
                if not RpcAssertions.expect_error(
                    result,
                    response,
                    expected_code=-32602,
                    expected_message="invalid_params"
                ):
                    return result
                result.add_detail(f"✅ {method} 正确拒绝数组参数")

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
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
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
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
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
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
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

            cases = [
                {
                    "name": "缺少 instanceId",
                    "payload": {
                        "instance": {
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "缺少 appId",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "缺少 pid",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "缺少 invoke",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "pid": 12345
                        }
                    }
                }
            ]

            for case in cases:
                response = client.call("hub.apps.registerInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_pid(self):
        """测试 hub.apps.registerInstance 使用无效 pid"""
        result = TestResult("测试 hub.apps.registerInstance 使用无效 pid")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "pid=0",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "pid": 0,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "pid=-1",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "pid": -1,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                }
            ]

            for case in cases:
                response = client.call("hub.apps.registerInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_instanceid(self):
        """测试 hub.apps.registerInstance 使用非法 instanceId"""
        result = TestResult("测试 hub.apps.registerInstance 使用非法 instanceId")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "instanceId 非字符串",
                    "payload": {
                        "instance": {
                            "instanceId": 123,
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "instanceId 含非法字符",
                    "payload": {
                        "instance": {
                            "instanceId": "invalid instance id",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "instanceId 长度超过256",
                    "payload": {
                        "instance": {
                            "instanceId": "a" * 257,
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                }
            ]

            for case in cases:
                response = client.call("hub.apps.registerInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_scope(self):
        """测试 hub.apps.registerInstance 使用无效 scope"""
        result = TestResult("测试 hub.apps.registerInstance 使用无效 scope")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "scope 空字符串",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "scope": "",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "scope='global'",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance",
                            "appId": "test-app",
                            "scope": "global",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                }
            ]

            for case in cases:
                response = client.call("hub.apps.registerInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

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
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_register_instance_invalid_invoke(self):
        """测试 hub.apps.registerInstance invoke 结构非法"""
        result = TestResult("测试 hub.apps.registerInstance invoke 结构非法")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "invoke 缺少 poll",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-invoke-1",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"respond": True}
                        }
                    }
                },
                {
                    "name": "invoke 缺少 respond",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-invoke-2",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True}
                        }
                    }
                },
                {
                    "name": "invoke.poll 非 bool",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-invoke-3",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": "yes", "respond": True}
                        }
                    }
                },
                {
                    "name": "invoke.respond 非 bool",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-invoke-4",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": 1}
                        }
                    }
                },
            ]

            for case in cases:
                response = client.call("hub.apps.registerInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_heartbeat_invalid_instanceid(self):
        """测试 hub.apps.heartbeat instanceId 非法"""
        result = TestResult("测试 hub.apps.heartbeat instanceId 非法")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "instanceId 空字符串", "payload": {"instanceId": ""}},
                {"name": "instanceId 非字符串", "payload": {"instanceId": 12345}},
                {"name": "instanceId 为 null", "payload": {"instanceId": None}},
            ]

            for case in cases:
                response = client.call("hub.apps.heartbeat", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_unregister_instance_invalid_instanceid(self):
        """测试 hub.apps.unregisterInstance instanceId 非法"""
        result = TestResult("测试 hub.apps.unregisterInstance instanceId 非法")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "instanceId 空字符串", "payload": {"instanceId": ""}},
                {"name": "instanceId 非字符串", "payload": {"instanceId": 12345}},
                {"name": "instanceId 为 null", "payload": {"instanceId": None}},
            ]

            for case in cases:
                response = client.call("hub.apps.unregisterInstance", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_apps_list_instances_invalid_params(self):
        """测试 hub.apps.listInstances 关键参数类型校验"""
        result = TestResult("测试 hub.apps.listInstances 关键参数类型校验")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "appId 非字符串", "payload": {"appId": 123}},
                {"name": "scope 非字符串/非null", "payload": {"scope": 123}},
                {"name": "includeAllScopes 非布尔", "payload": {"includeAllScopes": "true"}},
                {"name": "includeOffline 非布尔", "payload": {"includeOffline": "true"}},
                {"name": "scope 空字符串", "payload": {"scope": ""}},
                {"name": "scope=global", "payload": {"scope": "global"}},
            ]

            for case in cases:
                response = client.call("hub.apps.listInstances", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params")

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
        """运行所有 invalid_params 测试"""
        return [
            self.test_params_as_array(),
            self.test_hub_apps_get_definition_missing_appid(),
            self.test_hub_apps_get_definition_empty_appid(),
            self.test_hub_apps_register_instance_missing_instance(),
            self.test_hub_apps_register_instance_missing_required_fields(),
            self.test_hub_apps_register_instance_invalid_pid(),
            self.test_hub_apps_register_instance_invalid_instanceid(),
            self.test_hub_apps_register_instance_invalid_scope(),
            self.test_hub_apps_heartbeat_missing_instanceid(),
            self.test_hub_apps_register_instance_invalid_invoke(),
            self.test_hub_apps_heartbeat_invalid_instanceid(),
            self.test_hub_apps_unregister_instance_invalid_instanceid(),
            self.test_hub_apps_list_instances_invalid_params()
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
