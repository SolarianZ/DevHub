#!/usr/bin/env python3
"""
DevHub M1 测试基础类和工具函数
"""

import os
import json
import platform
import requests
import uuid
from contextlib import contextmanager
from datetime import datetime
from typing import Any, Dict, Iterable, List, Optional, Tuple


@contextmanager
def temporary_env_var(name: str, value: Optional[str]):
    """
    临时设置环境变量并在退出时恢复原值。

    该 helper 用于解决集成测试中的环境污染问题：
    某些用例（例如 DEVHUB_RUNTIME_DIR 相关测试）若直接覆盖并清空环境变量，
    会导致后续用例读取到错误的 hub.json 路径，从而出现 Connection refused。
    """
    original_value = os.environ.get(name)
    try:
        if value is None:
            os.environ.pop(name, None)
        else:
            os.environ[name] = value
        yield
    finally:
        if original_value is None:
            os.environ.pop(name, None)
        else:
            os.environ[name] = original_value


class DiscoveryService:
    """
    从 hub.json 发现文件中获取 DevHub 服务信息
    """

    @staticmethod
    def get_runtime_directory():
        """获取运行时目录"""
        if "DEVHUB_RUNTIME_DIR" in os.environ:
            return os.environ["DEVHUB_RUNTIME_DIR"]

        system = platform.system()
        if system == "Windows":
            return os.path.join(os.environ["LOCALAPPDATA"], "DevHub", "runtime")
        elif system == "Darwin":
            return os.path.join(os.environ["HOME"], "Library", "Application Support", "DevHub", "runtime")
        elif system == "Linux":
            xdg_data_home = os.environ.get("XDG_DATA_HOME", "").strip()
            if xdg_data_home:
                return os.path.join(xdg_data_home, "DevHub", "runtime")
            return os.path.join(os.environ["HOME"], ".local", "share", "DevHub", "runtime")
        else:
            raise Exception(f"Unsupported OS: {system}")

    @staticmethod
    def get_hub_info():
        """
        从 hub.json 获取服务信息
        :return: (http_base_url, token)
        """
        runtime_dir = DiscoveryService.get_runtime_directory()
        hub_json_path = os.path.join(runtime_dir, "hub.json")

        if not os.path.exists(hub_json_path):
            raise FileNotFoundError(f"hub.json not found: {hub_json_path}")

        with open(hub_json_path, "r", encoding="utf-8") as f:
            hub_info = json.load(f)

        token_path = hub_info["tokenFile"]
        if not os.path.exists(token_path):
            raise FileNotFoundError(f"Token file not found: {token_path}")

        with open(token_path, "r", encoding="utf-8") as f:
            token = f.read().strip()

        return hub_info["httpBaseUrl"], token


def get_definitions_dir() -> str:
    """获取应用定义目录（按 Spec 与环境变量约定）。"""
    if "DEVHUB_APPDEFS_DIR" in os.environ:
        definitions_dir = os.environ["DEVHUB_APPDEFS_DIR"]
    else:
        runtime_dir = DiscoveryService.get_runtime_directory()
        definitions_dir = os.path.abspath(os.path.join(runtime_dir, "..", "apps", "definitions"))

    os.makedirs(definitions_dir, exist_ok=True)
    return definitions_dir


