from __future__ import annotations

import asyncio
import json
from typing import Any, AsyncIterator, Sequence
from uuid import uuid4

import websockets

from ._jsonrpc import validate_response_envelope
from ._parsing import parse_event, require_bool, require_mapping, require_str
from .models import DevHubClientOptions, DevHubEvent, HubRuntime
from .runtime import discover_runtime


_SENTINEL = object()


class DevHubEventsClient:
    """DevHub WebSocket 事件客户端。"""

    def __init__(self, options: DevHubClientOptions) -> None:
        """初始化事件客户端。"""

        self._options = options.clone()
        self._options.validate()
        self._connection_info = discover_runtime(self._options)
        self._websocket = None
        self._authenticated = False
        self._send_lock = asyncio.Lock()
        self._pending: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._events: asyncio.Queue[DevHubEvent | object] = asyncio.Queue()
        self._receiver_task: asyncio.Task[None] | None = None
        self._terminal_error: BaseException | None = None
        self._stream_completed = False

    @classmethod
    async def from_runtime(cls, options: DevHubClientOptions) -> "DevHubEventsClient":
        """根据运行时目录创建事件客户端。"""

        return cls(options)

    @property
    def runtime(self) -> HubRuntime:
        """返回发现到的运行时信息。"""

        return self._connection_info.runtime

    async def authenticate(self) -> None:
        """执行 `hub.ws.authenticate`。"""

        result = await self._send_request(
            "hub.ws.authenticate",
            {
                "token": self._connection_info.token,
                "protocolVersion": self._options.protocol_version,
                "clientId": self._options.client_id,
                "clientSessionId": self._options.client_session_id,
            },
            require_authenticated=False,
        )
        root = require_mapping(result, "hub.ws.authenticate.result")
        if not require_bool(root, "ok", "hub.ws.authenticate.result"):
            raise RuntimeError("hub.ws.authenticate 返回结果非法。")
        protocol_version = root.get("protocolVersion")
        if protocol_version != 1:
            raise RuntimeError("hub.ws.authenticate 返回结果非法。")
        self._authenticated = True

    async def subscribe(self, types: Sequence[str] | None = None) -> str:
        """订阅事件。"""

        params: dict[str, Any] = {}
        if types is not None:
            types_list = list(types)
            if any(not isinstance(item, str) or not item.strip() for item in types_list):
                raise ValueError("types 只能包含非空字符串。")
            if types_list:
                params["types"] = types_list
        result = await self._send_request("hub.events.subscribe", params, require_authenticated=True)
        root = require_mapping(result, "hub.events.subscribe.result")
        if not require_bool(root, "ok", "hub.events.subscribe.result"):
            raise RuntimeError("hub.events.subscribe 返回结果非法。")
        return require_str(root, "subscriptionId", "hub.events.subscribe.result")

    async def unsubscribe(self, subscription_id: str) -> None:
        """取消订阅。"""

        if not subscription_id or not subscription_id.strip():
            raise ValueError("subscription_id 不能为空。")
        result = await self._send_request(
            "hub.events.unsubscribe",
            {"subscriptionId": subscription_id},
            require_authenticated=True,
        )
        root = require_mapping(result, "hub.events.unsubscribe.result")
        if not require_bool(root, "ok", "hub.events.unsubscribe.result"):
            raise RuntimeError("hub.events.unsubscribe 返回结果非法。")

    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        """读取事件流。"""

        while True:
            item = await self._events.get()
            if item is _SENTINEL:
                if self._terminal_error is not None:
                    raise self._terminal_error
                return
            yield item

    async def close(self) -> None:
        """关闭事件客户端。"""

        if self._websocket is not None:
            await self._websocket.close()
            self._websocket = None
        if self._receiver_task is not None:
            self._receiver_task.cancel()
            try:
                await self._receiver_task
            except asyncio.CancelledError:
                pass
            self._receiver_task = None
        self._complete_event_stream()

    async def __aenter__(self) -> "DevHubEventsClient":
        """异步上下文入口。"""

        return self

    async def __aexit__(self, exc_type, exc, tb) -> None:
        """异步上下文出口。"""

        await self.close()

    async def _ensure_connected(self) -> None:
        if self._websocket is not None:
            return
        self._websocket = await websockets.connect(
            self._connection_info.websocket_endpoint,
            open_timeout=self._options.request_timeout,
            close_timeout=self._options.request_timeout,
        )
        self._receiver_task = asyncio.create_task(self._run_receive_loop())

    async def _send_request(self, method: str, params: dict[str, Any] | None, *, require_authenticated: bool) -> dict[str, Any]:
        if require_authenticated and not self._authenticated:
            raise RuntimeError("当前 WebSocket 尚未通过鉴权。")

        await self._ensure_connected()
        if self._terminal_error is not None:
            raise RuntimeError("事件流已终止。") from self._terminal_error

        request_id = f"ws-{uuid4().hex}"
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future

        payload_dict = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
        }
        if params is not None:
            payload_dict["params"] = params
        payload = json.dumps(payload_dict)
        async with self._send_lock:
            await self._websocket.send(payload)

        try:
            if self._options.request_timeout is None:
                envelope = await future
            else:
                envelope = await asyncio.wait_for(future, timeout=self._options.request_timeout)
        finally:
            self._pending.pop(request_id, None)
        return validate_response_envelope(envelope, request_id)

    async def _run_receive_loop(self) -> None:
        terminal_error: BaseException | None = None
        try:
            async for message in self._websocket:
                await self._handle_message(message)
        except asyncio.CancelledError:
            raise
        except BaseException as exc:
            terminal_error = exc
            self._terminal_error = exc
        finally:
            if self._pending:
                error = terminal_error or RuntimeError("WebSocket 连接已关闭。")
                self._fail_pending(error)
            self._complete_event_stream()

    async def _handle_message(self, message: Any) -> None:
        if not isinstance(message, str):
            raise RuntimeError("WebSocket 消息必须为文本。")
        root = json.loads(message)
        if not isinstance(root, dict):
            raise RuntimeError("WebSocket JSON-RPC 消息必须为对象。")
        if root.get("jsonrpc") != "2.0":
            raise RuntimeError("WebSocket JSON-RPC 消息版本非法。")

        method = root.get("method")
        if method is not None:
            if method != "hub.event":
                raise RuntimeError("WebSocket 收到未知通知。")
            if "id" in root:
                raise RuntimeError("hub.event 通知不允许包含 id。")
            if "result" in root or "error" in root:
                raise RuntimeError("hub.event 通知禁止包含 result 或 error。")
            event = parse_event(root.get("params"), path="hub.event.params")
            await self._events.put(event)
            return

        request_id = root.get("id")
        if isinstance(request_id, bool):
            raise RuntimeError("WebSocket JSON-RPC 响应缺少有效 id。")
        if isinstance(request_id, int | float):
            request_id = str(request_id)
        if not isinstance(request_id, str):
            raise RuntimeError("WebSocket JSON-RPC 响应缺少有效 id。")
        future = self._pending.get(request_id)
        if future is not None and not future.done():
            future.set_result(root)

    def _fail_pending(self, error: BaseException) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    def _complete_event_stream(self) -> None:
        if not self._stream_completed:
            self._stream_completed = True
            self._events.put_nowait(_SENTINEL)
