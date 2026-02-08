#!/usr/bin/env python3
"""
DevHub M4 WebSocket 鉴权与事件测试
"""

import os
import sys
import json
import uuid
import time
import socket
import base64
import hashlib
from urllib.parse import urlparse

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, RpcAssertions


class WebSocketClosed(Exception):
    """表示 WebSocket 已关闭。"""


class SimpleWebSocketClient:
    """轻量 WebSocket 客户端（仅覆盖当前测试所需能力）。"""

    def __init__(self, ws_url, timeout=5):
        self.ws_url = ws_url
        self.timeout = timeout
        self.sock = None
        self._recv_buffer = b""

    def connect(self):
        parsed = urlparse(self.ws_url)
        if parsed.scheme != "ws":
            raise ValueError(f"Only ws:// is supported in tests, got: {self.ws_url}")

        host = parsed.hostname or "127.0.0.1"
        port = parsed.port or 80
        path = parsed.path or "/"
        if parsed.query:
            path = f"{path}?{parsed.query}"

        self.sock = socket.create_connection((host, port), timeout=self.timeout)
        self.sock.settimeout(self.timeout)

        sec_key = base64.b64encode(os.urandom(16)).decode("ascii")
        request = (
            f"GET {path} HTTP/1.1\r\n"
            f"Host: {host}:{port}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {sec_key}\r\n"
            "Sec-WebSocket-Version: 13\r\n"
            "\r\n"
        )
        self.sock.sendall(request.encode("ascii"))

        response = self._recv_until(b"\r\n\r\n")
        if b" 101 " not in response.split(b"\r\n", 1)[0]:
            raise RuntimeError(f"WebSocket handshake failed: {response!r}")

        accept_expected = base64.b64encode(
            hashlib.sha1((sec_key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()
        ).decode("ascii")

        response_text = response.decode("ascii", errors="ignore")
        if f"Sec-WebSocket-Accept: {accept_expected}" not in response_text:
            raise RuntimeError("Invalid Sec-WebSocket-Accept from server")

    def close(self):
        if self.sock is None:
            return

        try:
            self._send_frame(0x8, b"")
        except Exception:
            pass

        try:
            self.sock.close()
        finally:
            self.sock = None

    def __enter__(self):
        self.connect()
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()

    def send_json(self, payload):
        self._send_frame(0x1, json.dumps(payload, ensure_ascii=False).encode("utf-8"))

    def recv_json(self, timeout=None):
        while True:
            opcode, payload = self._recv_frame(timeout=timeout)

            if opcode == 0x8:
                raise WebSocketClosed("received close frame")

            if opcode == 0x9:  # ping
                self._send_frame(0xA, payload)
                continue

            if opcode == 0xA:  # pong
                continue

            if opcode != 0x1:
                continue

            text = payload.decode("utf-8")
            return json.loads(text)

    def wait_for_close(self, timeout=2):
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                opcode, _ = self._recv_frame(timeout=max(0.1, deadline - time.time()))
                if opcode == 0x8:
                    return True
            except WebSocketClosed:
                return True
            except TimeoutError:
                continue
        return False

    def _send_frame(self, opcode, payload):
        if self.sock is None:
            raise RuntimeError("socket is not connected")

        fin_and_opcode = 0x80 | (opcode & 0x0F)
        mask_key = os.urandom(4)
        payload_len = len(payload)

        if payload_len <= 125:
            header = bytes([fin_and_opcode, 0x80 | payload_len])
        elif payload_len <= 0xFFFF:
            header = bytes([fin_and_opcode, 0x80 | 126]) + payload_len.to_bytes(2, "big")
        else:
            header = bytes([fin_and_opcode, 0x80 | 127]) + payload_len.to_bytes(8, "big")

        masked_payload = bytes(payload[i] ^ mask_key[i % 4] for i in range(payload_len))
        self.sock.sendall(header + mask_key + masked_payload)

    def _recv_frame(self, timeout=None):
        if self.sock is None:
            raise RuntimeError("socket is not connected")

        if timeout is not None:
            self.sock.settimeout(timeout)

        header = self._read_exact(2)
        byte1, byte2 = header[0], header[1]
        opcode = byte1 & 0x0F
        masked = (byte2 & 0x80) != 0
        payload_len = byte2 & 0x7F

        if payload_len == 126:
            payload_len = int.from_bytes(self._read_exact(2), "big")
        elif payload_len == 127:
            payload_len = int.from_bytes(self._read_exact(8), "big")

        mask_key = self._read_exact(4) if masked else b""
        payload = self._read_exact(payload_len) if payload_len > 0 else b""

        if masked:
            payload = bytes(payload[i] ^ mask_key[i % 4] for i in range(payload_len))

        return opcode, payload

    def _read_exact(self, n):
        chunks = []
        received = 0

        while received < n:
            try:
                chunk = self.sock.recv(n - received)
            except socket.timeout as e:
                raise TimeoutError("socket read timeout") from e

            if not chunk:
                raise WebSocketClosed("socket closed")

            chunks.append(chunk)
            received += len(chunk)

        return b"".join(chunks)

    def _recv_until(self, marker):
        data = b""
        while marker not in data:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise RuntimeError("connection closed before HTTP headers complete")
            data += chunk
        return data


class TestWsEvents:
    """M4 WebSocket 测试集合。"""

    @staticmethod
    def _runtime_hub_info():
        runtime_dir = DiscoveryService.get_runtime_directory()
        hub_json_path = os.path.join(runtime_dir, "hub.json")

        with open(hub_json_path, "r", encoding="utf-8") as f:
            hub_info = json.load(f)

        with open(hub_info["tokenFile"], "r", encoding="utf-8") as f:
            token = f.read().strip()

        return hub_info["httpBaseUrl"], hub_info["wsUrl"], token

    @staticmethod
    def _new_instance_id(prefix):
        return f"{prefix}-{uuid.uuid4().hex[:10]}"

    @staticmethod
    def _new_app_id(suffix):
        return f"m4-ws-{suffix}-{uuid.uuid4().hex[:6]}"

    def _authenticate(self, ws, token, request_id="ws-auth-1"):
        ws.send_json({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "hub.ws.authenticate",
            "params": {
                "token": token,
                "protocolVersion": 1,
                "clientId": "PyWsTestClient",
                "clientSessionId": str(uuid.uuid4())
            }
        })
        return ws.recv_json(timeout=3)

    @staticmethod
    def _collect_event_types(ws, expected_types, timeout_sec=6):
        deadline = time.time() + timeout_sec
        found_types = []

        while time.time() < deadline and not expected_types.issubset(set(found_types)):
            timeout = max(0.1, deadline - time.time())
            try:
                message = ws.recv_json(timeout=timeout)
            except TimeoutError:
                continue
            except WebSocketClosed:
                break

            if isinstance(message, dict) and message.get("method") == "hub.event":
                event_type = message.get("params", {}).get("type")
                if isinstance(event_type, str):
                    found_types.append(event_type)

        return found_types

    def test_m4_ws_001_first_message_must_authenticate(self):
        """M4-WS-001: 首条非鉴权请求（带 id）应返回 unauthorized。"""
        result = TestResult("M4-WS-001 首条非鉴权请求应返回 unauthorized")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "pre-auth-req",
                    "method": "hub.ping",
                    "params": {}
                })
                response = ws.recv_json(timeout=3)

                if not RpcAssertions.expect_error(result, response, -32001, "unauthorized", expected_id="pre-auth-req"):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_002_pre_auth_notification_should_close_connection(self):
        """M4-WS-002: 鉴权前非鉴权通知（无 id）应关闭连接。"""
        result = TestResult("M4-WS-002 鉴权前通知应触发断连")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "method": "hub.events.subscribe",
                    "params": {}
                })

                closed = ws.wait_for_close(timeout=2)
                if not closed:
                    result.mark_failure("❌ 鉴权前通知后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_003_authenticate_invalid_token_should_close(self):
        """M4-WS-003: 非法 token 鉴权返回 unauthorized 并断开连接。"""
        result = TestResult("M4-WS-003 非法 token 鉴权")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "bad-auth",
                    "method": "hub.ws.authenticate",
                    "params": {
                        "token": f"invalid-{token}",
                        "protocolVersion": 1,
                        "clientId": "PyWsTestClient",
                        "clientSessionId": str(uuid.uuid4())
                    }
                })

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32001, "unauthorized", expected_id="bad-auth"):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 非法 token 鉴权后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_004_subscribe_unsubscribe_should_work_after_auth(self):
        """M4-WS-004: 鉴权后可 subscribe/unsubscribe。"""
        result = TestResult("M4-WS-004 鉴权后订阅与取消订阅")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-4")
                if not RpcAssertions.expect_success(result, auth_response, ["protocolVersion"]):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-4",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.instance.registered", "invocation.completed"]
                    }
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result

                subscription_id = subscribe_response["result"].get("subscriptionId")
                if not isinstance(subscription_id, str) or not subscription_id:
                    result.mark_failure(f"❌ subscriptionId 非法: {subscribe_response}")
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "unsub-4",
                    "method": "hub.events.unsubscribe",
                    "params": {
                        "subscriptionId": subscription_id
                    }
                })
                unsubscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, unsubscribe_response):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_005_should_push_registered_delivered_completed_events(self):
        """M4-WS-005: 订阅后应收到 registered/queued/delivered/completed。"""
        result = TestResult("M4-WS-005 事件推送 completed 主链路")

        instance_id = self._new_instance_id("m4-ws-event-completed")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)

            app_id = self._new_app_id("completed")

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-5")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-5",
                    "method": "hub.events.subscribe",
                    "params": {}
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result

                register_response = rpc_client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope=None,
                    poll=True,
                    respond=True,
                    pid=6201,
                )
                if not RpcAssertions.expect_success(result, register_response):
                    return result

                notify_response = rpc_client.invoke_notify(
                    app_id=app_id,
                    method="demo.notify",
                    args={"k": 1},
                    target_scope=None,
                    target_instance_id=instance_id,
                    ttl_ms=60000,
                    queue_if_offline=True,
                    auto_launch=False,
                    request_id="notify-5",
                )
                if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                    return result

                poll_response = rpc_client.poll_once(instance_id, max_count=1, wait_ms=50)
                if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                    return result

                items = poll_response["result"].get("items", [])
                if len(items) != 1:
                    result.mark_failure(f"❌ poll 未取到预期 invocation: {poll_response}")
                    return result

                invocation_id = items[0].get("invocationId")
                respond_response = rpc_client.respond_value(instance_id, invocation_id, {"ok": True})
                if not RpcAssertions.expect_success(result, respond_response):
                    return result

                expected_types = {
                    "app.instance.registered",
                    "invocation.queued",
                    "invocation.delivered",
                    "invocation.completed",
                }
                found_types = self._collect_event_types(ws, expected_types, timeout_sec=6)

                if not expected_types.issubset(set(found_types)):
                    result.mark_failure(f"❌ 事件类型不完整: expected={sorted(expected_types)}, actual={found_types}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                http_base_url, _, token = self._runtime_hub_info()
                RpcClient(http_base_url, token).unregister_instance(instance_id)
            except Exception:
                pass

        return result

    def test_m4_ws_006_should_push_failed_event(self):
        """M4-WS-006: 被调用方回传 error 应触发 invocation.failed。"""
        result = TestResult("M4-WS-006 事件推送 failed 主链路")

        instance_id = self._new_instance_id("m4-ws-event-failed")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)

            app_id = self._new_app_id("failed")

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-6")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-6",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["invocation.failed"]
                    }
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result

                register_response = rpc_client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope="workspace-A",
                    poll=True,
                    respond=True,
                    pid=6202,
                )
                if not RpcAssertions.expect_success(result, register_response):
                    return result

                notify_response = rpc_client.invoke_notify(
                    app_id=app_id,
                    method="demo.notify",
                    args={"k": 2},
                    target_scope="workspace-A",
                    target_instance_id=instance_id,
                    ttl_ms=60000,
                    queue_if_offline=True,
                    auto_launch=False,
                    request_id="notify-6",
                )
                if not RpcAssertions.expect_success(result, notify_response, ["invocationId"]):
                    return result

                poll_response = rpc_client.poll_once(instance_id, max_count=1, wait_ms=50)
                if not RpcAssertions.expect_success(result, poll_response, ["items"]):
                    return result

                items = poll_response["result"].get("items", [])
                if len(items) != 1:
                    result.mark_failure(f"❌ poll 未取到预期 invocation: {poll_response}")
                    return result

                invocation_id = items[0].get("invocationId")
                respond_response = rpc_client.respond_error(instance_id, invocation_id, {
                    "code": 1001,
                    "message": "app_error",
                    "data": {"reason": "mock"}
                })
                if not RpcAssertions.expect_success(result, respond_response):
                    return result

                expected_types = {"invocation.failed"}
                found_types = self._collect_event_types(ws, expected_types, timeout_sec=6)
                if not expected_types.issubset(set(found_types)):
                    result.mark_failure(f"❌ 未收到 invocation.failed: {found_types}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                http_base_url, _, token = self._runtime_hub_info()
                RpcClient(http_base_url, token).unregister_instance(instance_id)
            except Exception:
                pass

        return result

    def run_all_tests(self, full=False):
        results = [
            self.test_m4_ws_001_first_message_must_authenticate(),
            self.test_m4_ws_002_pre_auth_notification_should_close_connection(),
            self.test_m4_ws_003_authenticate_invalid_token_should_close(),
            self.test_m4_ws_004_subscribe_unsubscribe_should_work_after_auth(),
            self.test_m4_ws_005_should_push_registered_delivered_completed_events(),
        ]

        if full:
            results.append(self.test_m4_ws_006_should_push_failed_event())

        return results


if __name__ == "__main__":
    test = TestWsEvents()
    results = test.run_all_tests(full=True)

    for result in results:
        status = "✅ 通过" if result.success else "❌ 失败"
        print(f"{status}: {result.test_name}")
        if result.details:
            for detail in result.details:
                print(f"  - {detail}")
        if result.error_message:
            print(f"  错误: {result.error_message}")
        print()