def write_definition(app_id: str, payload: Dict[str, Any]) -> str:
    """写入测试 AppDefinition 并返回文件路径。"""
    definition_path = os.path.join(get_definitions_dir(), f"{app_id}.json")
    with open(definition_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    return definition_path


def build_app_definition(
    app_id: str,
    *,
    display_name: Optional[str] = None,
    description: Optional[str] = None,
    rpc: bool = True,
    events: bool = False,
    launch: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """构造标准测试 AppDefinition 负载。"""
    payload: Dict[str, Any] = {
        "appId": app_id,
        "displayName": display_name or app_id,
        "capabilities": {
            "rpc": rpc,
            "events": events,
        },
    }
    if description is not None:
        payload["description"] = description
    if launch is not None:
        payload["launch"] = launch
    return payload


def write_app_definition(
    app_id: str,
    *,
    display_name: Optional[str] = None,
    description: Optional[str] = None,
    rpc: bool = True,
    events: bool = False,
    launch: Optional[Dict[str, Any]] = None,
) -> str:
    """按统一结构写入 AppDefinition。"""
    return write_definition(
        app_id,
        build_app_definition(
            app_id=app_id,
            display_name=display_name,
            description=description,
            rpc=rpc,
            events=events,
            launch=launch,
        ),
    )


def safe_remove(path: Optional[str]):
    """安全删除文件（不存在或删除失败时忽略）。"""
    if not path:
        return

    try:
        if os.path.exists(path):
            os.remove(path)
    except Exception:
        pass


def new_instance_id(prefix: str) -> str:
    """生成统一格式实例 ID。"""
    return f"{prefix}-{uuid.uuid4().hex[:10]}"


def unregister_instances(instance_ids: Iterable[Optional[str]]):
    """按实例 ID 列表执行幂等注销（用于测试清理）。"""
    ids = [instance_id for instance_id in instance_ids if instance_id]
    if not ids:
        return

    try:
        base_url, token = DiscoveryService.get_hub_info()
        client = RpcClient(base_url, token)
        for instance_id in ids:
            client.unregister_instance(instance_id)
    except Exception:
        pass


def get_runtime_hub_info() -> Tuple[str, str, str]:
    """读取运行时 HTTP/WS 地址与 token。"""
    runtime_dir = DiscoveryService.get_runtime_directory()
    hub_json_path = os.path.join(runtime_dir, "hub.json")

    if not os.path.exists(hub_json_path):
        raise FileNotFoundError(f"hub.json not found: {hub_json_path}")

    with open(hub_json_path, "r", encoding="utf-8") as f:
        hub_info = json.load(f)

    ws_url = hub_info.get("wsUrl")
    if not ws_url:
        raise ValueError("hub.json 缺少 wsUrl")

    token_path = hub_info.get("tokenFile")
    if not token_path or not os.path.exists(token_path):
        raise FileNotFoundError(f"Token file not found: {token_path}")

    with open(token_path, "r", encoding="utf-8") as f:
        token = f.read().strip()

    return hub_info["httpBaseUrl"], ws_url, token


class RpcClient:
    """
    JSON-RPC 客户端
    """

    def __init__(self, base_url, token, client_session_id=None):
        self.base_url = base_url
        self.token = token
        self.headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "PythonTestClient",
            "X-DevHub-ClientSessionId": client_session_id or str(uuid.uuid4())
        }

    def post_json(self, payload, headers=None, timeout=30):
        """发送 JSON body 并返回 (status_code, parsed_json)。"""
        request_headers = headers or self.headers
        url = f"{self.base_url}/rpc"
        try:
            response = requests.post(url, json=payload, headers=request_headers, timeout=timeout)
            return response.status_code, response.json()
        except requests.exceptions.RequestException as e:
            raise Exception(f"RPC request failed: {e}")

    def post_raw(self, body, headers=None, timeout=30):
        """发送原始 body 并返回 (status_code, parsed_json/text)。"""
        request_headers = headers or self.headers
        url = f"{self.base_url}/rpc"
        try:
            response = requests.post(url, data=body, headers=request_headers, timeout=timeout)
            try:
                return response.status_code, response.json()
            except Exception:
                return response.status_code, response.text
        except requests.exceptions.RequestException as e:
            raise Exception(f"RPC request failed: {e}")

    def call(self, method, params=None, request_id="1"):
        """
        调用 JSON-RPC 方法
        :param method: 方法名
        :param params: 参数字典
        :param request_id: 请求 ID
        :return: 响应字典
        """
        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

        _, response = self.post_json(payload, headers=self.headers, timeout=30)
        return response

    def call_with_invalid_headers(self, method, invalid_headers, params=None, request_id="1"):
        """
        使用无效的请求头调用方法（用于测试鉴权）
        """
        headers = self.headers.copy()
        headers.update(invalid_headers)

        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

        _, response = self.post_json(payload, headers=headers, timeout=30)
        return response

    def send_batch_request(self, requests_list):
        """
        发送 batch 请求（用于测试批量请求被拒绝的情况）
        """
        status_code, response = self.post_json(requests_list, headers=self.headers, timeout=30)
        return response, status_code

    def call_with_timeout(self, method, params=None, timeout_sec=30, request_id="1"):
        """带超时的 JSON-RPC 调用。"""
        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params or {}
        }

        _, response = self.post_json(payload, headers=self.headers, timeout=timeout_sec)
        return response

    def register_instance(self, instance_id, app_id, scope=None, poll=True, respond=True, pid=12345):
        """注册实例。"""
        return self.call("hub.apps.registerInstance", {
            "instance": {
                "instanceId": instance_id,
                "appId": app_id,
                "scope": scope,
                "pid": pid,
                "invoke": {
                    "poll": poll,
                    "respond": respond
                }
            }
        })

    def heartbeat_instance(self, instance_id):
        """发送实例心跳。"""
        return self.call("hub.apps.heartbeat", {"instanceId": instance_id})

    def unregister_instance(self, instance_id):
        """注销实例。"""
        return self.call("hub.apps.unregisterInstance", {"instanceId": instance_id})

    def poll_once(self, instance_id, max_count=10, wait_ms=25000):
        """执行一次 poll。"""
        return self.call("hub.invoke.poll", {
            "instanceId": instance_id,
            "maxCount": max_count,
            "waitMs": wait_ms
        })

    def respond_value(self, instance_id, invocation_id, value):
        """回传 value。"""
        return self.call("hub.invoke.respond", {
            "instanceId": instance_id,
            "invocationId": invocation_id,
            "value": value
        })

    def respond_error(self, instance_id, invocation_id, error):
        """回传 error。"""
        return self.call("hub.invoke.respond", {
            "instanceId": instance_id,
            "invocationId": invocation_id,
            "error": error
        })

    def build_invoke_notify_params(
        self,
        app_id,
        method,
        args=None,
        target_scope=None,
        target_instance_id=None,
        ttl_ms=60000,
        queue_if_offline=True,
        auto_launch=None,
    ):
        """构造 notify 参数。"""
        if auto_launch is None:
            auto_launch = target_instance_id is None

        return {
            "appId": app_id,
            "target": {
                "scope": target_scope,
                "instanceId": target_instance_id
            },
            "method": method,
            "args": args or {},
            "options": {
                "ttlMs": ttl_ms,
                "queueIfOffline": queue_if_offline,
                "autoLaunch": auto_launch
            }
        }

    def invoke_notify(
        self,
        app_id,
        method,
        args=None,
        target_scope=None,
        target_instance_id=None,
        ttl_ms=60000,
        queue_if_offline=True,
        auto_launch=None,
        request_id="1",
    ):
        """调用 hub.invoke.notify。"""
        params = self.build_invoke_notify_params(
            app_id=app_id,
            method=method,
            args=args,
            target_scope=target_scope,
            target_instance_id=target_instance_id,
            ttl_ms=ttl_ms,
            queue_if_offline=queue_if_offline,
            auto_launch=auto_launch,
        )
        return self.call("hub.invoke.notify", params=params, request_id=request_id)

    def invoke_request(
        self,
        app_id,
        method,
        args=None,
        target_scope=None,
        target_instance_id=None,
        options=None,
        request_id="1",
    ):
        """调用 hub.invoke.request。"""
        default_auto_launch = target_instance_id is None
        params = {
            "appId": app_id,
            "target": {
                "scope": target_scope,
                "instanceId": target_instance_id
            },
            "method": method,
            "args": args or {},
            "options": options or {
                "ttlMs": 300000,
                "waitTimeoutMs": 120000,
                "queueIfOffline": True,
                "autoLaunch": default_auto_launch
            }
        }
        return self.call("hub.invoke.request", params=params, request_id=request_id)

    def launch_app(
        self,
        app_id,
        scope=None,
        dedupe_key=None,
        wait_for_register_ms=0,
        request_id="1",
    ):
        """调用 hub.apps.launch。"""
        params = {
            "appId": app_id,
            "scope": scope,
            "waitForRegisterMs": wait_for_register_ms,
        }
        if dedupe_key is not None:
            params["dedupeKey"] = dedupe_key

        return self.call("hub.apps.launch", params=params, request_id=request_id)


