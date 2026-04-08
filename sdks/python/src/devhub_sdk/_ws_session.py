from __future__ import annotations

import asyncio
import json
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, AsyncIterator, Awaitable, Callable
from uuid import uuid4

import websockets

from ._json import load_json_text
from ._jsonrpc import validate_response_envelope
from ._parsing import parse_event
from .models import DevHubClientOptions, DevHubEvent, RuntimeConnectionInfo


_SENTINEL = object()


class JsonRpcWsSession(ABC):
    @abstractmethod
    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        """发送一条 JSON-RPC 请求并返回结果载荷。"""

    @abstractmethod
    async def disconnect(self, reason: str) -> None:
        """断开当前连接代次，但保留会话对象供后续重连使用。"""

    @abstractmethod
    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        """读取事件流。"""

    @abstractmethod
    async def close(self) -> None:
        """关闭会话。"""

    def reopen(self) -> None:
        """为重新认证准备新的连接代次。"""

    def is_terminated(self) -> bool:
        """返回当前连接是否已经终止。"""

        return False


@dataclass(slots=True)
class _EventStreamState:
    queue: asyncio.Queue[DevHubEvent | object] = field(default_factory=asyncio.Queue)
    terminal_error: BaseException | None = None
    completed: bool = False


class WebSocketJsonRpcSession(JsonRpcWsSession):
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
        self._stream = _EventStreamState()
        self._receiver_task: asyncio.Task[None] | None = None
        self._terminated = False
        self._closed = False

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self._ensure_open()
        self._ensure_stream_available()
        await self._ensure_connected()
        self._ensure_stream_available()

        request_id = f"ws-{uuid4().hex}"
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future

        payload_dict: dict[str, Any] = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
        }
        if params is not None:
            payload_dict["params"] = params

        try:
            async with self._send_lock:
                await self._websocket.send(json.dumps(payload_dict, allow_nan=False))
        except Exception as exc:
            await self._abort_connection(exc)
            raise RuntimeError("事件流已终止。") from exc

        try:
            if self._options.request_timeout is None:
                envelope = await future
            else:
                envelope = await asyncio.wait_for(future, timeout=self._options.request_timeout)
        finally:
            self._pending.pop(request_id, None)

        return validate_response_envelope(envelope, request_id)

    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        stream = self._stream
        while True:
            item = await stream.queue.get()
            if item is _SENTINEL:
                stream.queue.put_nowait(_SENTINEL)
                if stream.terminal_error is not None:
                    raise stream.terminal_error
                return
            yield item

    async def disconnect(self, reason: str) -> None:
        self._ensure_open()
        await self._disconnect_current_connection(reason)

    async def close(self) -> None:
        if self._closed:
            return

        self._closed = True
        await self._disconnect_current_connection("session_closed")

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
        current_task = asyncio.current_task()
        try:
            async for message in self._websocket:
                await self._handle_message(message)
        except asyncio.CancelledError:
            raise
        except BaseException as exc:
            terminal_error = exc
        finally:
            if self._pending:
                error = terminal_error or RuntimeError("WebSocket 连接已关闭。")
                self._fail_pending(error)
            self._websocket = None
            if self._receiver_task is current_task:
                self._receiver_task = None
            self._terminated = True
            self._complete_event_stream(self._stream, terminal_error)

    async def _handle_message(self, message: Any) -> None:
        if not isinstance(message, str):
            raise RuntimeError("WebSocket 消息必须为文本。")

        root = load_json_text(message, source="WebSocket 消息")
        if not isinstance(root, dict):
            raise RuntimeError("WebSocket JSON-RPC 消息必须为对象。")
        if root.get("jsonrpc") != "2.0":
            raise RuntimeError("WebSocket JSON-RPC 消息版本非法。")

        method = root.get("method")
        if method is not None:
            if method != "hub.event":
                raise RuntimeError("WebSocket JSON-RPC 消息只允许挂起请求响应或 hub.event 通知。")
            if "id" in root:
                raise RuntimeError("hub.event 通知不允许包含 id。")
            if "result" in root or "error" in root:
                raise RuntimeError("hub.event 通知禁止包含 result 或 error。")
            event = parse_event(root.get("params"), path="hub.event.params")
            await self._stream.queue.put(event)
            return

        request_id = root.get("id")
        if isinstance(request_id, bool):
            raise RuntimeError("WebSocket JSON-RPC 响应缺少有效 id。")
        if isinstance(request_id, int | float):
            request_id = str(request_id)
        if not isinstance(request_id, str):
            raise RuntimeError("WebSocket JSON-RPC 响应缺少有效 id。")

        future = self._pending.get(request_id)
        if future is None or future.done():
            raise RuntimeError("WebSocket JSON-RPC 响应 id 未匹配任何挂起请求。")
        future.set_result(root)

    def _ensure_open(self) -> None:
        if self._closed:
            raise RuntimeError("当前 WebSocket 会话已关闭。")

    def _ensure_stream_available(self) -> None:
        if not self._terminated:
            return
        if self._stream.terminal_error is not None:
            raise RuntimeError("事件流已终止。") from self._stream.terminal_error
        raise RuntimeError("事件流已终止。")

    def _fail_pending(self, error: BaseException) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    def reopen(self) -> None:
        self._ensure_open()
        if self._receiver_task is not None and not self._receiver_task.done():
            raise RuntimeError("当前 WebSocket 连接仍处于活动状态。")
        if not self._terminated:
            return
        self._stream = _EventStreamState()
        self._terminated = False

    def is_terminated(self) -> bool:
        return self._terminated

    async def _abort_connection(self, error: BaseException) -> None:
        self._terminated = True
        self._fail_pending(error)
        await self._shutdown_connection()
        self._complete_event_stream(self._stream, error)

    async def _disconnect_current_connection(self, reason: str) -> None:
        self._terminated = True
        self._fail_pending(RuntimeError("WebSocket 连接已关闭。"))
        await self._shutdown_connection(reason)
        self._complete_event_stream(self._stream)

    async def _shutdown_connection(self, reason: str | None = None) -> None:
        websocket = self._websocket
        self._websocket = None
        if websocket is not None:
            try:
                if reason:
                    await websocket.close(reason=reason)
                else:
                    await websocket.close()
            except Exception:
                pass
        receiver_task = self._receiver_task
        if receiver_task is not None:
            if not receiver_task.done():
                receiver_task.cancel()
            try:
                await receiver_task
            except asyncio.CancelledError:
                pass
            except Exception:
                pass
            self._receiver_task = None

    @staticmethod
    def _complete_event_stream(stream: _EventStreamState, error: BaseException | None = None) -> None:
        if stream.completed:
            return
        if error is not None and stream.terminal_error is None:
            stream.terminal_error = error
        stream.completed = True
        stream.queue.put_nowait(_SENTINEL)
