from __future__ import annotations

import asyncio
import json
from abc import ABC, abstractmethod
from typing import Any, AsyncIterator, Awaitable, Callable
from uuid import uuid4

import websockets

from ._jsonrpc import validate_response_envelope
from ._parsing import parse_event
from .models import DevHubClientOptions, DevHubEvent, RuntimeConnectionInfo


_SENTINEL = object()


class JsonRpcWsSession(ABC):
    """WebSocket JSON-RPC 会话抽象。"""

    @abstractmethod
    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        """发送一条 JSON-RPC 请求并返回结果载荷。"""

    @abstractmethod
    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        """读取事件流。"""

    @abstractmethod
    async def close(self) -> None:
        """关闭会话。"""


class WebSocketJsonRpcSession(JsonRpcWsSession):
    """基于 websockets 的默认 WebSocket JSON-RPC 会话。"""

    def __init__(
        self,
        connection_info: RuntimeConnectionInfo,
        options: DevHubClientOptions,
        *,
        connect: Callable[..., Awaitable[Any]] | None = None,
    ) -> None:
        self._connection_info = connection_info
        self._options = options.clone()
        self._connect = connect or websockets.connect
        self._websocket = None
        self._send_lock = asyncio.Lock()
        self._pending: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._events: asyncio.Queue[DevHubEvent | object] = asyncio.Queue()
        self._receiver_task: asyncio.Task[None] | None = None
        self._terminal_error: BaseException | None = None
        self._stream_completed = False
        self._closed = False

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self._ensure_open()
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

        async with self._send_lock:
            await self._websocket.send(json.dumps(payload_dict))

        try:
            if self._options.request_timeout is None:
                envelope = await future
            else:
                envelope = await asyncio.wait_for(future, timeout=self._options.request_timeout)
        finally:
            self._pending.pop(request_id, None)

        return validate_response_envelope(envelope, request_id)

    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        while True:
            item = await self._events.get()
            if item is _SENTINEL:
                if self._terminal_error is not None:
                    raise self._terminal_error
                return
            yield item

    async def close(self) -> None:
        if self._closed:
            return

        self._closed = True
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

    async def _ensure_connected(self) -> None:
        if self._websocket is not None:
            return

        self._websocket = await self._connect(
            self._connection_info.websocket_endpoint,
            open_timeout=self._options.request_timeout,
            close_timeout=self._options.request_timeout,
        )
        self._receiver_task = asyncio.create_task(self._run_receive_loop())

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

    def _ensure_open(self) -> None:
        if self._closed:
            raise RuntimeError("当前 WebSocket 会话已关闭。")

    def _fail_pending(self, error: BaseException) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    def _complete_event_stream(self) -> None:
        if not self._stream_completed:
            self._stream_completed = True
            self._events.put_nowait(_SENTINEL)