class TestResult:
    """
    测试结果类
    """

    def __init__(self, test_name):
        self.test_name = test_name
        self.start_time = datetime.now()
        self.end_time = None
        self.success = False
        self.error_message = None
        self.details = []

    def add_detail(self, message):
        """添加详细信息"""
        self.details.append(message)

    def mark_success(self):
        """标记成功"""
        self.success = True
        self.end_time = datetime.now()

    def mark_failure(self, error_message):
        """标记失败"""
        self.success = False
        self.error_message = error_message
        self.end_time = datetime.now()

    def to_dict(self):
        """转换为字典"""
        return {
            "test_name": self.test_name,
            "start_time": self.start_time.isoformat(),
            "end_time": self.end_time.isoformat() if self.end_time else None,
            "success": self.success,
            "error_message": self.error_message,
            "details": self.details
        }


class RpcAssertions:
    """JSON-RPC 断言辅助方法"""

    @staticmethod
    def expect_success(result: TestResult, response: dict, required_fields: Optional[List[str]] = None):
        """断言响应为成功结果"""
        if "error" in response:
            result.mark_failure(f"❌ 期望成功响应，但返回错误: {response['error']}")
            return False

        if "result" not in response or not isinstance(response["result"], dict):
            result.mark_failure(f"❌ 响应缺少 result 对象: {response}")
            return False

        if response["result"].get("ok") is not True:
            result.mark_failure(f"❌ result.ok 不为 true: {response['result']}")
            return False

        if required_fields:
            for field in required_fields:
                if field not in response["result"]:
                    result.mark_failure(f"❌ 成功响应缺少字段 {field}: {response['result']}")
                    return False

        return True

    @staticmethod
    def expect_error(
        result: TestResult,
        response: dict,
        expected_code: int,
        expected_message: Optional[str] = None,
        expected_id: Any = ...,
        expected_data: Optional[dict] = None,
    ):
        """断言响应为错误结果"""
        if "error" not in response or not isinstance(response["error"], dict):
            result.mark_failure(f"❌ 期望错误响应，但未返回 error: {response}")
            return False

        error = response["error"]
        actual_code = error.get("code")
        if actual_code != expected_code:
            result.mark_failure(f"❌ 错误码不正确: 期望 {expected_code}，实际 {actual_code}")
            return False

        if expected_message is not None:
            actual_message = error.get("message")
            if actual_message != expected_message:
                result.mark_failure(f"❌ 错误消息不正确: 期望 {expected_message}，实际 {actual_message}")
                return False

        if expected_id is not ...:
            actual_id = response.get("id")
            if actual_id != expected_id:
                result.mark_failure(f"❌ 响应 id 不正确: 期望 {expected_id}，实际 {actual_id}")
                return False

        if expected_data is not None:
            data = error.get("data")
            if not isinstance(data, dict):
                result.mark_failure(f"❌ error.data 不是对象: {error}")
                return False

            for key, expected_value in expected_data.items():
                if data.get(key) != expected_value:
                    result.mark_failure(
                        f"❌ error.data.{key} 不正确: 期望 {expected_value}，实际 {data.get(key)}")
                    return False

        return True

    @staticmethod
    def expect_http_status(result: TestResult, status_code: int, expected_status: int = 200):
        """断言 HTTP 状态码。"""
        if status_code != expected_status:
            result.mark_failure(f"❌ HTTP 状态码不正确: 期望 {expected_status}，实际 {status_code}")
            return False
        return True

    @staticmethod
    def expect_error_data_fields(result: TestResult, response: dict, expected_data: dict):
        """断言 error.data 关键字段。"""
        if "error" not in response or not isinstance(response["error"], dict):
            result.mark_failure(f"❌ 响应缺少 error 对象: {response}")
            return False

        data = response["error"].get("data")
        if not isinstance(data, dict):
            result.mark_failure(f"❌ error.data 不是对象: {response['error']}")
            return False

        for key, expected_value in expected_data.items():
            if data.get(key) != expected_value:
                result.mark_failure(
                    f"❌ error.data.{key} 不正确: 期望 {expected_value}，实际 {data.get(key)}")
                return False

        return True

    @staticmethod
    def assert_invalid_scope_error(result: TestResult, response: dict, reason: Optional[str] = None):
        """断言 scope 相关 invalid_params 错误。"""
        if not RpcAssertions.expect_error(result, response, -32602, "invalid_params"):
            return False

        if reason is None:
            return True

        return RpcAssertions.expect_error_data_fields(result, response, {"reason": reason})

    @staticmethod
    def assert_items_all_match_scope(result: TestResult, items: list, scope):
        """断言 items 内所有元素都满足给定 scope。"""
        if not isinstance(items, list):
            result.mark_failure(f"❌ items 不是数组: {items}")
            return False

        for index, item in enumerate(items):
            if not isinstance(item, dict):
                result.mark_failure(f"❌ items[{index}] 不是对象: {item}")
                return False

            actual_scope = item.get("scope")
            if actual_scope != scope:
                result.mark_failure(
                    f"❌ items[{index}].scope 不匹配: 期望 {scope!r}，实际 {actual_scope!r}, item={item}")
                return False

        return True


