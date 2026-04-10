#!/usr/bin/env python3
"""
DevHub M4 WebSocket 鉴权与事件测试
"""

import os
import json
import uuid
import time
import socket
import base64
import hashlib
import ssl
from urllib.parse import urlparse


from tests.blackbox.test_base import (
    RpcClient,
    RpcAssertions,
    TestResult,
    get_runtime_hub_info,
    new_instance_id,
)


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
        if parsed.scheme not in {"ws", "wss"}:
            raise ValueError(f"Only ws:// or wss:// is supported in tests, got: {self.ws_url}")

        host = parsed.hostname or "127.0.0.1"
        port = parsed.port or (443 if parsed.scheme == "wss" else 80)
        path = parsed.path or "/"
        if parsed.query:
            path = f"{path}?{parsed.query}"

        raw_sock = socket.create_connection((host, port), timeout=self.timeout)
        try:
            if parsed.scheme == "wss":
                tls_context = ssl.create_default_context()
                tls_context.check_hostname = False
                tls_context.verify_mode = ssl.CERT_NONE
                self.sock = tls_context.wrap_socket(raw_sock, server_hostname=host)
            else:
                self.sock = raw_sock
        except Exception:
            raw_sock.close()
            raise
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

    def send_text(self, text):
        self._send_frame(0x1, text.encode("utf-8"))

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
        return get_runtime_hub_info()

    @staticmethod
    def _new_instance_id(prefix):
        return new_instance_id(prefix)

    @staticmethod
    def _new_app_id(suffix):
        return f"m4-ws-{suffix}-{uuid.uuid4().hex[:6]}"

    @staticmethod
    def _definition_payload(app_id, display_name=None):
        return {
            "appId": app_id,
            "displayName": display_name or app_id,
        }

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
    def _assert_event_notification_contract(result: TestResult, message: dict, expected_subscription_id=None):
        if not isinstance(message, dict):
            result.mark_failure(f"❌ 事件消息不是对象: {message}")
            return None

        if message.get("jsonrpc") != "2.0":
            result.mark_failure(f"❌ 事件消息 jsonrpc 非 2.0: {message}")
            return None

        if message.get("method") != "hub.event":
            result.mark_failure(f"❌ 事件消息 method 非 hub.event: {message}")
            return None

        params = message.get("params")
        if not isinstance(params, dict):
            result.mark_failure(f"❌ 事件消息 params 非对象: {message}")
            return None

        subscription_id = params.get("subscriptionId")
        if not isinstance(subscription_id, str) or not subscription_id:
            result.mark_failure(f"❌ 事件消息缺少有效 subscriptionId: {message}")
            return None

        if expected_subscription_id is not None and subscription_id != expected_subscription_id:
            result.mark_failure(
                f"❌ 事件消息 subscriptionId 不匹配: expected={expected_subscription_id}, actual={subscription_id}, message={message}")
            return None

        event_type = params.get("type")
        if not isinstance(event_type, str) or not event_type:
            result.mark_failure(f"❌ 事件消息缺少有效 type: {message}")
            return None

        time_utc = params.get("timeUtc")
        if not isinstance(time_utc, str) or not time_utc:
            result.mark_failure(f"❌ 事件消息缺少有效 timeUtc: {message}")
            return None

        if not time_utc.endswith("Z"):
            result.mark_failure(f"❌ 事件消息 timeUtc 非 UTC RFC3339 格式: {message}")
            return None

        payload = params.get("payload")
        if not isinstance(payload, dict):
            result.mark_failure(f"❌ 事件消息 payload 非对象: {message}")
            return None

        return params

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

    def _collect_and_validate_event_types(self, result: TestResult, ws, expected_types, expected_subscription_id=None, timeout_sec=6):
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

            if not isinstance(message, dict) or message.get("method") != "hub.event":
                continue

            params = self._assert_event_notification_contract(result, message, expected_subscription_id)
            if params is None:
                return None

            event_type = params.get("type")
            if isinstance(event_type, str):
                found_types.append(event_type)

        return found_types

    def _wait_for_event(self, result: TestResult, ws, expected_type, expected_subscription_id=None, timeout_sec=6):
        deadline = time.time() + timeout_sec

        while time.time() < deadline:
            timeout = max(0.1, deadline - time.time())
            try:
                message = ws.recv_json(timeout=timeout)
            except TimeoutError:
                continue
            except WebSocketClosed:
                break

            if not isinstance(message, dict) or message.get("method") != "hub.event":
                continue

            params = self._assert_event_notification_contract(result, message, expected_subscription_id)
            if params is None:
                return None

            if params.get("type") == expected_type:
                return params

        result.mark_failure(f"❌ 未收到 {expected_type} 事件")
        return None

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

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 首条非鉴权请求返回 unauthorized 后连接未关闭")
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
                if not RpcAssertions.expect_error(
                    result,
                    response,
                    -32001,
                    "unauthorized",
                    expected_id="bad-auth",
                    expected_data={"reason": "invalid_token"},
                ):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 非法 token 鉴权后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_004_authenticate_unsupported_protocol_should_close(self):
        """M4-WS-004: 协议版本不匹配应返回 not_supported 并断连。"""
        result = TestResult("M4-WS-004 协议版本不匹配鉴权")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "bad-protocol-auth",
                    "method": "hub.ws.authenticate",
                    "params": {
                        "token": token,
                        "protocolVersion": 2,
                        "clientId": "PyWsTestClient",
                        "clientSessionId": str(uuid.uuid4())
                    }
                })

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32099, "not_supported", expected_id="bad-protocol-auth"):
                    return result

                if not RpcAssertions.expect_error_data_fields(result, response, {"expected": 1, "reason": "mismatch"}):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 协议版本不匹配鉴权后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_005_subscribe_unsubscribe_should_work_after_auth(self):
        """M4-WS-005: 鉴权后可 subscribe/unsubscribe。"""
        result = TestResult("M4-WS-005 鉴权后订阅与取消订阅")

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

    def test_m4_ws_006_should_push_registered_delivered_completed_events(self):
        """M4-WS-006: 订阅后应收到 registered/queued/delivered/completed。"""
        result = TestResult("M4-WS-006 事件推送 completed 主链路")

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
                subscription_id = subscribe_response["result"].get("subscriptionId")

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
                found_types = self._collect_and_validate_event_types(
                    result,
                    ws,
                    expected_types,
                    expected_subscription_id=subscription_id,
                    timeout_sec=6,
                )
                if found_types is None:
                    return result

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

    def test_m4_ws_007_reconnect_after_disconnect_should_receive_events(self):
        """M4-WS-007: 断线后重连并重新订阅，事件链路应保持稳定。"""
        result = TestResult("M4-WS-007 断线后重连订阅稳定性")

        instance_id = self._new_instance_id("m4-ws-reconnect")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)
            app_id = self._new_app_id("reconnect")

            with SimpleWebSocketClient(ws_url) as ws1:
                auth_response = self._authenticate(ws1, token, request_id="auth-7-1")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws1.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-7-1",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.instance.registered"]
                    }
                })
                subscribe_response = ws1.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result

            reconnect_deadline = time.time() + 3
            ws2 = None
            subscription_id = None

            while time.time() < reconnect_deadline:
                try:
                    ws2 = SimpleWebSocketClient(ws_url)
                    ws2.connect()

                    auth_response = self._authenticate(ws2, token, request_id="auth-7-2")
                    if not RpcAssertions.expect_success(result, auth_response):
                        ws2.close()
                        return result

                    ws2.send_json({
                        "jsonrpc": "2.0",
                        "id": "sub-7-2",
                        "method": "hub.events.subscribe",
                        "params": {
                            "types": ["app.instance.registered"]
                        }
                    })
                    subscribe_response = ws2.recv_json(timeout=3)
                    if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                        ws2.close()
                        return result

                    subscription_id = subscribe_response["result"].get("subscriptionId")
                    break
                except (TimeoutError, WebSocketClosed, RuntimeError):
                    if ws2 is not None:
                        ws2.close()
                    ws2 = None

            if ws2 is None or not subscription_id:
                result.mark_failure("Reconnect did not finish before timeout")
                return result

            try:
                register_response = rpc_client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope=None,
                    poll=True,
                    respond=True,
                    pid=6203,
                )
                if not RpcAssertions.expect_success(result, register_response):
                    return result

                expected_types = {"app.instance.registered"}
                found_types = self._collect_and_validate_event_types(
                    result,
                    ws2,
                    expected_types,
                    expected_subscription_id=subscription_id,
                    timeout_sec=6,
                )
                if found_types is None:
                    return result
                if not expected_types.issubset(set(found_types)):
                    result.mark_failure(f"Missing expected events after reconnect: {found_types}")
                    return result
            finally:
                ws2.close()

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

    def test_m4_ws_008_should_push_failed_event(self):
        """M4-WS-008: 被调用方回传 error 应触发 invocation.failed。"""
        result = TestResult("M4-WS-008 事件推送 failed 主链路")

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
                subscription_id = subscribe_response["result"].get("subscriptionId")

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
                found_types = self._collect_and_validate_event_types(
                    result,
                    ws,
                    expected_types,
                    expected_subscription_id=subscription_id,
                    timeout_sec=6,
                )
                if found_types is None:
                    return result
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

    def test_m4_ws_009_pre_auth_request_array_params_should_unauthorized_and_close(self):
        """M4-WS-009: 未鉴权首条非鉴权请求（params=[]）应 unauthorized 并断连。"""
        result = TestResult("M4-WS-009 未鉴权请求(params=[])应 unauthorized 并断连")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "pre-auth-array-req",
                    "method": "hub.ping",
                    "params": []
                })

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32001, "unauthorized", expected_id="pre-auth-array-req"):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 未鉴权请求(params=[])返回 unauthorized 后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_010_pre_auth_notification_array_params_should_close(self):
        """M4-WS-010: 未鉴权非鉴权通知（params=[]）应直接断连。"""
        result = TestResult("M4-WS-010 未鉴权通知(params=[])应断连")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "method": "hub.events.subscribe",
                    "params": []
                })

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 未鉴权通知(params=[])后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_011_subscribe_unknown_event_type_should_invalid_params(self):
        """M4-WS-011: 订阅未知事件类型应返回 invalid_params。"""
        result = TestResult("M4-WS-011 订阅未知事件类型返回 invalid_params")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-11")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-11",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["unknown.type"]
                    }
                })

                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, subscribe_response, -32602, "invalid_params", expected_id="sub-11"):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_012_should_push_unregistered_event(self):
        """M4-WS-012: 注销实例应推送 app.instance.unregistered。"""
        result = TestResult("M4-WS-012 事件推送 app.instance.unregistered")

        instance_id = self._new_instance_id("m4-ws-event-unregistered")
        app_id = self._new_app_id("unregistered")
        scope = "workspace-unregistered"

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-12")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-12",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.instance.unregistered"]
                    }
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result
                subscription_id = subscribe_response["result"].get("subscriptionId")

                register_response = rpc_client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope=scope,
                    poll=True,
                    respond=True,
                    pid=6204,
                )
                if not RpcAssertions.expect_success(result, register_response):
                    return result

                unregister_response = rpc_client.unregister_instance(instance_id)
                if not RpcAssertions.expect_success(result, unregister_response):
                    return result

                deadline = time.time() + 6
                while time.time() < deadline:
                    timeout = max(0.1, deadline - time.time())
                    try:
                        message = ws.recv_json(timeout=timeout)
                    except TimeoutError:
                        continue

                    if not isinstance(message, dict) or message.get("method") != "hub.event":
                        continue

                    event_params = self._assert_event_notification_contract(
                        result,
                        message,
                        expected_subscription_id=subscription_id,
                    )
                    if event_params is None:
                        return result

                    if event_params.get("type") != "app.instance.unregistered":
                        continue

                    payload = event_params.get("payload", {})
                    if not isinstance(payload, dict):
                        result.mark_failure(f"❌ payload 非对象: {message}")
                        return result

                    if payload.get("appId") != app_id:
                        result.mark_failure(f"❌ unregistered payload.appId 不匹配: {payload}")
                        return result

                    if payload.get("instanceId") != instance_id:
                        result.mark_failure(f"❌ unregistered payload.instanceId 不匹配: {payload}")
                        return result

                    if payload.get("scope") != scope:
                        result.mark_failure(f"❌ unregistered payload.scope 不匹配: {payload}")
                        return result

                    result.mark_success()
                    return result

                result.mark_failure("❌ 未收到 app.instance.unregistered 事件")
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                http_base_url, _, token = self._runtime_hub_info()
                RpcClient(http_base_url, token).unregister_instance(instance_id)
            except Exception:
                pass

        return result

    def test_m4_ws_012d_should_push_definition_upserted_event(self):
        """M4-WS-012D: upsertDefinition 成功后应推送 app.definition.upserted。"""
        result = TestResult("M4-WS-012D 事件推送 app.definition.upserted")
        app_id = self._new_app_id("definition-upserted")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)
            definition = self._definition_payload(app_id, "Definition Upserted Event")

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-12d")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-12d",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.definition.upserted"]
                    }
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result
                subscription_id = subscribe_response["result"].get("subscriptionId")

                upsert_response = rpc_client.call("hub.apps.upsertDefinition", {"definition": definition}, request_id="upsert-12d")
                if not RpcAssertions.expect_success(result, upsert_response, ["definition"]):
                    return result

                event_params = self._wait_for_event(
                    result,
                    ws,
                    "app.definition.upserted",
                    expected_subscription_id=subscription_id,
                    timeout_sec=6,
                )
                if event_params is None:
                    return result

                payload = event_params.get("payload", {})
                if payload.get("appId") != app_id:
                    result.mark_failure(f"❌ upserted payload.appId 不匹配: {payload}")
                    return result

                event_definition = payload.get("definition")
                if not isinstance(event_definition, dict):
                    result.mark_failure(f"❌ upserted payload.definition 非对象: {payload}")
                    return result

                if event_definition.get("appId") != app_id:
                    result.mark_failure(f"❌ upserted payload.definition.appId 不匹配: {payload}")
                    return result

                if event_definition.get("displayName") != definition["displayName"]:
                    result.mark_failure(f"❌ upserted payload.definition.displayName 不匹配: {payload}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))
        finally:
            try:
                http_base_url, _, token = self._runtime_hub_info()
                RpcClient(http_base_url, token).call("hub.apps.deleteDefinition", {"appId": app_id}, request_id="cleanup-12d")
            except Exception:
                pass

        return result

    def test_m4_ws_012e_should_push_definition_deleted_event(self):
        """M4-WS-012E: deleteDefinition 成功后应推送 app.definition.deleted。"""
        result = TestResult("M4-WS-012E 事件推送 app.definition.deleted")
        app_id = self._new_app_id("definition-deleted")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)
            definition = self._definition_payload(app_id, "Definition Deleted Event")

            seed_response = rpc_client.call("hub.apps.upsertDefinition", {"definition": definition}, request_id="seed-12e")
            if not RpcAssertions.expect_success(result, seed_response, ["definition"]):
                return result

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-12e")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-12e",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.definition.deleted"]
                    }
                })
                subscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, subscribe_response, ["subscriptionId"]):
                    return result
                subscription_id = subscribe_response["result"].get("subscriptionId")

                delete_response = rpc_client.call("hub.apps.deleteDefinition", {"appId": app_id}, request_id="delete-12e")
                if not RpcAssertions.expect_success(result, delete_response):
                    return result

                event_params = self._wait_for_event(
                    result,
                    ws,
                    "app.definition.deleted",
                    expected_subscription_id=subscription_id,
                    timeout_sec=6,
                )
                if event_params is None:
                    return result

                payload = event_params.get("payload", {})
                if payload.get("appId") != app_id:
                    result.mark_failure(f"❌ deleted payload.appId 不匹配: {payload}")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_012b_unsubscribe_unknown_id_should_be_idempotent(self):
        """M4-WS-012B: 取消订阅未知 subscriptionId 仍应返回 ok。"""
        result = TestResult("M4-WS-012B unknown subscriptionId 取消订阅幂等")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-12b")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "unsub-12b",
                    "method": "hub.events.unsubscribe",
                    "params": {
                        "subscriptionId": "sub-unknown-idempotent-001"
                    }
                })

                unsubscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, unsubscribe_response):
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_013_pre_auth_invalid_json_should_parse_error(self):
        """M4-WS-013: 鉴权前非法 JSON 应返回 parse_error 并断连。"""
        result = TestResult("M4-WS-013 鉴权前非法JSON返回 parse_error")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_text('{"jsonrpc":"2.0","id":"bad-json-13","method":"hub.ping","params":')

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32700, "parse_error", expected_id=None):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 鉴权前非法 JSON 返回 parse_error 后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_014_pre_auth_invalid_envelope_should_invalid_request(self):
        """M4-WS-014: 鉴权前非法信封应返回 invalid_request 并断连。"""
        result = TestResult("M4-WS-014 鉴权前非法信封返回 invalid_request")

        try:
            _, ws_url, _ = self._runtime_hub_info()

            cases = [
                {
                    "name": "缺少 method",
                    "id": "bad-envelope-14-1",
                    "payload": {
                        "jsonrpc": "2.0",
                        "id": "bad-envelope-14-1",
                        "params": {}
                    }
                },
                {
                    "name": "jsonrpc 非 2.0",
                    "id": "bad-envelope-14-2",
                    "payload": {
                        "jsonrpc": "1.0",
                        "id": "bad-envelope-14-2",
                        "method": "hub.ping",
                        "params": {}
                    }
                }
            ]

            for case in cases:
                with SimpleWebSocketClient(ws_url) as ws:
                    ws.send_json(case["payload"])

                    response = ws.recv_json(timeout=3)
                    if not RpcAssertions.expect_error(result, response, -32600, "invalid_request", expected_id=case["id"]):
                        return result

                    if not ws.wait_for_close(timeout=2):
                        result.mark_failure(f"❌ {case['name']} 返回 invalid_request 后连接未关闭")
                        return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_012c_unsubscribe_existing_id_should_stop_delivery(self):
        """M4-WS-012C: 取消真实 subscriptionId 后不应再收到 hub.event。"""
        result = TestResult("M4-WS-012C 真实 subscriptionId 取消后停止事件投递")

        instance_id = self._new_instance_id("m4-ws-unsub-stop")
        app_id = self._new_app_id("unsub-stop")

        try:
            http_base_url, ws_url, token = self._runtime_hub_info()
            rpc_client = RpcClient(http_base_url, token)

            with SimpleWebSocketClient(ws_url) as ws:
                auth_response = self._authenticate(ws, token, request_id="auth-12c")
                if not RpcAssertions.expect_success(result, auth_response):
                    return result

                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "sub-12c",
                    "method": "hub.events.subscribe",
                    "params": {
                        "types": ["app.instance.registered"]
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
                    "id": "unsub-12c",
                    "method": "hub.events.unsubscribe",
                    "params": {
                        "subscriptionId": subscription_id
                    }
                })
                unsubscribe_response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_success(result, unsubscribe_response):
                    return result

                register_response = rpc_client.register_instance(
                    instance_id=instance_id,
                    app_id=app_id,
                    scope="workspace-unsub-stop",
                    poll=True,
                    respond=True,
                    pid=6205,
                )
                if not RpcAssertions.expect_success(result, register_response):
                    return result

                deadline = time.time() + 2
                while time.time() < deadline:
                    timeout = max(0.1, deadline - time.time())
                    try:
                        message = ws.recv_json(timeout=timeout)
                    except TimeoutError:
                        continue
                    except WebSocketClosed:
                        result.mark_failure("❌ 取消订阅后连接异常关闭")
                        return result

                    if isinstance(message, dict) and message.get("method") == "hub.event":
                        result.mark_failure(f"❌ 取消订阅后仍收到 hub.event: {message}")
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

    def test_m4_ws_015_first_authenticate_without_id_should_invalid_request(self):
        """M4-WS-015: 首条 hub.ws.authenticate 缺失 id 应 invalid_request 并断连。"""
        result = TestResult("M4-WS-015 首条鉴权缺失id返回 invalid_request")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "method": "hub.ws.authenticate",
                    "params": {
                        "token": token,
                        "protocolVersion": 1,
                        "clientId": "PyWsTestClient",
                        "clientSessionId": str(uuid.uuid4())
                    }
                })

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32600, "invalid_request", expected_id=None):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 首条鉴权缺失 id 返回 invalid_request 后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_016_pre_auth_batch_root_array_should_invalid_request(self):
        """M4-WS-016: 鉴权前根数组 batch 应返回单一 invalid_request 并断连。"""
        result = TestResult("M4-WS-016 鉴权前根数组batch返回 invalid_request")

        try:
            _, ws_url, _ = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json([
                    {
                        "jsonrpc": "2.0",
                        "id": "batch-16",
                        "method": "hub.ping",
                        "params": {}
                    }
                ])

                response = ws.recv_json(timeout=3)
                if not isinstance(response, dict):
                    result.mark_failure(f"❌ 根数组 batch 响应不是单一 JSON-RPC 对象: {response}")
                    return result

                if not RpcAssertions.expect_error(result, response, -32600, "invalid_request", expected_id=None):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 根数组 batch 返回 invalid_request 后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_m4_ws_017_authenticate_invalid_params_should_close_and_block_retry(self):
        """M4-WS-017: 首条鉴权 invalid_params 后必须断连，不能在同连接重试。"""
        result = TestResult("M4-WS-017 鉴权 invalid_params 后断连且禁止重试")

        try:
            _, ws_url, token = self._runtime_hub_info()
            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_json({
                    "jsonrpc": "2.0",
                    "id": "bad-auth-params",
                    "method": "hub.ws.authenticate",
                    "params": {
                        "token": token,
                        "protocolVersion": 1,
                        "clientSessionId": str(uuid.uuid4())
                    }
                })

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="bad-auth-params"):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 鉴权 invalid_params 后连接未关闭")
                    return result

            with SimpleWebSocketClient(ws_url) as ws:
                ws.send_text('{"jsonrpc":"2.0","id":"bad-auth-array","method":"hub.ws.authenticate","params":[1,2,3]}')

                response = ws.recv_json(timeout=3)
                if not RpcAssertions.expect_error(result, response, -32602, "invalid_params", expected_id="bad-auth-array"):
                    return result

                if not ws.wait_for_close(timeout=2):
                    result.mark_failure("❌ 鉴权 params 数组 invalid_params 后连接未关闭")
                    return result

            result.mark_success()
        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
        results = [
            self.test_m4_ws_001_first_message_must_authenticate(),
            self.test_m4_ws_002_pre_auth_notification_should_close_connection(),
            self.test_m4_ws_003_authenticate_invalid_token_should_close(),
            self.test_m4_ws_004_authenticate_unsupported_protocol_should_close(),
            self.test_m4_ws_005_subscribe_unsubscribe_should_work_after_auth(),
            self.test_m4_ws_006_should_push_registered_delivered_completed_events(),
            self.test_m4_ws_007_reconnect_after_disconnect_should_receive_events(),
            self.test_m4_ws_009_pre_auth_request_array_params_should_unauthorized_and_close(),
            self.test_m4_ws_010_pre_auth_notification_array_params_should_close(),
            self.test_m4_ws_011_subscribe_unknown_event_type_should_invalid_params(),
            self.test_m4_ws_012_should_push_unregistered_event(),
            self.test_m4_ws_012b_unsubscribe_unknown_id_should_be_idempotent(),
            self.test_m4_ws_012c_unsubscribe_existing_id_should_stop_delivery(),
            self.test_m4_ws_012d_should_push_definition_upserted_event(),
            self.test_m4_ws_012e_should_push_definition_deleted_event(),
            self.test_m4_ws_013_pre_auth_invalid_json_should_parse_error(),
            self.test_m4_ws_014_pre_auth_invalid_envelope_should_invalid_request(),
            self.test_m4_ws_015_first_authenticate_without_id_should_invalid_request(),
            self.test_m4_ws_016_pre_auth_batch_root_array_should_invalid_request(),
            self.test_m4_ws_017_authenticate_invalid_params_should_close_and_block_retry(),
        ]

        if full:
            results.append(self.test_m4_ws_008_should_push_failed_event())

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
