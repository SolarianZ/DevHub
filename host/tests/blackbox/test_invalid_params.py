#!/usr/bin/env python3
"""
DevHub -32602 invalid_params 参数验证测试
"""

import os
import unittest


from tests.blackbox.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


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
                ("hub.getVersion", ["invalid"]),
                ("hub.apps.listDefinitions", ["invalid"]),
                ("hub.apps.getDefinition", ["invalid"]),
                ("hub.apps.registerInstance", ["invalid"]),
                ("hub.apps.heartbeat", ["invalid"]),
                ("hub.apps.unregisterInstance", ["invalid"]),
                ("hub.apps.listInstances", ["invalid"]),
                ("hub.apps.launch", ["invalid"]),
                ("hub.invoke.notify", ["invalid"]),
                ("hub.invoke.request", ["invalid"]),
                ("hub.invoke.poll", ["invalid"]),
                ("hub.invoke.respond", ["invalid"]),
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

    def test_hub_get_version_unexpected_params(self):
        """测试 hub.getVersion 传入额外字段或标量时返回 invalid_params"""
        result = TestResult("测试 hub.getVersion 传入额外字段或标量时返回 invalid_params")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            response = client.call("hub.getVersion", {"verbose": True})
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                return result

            scalar_response = client.call("hub.getVersion", 1, request_id="get-version-scalar")
            if not RpcAssertions.expect_error(
                result,
                scalar_response,
                expected_code=-32602,
                expected_message="invalid_params",
                expected_id="get-version-scalar",
            ):
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

    def test_hub_apps_register_instance_missing_password(self):
        """测试 hub.apps.registerInstance 缺少顶层 password 参数"""
        result = TestResult("测试 hub.apps.registerInstance 缺少顶层 password 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            _, response = client.post_json(
                {
                    "jsonrpc": "2.0",
                    "id": "missing-password-register",
                    "method": "hub.apps.registerInstance",
                    "params": {
                        "instance": {
                            "instanceId": "missing-password-register-instance",
                            "appId": "missing.password.register.app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True},
                        }
                    },
                },
                headers=client.headers,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="missing-password-register"):
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
                    "name": "instanceId 以 . 开头",
                    "payload": {
                        "instance": {
                            "instanceId": ".invalid-id",
                            "appId": "test-app",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "instanceId 以 - 结尾",
                    "payload": {
                        "instance": {
                            "instanceId": "invalid-id-",
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
                    "name": "scope 非字符串（数字）",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-scope-1",
                            "appId": "test-app",
                            "scope": 123,
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "scope 非字符串（布尔）",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-scope-2",
                            "appId": "test-app",
                            "scope": True,
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "scope 非字符串（对象）",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-scope-3",
                            "appId": "test-app",
                            "scope": {"name": "workspace-a"},
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "scope 以 . 开头",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-scope-4",
                            "appId": "test-app",
                            "scope": ".invalid-scope",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "scope 以 - 结尾",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-scope-5",
                            "appId": "test-app",
                            "scope": "invalid-scope-",
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

    def test_hub_apps_register_instance_invalid_appid(self):
        """测试 hub.apps.registerInstance 使用非法 appId"""
        result = TestResult("测试 hub.apps.registerInstance 使用非法 appId")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "appId 包含大写字母",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-appid-1",
                            "appId": "Invalid.App",
                            "scope": "",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "appId 包含下划线",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-appid-2",
                            "appId": "invalid_app",
                            "scope": "",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
                        }
                    }
                },
                {
                    "name": "appId 包含空格",
                    "payload": {
                        "instance": {
                            "instanceId": "test-instance-appid-3",
                            "appId": "invalid app",
                            "scope": "",
                            "pid": 12345,
                            "invoke": {"poll": True, "respond": True}
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

    def test_hub_apps_launch_invalid_scope(self):
        """测试 hub.apps.launch 使用无效 scope"""
        result = TestResult("测试 hub.apps.launch 使用无效 scope")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {
                    "name": "scope 非字符串（数字）",
                    "payload": {
                        "appId": "test-launch-invalid-scope",
                        "scope": 123,
                        "waitForRegisterMs": 0,
                    }
                },
                {
                    "name": "scope 非字符串（布尔）",
                    "payload": {
                        "appId": "test-launch-invalid-scope",
                        "scope": True,
                        "waitForRegisterMs": 0,
                    }
                },
                {
                    "name": "scope 非字符串（对象）",
                    "payload": {
                        "appId": "test-launch-invalid-scope",
                        "scope": {"name": "workspace-a"},
                        "waitForRegisterMs": 0,
                    }
                }
            ]

            for case in cases:
                response = client.call("hub.apps.launch", case["payload"])
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
                    return result
                if not RpcAssertions.expect_error_data_fields(result, response, {"reason": "invalid_scope"}):
                    return result
                result.add_detail(f"✅ {case['name']} 正确返回 invalid_params/invalid_scope")

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

    def test_hub_apps_heartbeat_missing_instance_session_token(self):
        """测试 hub.apps.heartbeat 缺少 instanceSessionToken 参数"""
        result = TestResult("测试 hub.apps.heartbeat 缺少 instanceSessionToken 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            _, response = client.post_json(
                {
                    "jsonrpc": "2.0",
                    "id": "missing-heartbeat-token",
                    "method": "hub.apps.heartbeat",
                    "params": {
                        "instanceId": "missing-heartbeat-token-instance",
                    },
                },
                headers=client.headers,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="missing-heartbeat-token"):
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
                {"name": "instanceId 以 . 开头", "payload": {"instanceId": ".invalid"}},
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
                {"name": "instanceId 以 - 结尾", "payload": {"instanceId": "invalid-"}},
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

    def test_hub_apps_unregister_instance_missing_instance_session_token(self):
        """测试 hub.apps.unregisterInstance 缺少顶层 instanceSessionToken 参数"""
        result = TestResult("测试 hub.apps.unregisterInstance 缺少顶层 instanceSessionToken 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            _, response = client.post_json(
                {
                    "jsonrpc": "2.0",
                    "id": "missing-unregister-token",
                    "method": "hub.apps.unregisterInstance",
                    "params": {
                        "instanceId": "missing-unregister-token-instance",
                    },
                },
                headers=client.headers,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="missing-unregister-token"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_invoke_poll_missing_instance_session_token(self):
        """测试 hub.invoke.poll 缺少 instanceSessionToken 参数"""
        result = TestResult("测试 hub.invoke.poll 缺少 instanceSessionToken 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            _, response = client.post_json(
                {
                    "jsonrpc": "2.0",
                    "id": "missing-poll-token",
                    "method": "hub.invoke.poll",
                    "params": {
                        "instanceId": "missing-poll-token-instance",
                        "maxCount": 1,
                        "waitMs": 0,
                    },
                },
                headers=client.headers,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="missing-poll-token"):
                return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_hub_invoke_respond_missing_instance_session_token(self):
        """测试 hub.invoke.respond 缺少 instanceSessionToken 参数"""
        result = TestResult("测试 hub.invoke.respond 缺少 instanceSessionToken 参数")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            _, response = client.post_json(
                {
                    "jsonrpc": "2.0",
                    "id": "missing-respond-token",
                    "method": "hub.invoke.respond",
                    "params": {
                        "instanceId": "missing-respond-token-instance",
                        "invocationId": "invk-missing-respond-token",
                        "value": {"ok": True},
                    },
                },
                headers=client.headers,
                timeout=30,
            )
            if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="missing-respond-token"):
                return result

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
                {"name": "缺少 scope", "payload": {}},
                {"name": "appId 非字符串", "payload": {"appId": 123}},
                {"name": "scope 非字符串/非null", "payload": {"scope": 123}},
                {"name": "includeOffline 非布尔", "payload": {"includeOffline": "true"}},
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

    def test_hub_apps_get_instance_invalid_params(self):
        """测试 hub.apps.getInstance 关键参数类型校验"""
        result = TestResult("测试 hub.apps.getInstance 关键参数类型校验")

        try:
            base_url, token = DiscoveryService.get_hub_info()
            client = RpcClient(base_url, token)

            cases = [
                {"name": "缺少 instanceId", "payload": {}},
                {"name": "instanceId 非字符串", "payload": {"instanceId": 123}},
                {"name": "instanceId 格式非法", "payload": {"instanceId": "invalid instance id"}},
                {"name": "instanceId 以 . 开头", "payload": {"instanceId": ".invalid.instance"}},
            ]

            for case in cases:
                response = client.call("hub.apps.getInstance", case["payload"])
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
            self.test_hub_apps_get_instance_invalid_params(),
            self.test_hub_apps_register_instance_missing_instance(),
            self.test_hub_apps_register_instance_missing_password(),
            self.test_hub_apps_register_instance_missing_required_fields(),
            self.test_hub_apps_register_instance_invalid_pid(),
            self.test_hub_apps_register_instance_invalid_instanceid(),
            self.test_hub_apps_register_instance_invalid_scope(),
            self.test_hub_apps_register_instance_invalid_appid(),
            self.test_hub_apps_launch_invalid_scope(),
            self.test_hub_apps_heartbeat_missing_instanceid(),
            self.test_hub_apps_heartbeat_missing_instance_session_token(),
            self.test_hub_apps_register_instance_invalid_invoke(),
            self.test_hub_apps_heartbeat_invalid_instanceid(),
            self.test_hub_apps_unregister_instance_invalid_instanceid(),
            self.test_hub_apps_unregister_instance_missing_instance_session_token(),
            self.test_hub_apps_list_instances_invalid_params(),
            self.test_hub_invoke_poll_missing_instance_session_token(),
            self.test_hub_invoke_respond_missing_instance_session_token(),
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