class TestReport:
    """
    测试报告类
    """

    def __init__(self, mode="default", coverage="全部 M1 必测 + Spec MUST（默认）"):
        self.results = []
        self.start_time = datetime.now()
        self.end_time = None
        self.mode = mode
        self.coverage = coverage

    def add_result(self, result):
        """添加测试结果"""
        self.results.append(result)

    def mark_complete(self):
        """标记所有测试完成"""
        self.end_time = datetime.now()

    def get_summary(self):
        """获取摘要"""
        total = len(self.results)
        passed = sum(1 for r in self.results if r.success)
        failed = total - passed
        return {
            "total": total,
            "passed": passed,
            "failed": failed,
            "success_rate": passed / total * 100 if total > 0 else 0
        }

    def save_to_file(self, file_path):
        """保存到文件"""
        self.mark_complete()

        data = {
            "start_time": self.start_time.isoformat(),
            "end_time": self.end_time.isoformat(),
            "duration": (self.end_time - self.start_time).total_seconds(),
            "mode": self.mode,
            "coverage": self.coverage,
            "summary": self.get_summary(),
            "results": [r.to_dict() for r in self.results]
        }

        with open(file_path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2, default=str)

    def save_text_report(self, file_path):
        """保存文本格式报告"""
        self.mark_complete()
        summary = self.get_summary()

        with open(file_path, "w", encoding="utf-8") as f:
            f.write("=" * 60 + "\n")
            f.write("DevHub M1~M4 功能测试报告\n")
            f.write("=" * 60 + "\n\n")
            f.write(f"测试时间: {self.start_time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"完成时间: {self.end_time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"测试时长: {self.end_time - self.start_time}\n")
            f.write(f"测试模式: {self.mode}\n")
            f.write(f"覆盖级别: {self.coverage}\n")
            f.write("\n")
            f.write(f"总测试数: {summary['total']}\n")
            f.write(f"通过数: {summary['passed']}\n")
            f.write(f"失败数: {summary['failed']}\n")
            f.write(f"成功率: {summary['success_rate']:.1f}%\n")
            f.write("\n" + "-" * 60 + "\n\n")

            for i, result in enumerate(self.results, 1):
                status = "✅" if result.success else "❌"
                f.write(f"{status} 测试 {i}: {result.test_name}\n")

                if result.details:
                    for detail in result.details:
                        f.write(f"  - {detail}\n")

                if not result.success and result.error_message:
                    f.write(f"  错误: {result.error_message}\n")

                f.write("\n")


def create_temp_directory():
    """创建 temp 目录"""
    temp_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "temp")
    os.makedirs(temp_dir, exist_ok=True)
    return temp_dir
