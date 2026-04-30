from __future__ import annotations

import asyncio
import json
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, AsyncIterator, Awaitable, Callable
from uuid import uuid4

import websockets

from ._json import load_json_text
from ._jsonrpc import normalize_jsonrpc_id, validate_response_envelope
from .models import AbandonedRequestFilter, DevHubClientOptions, RuntimeConnectionInfo


_SENTINEL = object()


class JsonRpcWsSession(ABC):
    @abstractmethod
    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        """发送一条 JSON-RPC 请求并返回结果载荷。"""

    @abstractmethod
    async def disconnect(self, reason: str) -> None:
        """断开当前连接代次，但保留会话对象供后续重连使用。"""

    @abstractmethod
    async def read_events(self) -> AsyncIterator[dict[str, Any]]:
        """读取原始 `hub.event.params` 事件参数。"""

    @abstractmethod
    async def close(self) -> None:
        """关闭会话。"""

    @abstractmethod
    def get_abandoned_request_count(self, filter: AbandonedRequestFilter | None = None) -> int:
        """返回当前会话内匹配条件的已放弃请求数量。

        该操作仅维护本地状态，不会发送网络请求。
        """

    @abstractmethod
    def clear_abandoned_requests(self, filter: AbandonedRequestFilter | None = None) -> int:
        """清理当前会话内匹配条件的已放弃请求记录。

        该操作仅维护本地状态，不会发送网络请求。
        """

    def reopen(self) -> None:
        """为重新认证准备新的连接代次。"""

    def is_terminated(self) -> bool:
        """返回当前连接是否已经终止。"""

        return False


@dataclass(slots=True)
class _EventStreamState:
    queue: asyncio.Queue[dict[str, Any] | object] = field(default_factory=asyncio.Queue)
    terminal_error: BaseException | None = None
    completed: bool = False


@dataclass(slots=True)
class _AbandonedRequestEntry:
    request_id: str
    method: str
    abandoned_at: float
    app_id: str | None


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
        self._connect_lock = asyncio.Lock()
        self._send_lock = asyncio.Lock()
        self._pending: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._abandoned_request_ids: dict[str, _AbandonedRequestEntry] = {}
        self._abandoned_request_ttl_seconds = max(float(self._options.request_timeout or 0.0), 30.0)
        self._stream = _EventStreamState()
        self._event_reader_lock = asyncio.Lock()
        self._event_reader_active = False
        self._receiver_task: asyncio.Task[None] | None = None
        self._terminated = False
        self._closed = False

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self._ensure_open()
        self._ensure_stream_available()
        self._prune_abandoned_request_ids()
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
            self._pending.pop(request_id, None)
            await self._abort_connection(exc)
            raise RuntimeError("事件流已终止。") from exc

        try:
            if self._options.request_timeout is None:
                envelope = await future
            else:
                envelope = await asyncio.wait_for(
                    asyncio.shield(future),
                    timeout=self._options.request_timeout,
                )
        except asyncio.TimeoutError:
            self._pending.pop(request_id, None)
            self._mark_request_abandoned(request_id, method, params)
            future.cancel()
            raise
        except BaseException:
            self._pending.pop(request_id, None)
            raise
        else:
            self._pending.pop(request_id, None)
            self._abandoned_request_ids.pop(request_id, None)

        return validate_response_envelope(envelope, request_id)

    async def read_events(self) -> AsyncIterator[dict[str, Any]]:
        await self._acquire_event_reader()
        stream = self._stream
        try:
            while True:
                item = await stream.queue.get()
                if item is _SENTINEL:
                    stream.queue.put_nowait(_SENTINEL)
                    if stream.terminal_error is not None:
                        raise stream.terminal_error
                    return
                yield item
        finally:
            await self._release_event_reader()

    async def disconnect(self, reason: str) -> None:
        self._ensure_open()
        await self._disconnect_current_connection(reason)

    async def close(self) -> None:
        if self._closed:
            return

        self._closed = True
        await self._disconnect_current_connection("session_closed")

    def get_abandoned_request_count(self, filter: AbandonedRequestFilter | None = None) -> int:
        self._ensure_open()
        normalized_filter = self._normalize_abandoned_request_filter(filter)
        self._prune_abandoned_request_ids()
        now = time.monotonic()
        return sum(
            1
            for entry in self._abandoned_request_ids.values()
            if self._matches_abandoned_request(entry, normalized_filter, now)
        )

    def clear_abandoned_requests(self, filter: AbandonedRequestFilter | None = None) -> int:
        self._ensure_open()
        normalized_filter = self._normalize_abandoned_request_filter(filter)
        self._prune_abandoned_request_ids()
        now = time.monotonic()
        removed = 0

        for request_id, entry in list(self._abandoned_request_ids.items()):
            if not self._matches_abandoned_request(entry, normalized_filter, now):
                continue
            if self._abandoned_request_ids.pop(request_id, None) is not None:
                removed += 1

        return removed

    async def _ensure_connected(self) -> None:
        if self._websocket is not None:
            return

        async with self._connect_lock:
            if self._websocket is not None:
                return

            self._websocket = await self._connect(
                self._connection_info.websocket_endpoint,
                open_timeout=self._options.request_timeout,
                close_timeout=self._options.request_timeout,
            )
            self._clear_abandoned_request_ids()
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
            self._clear_abandoned_request_ids()
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
            params = root.get("params")
            if not isinstance(params, dict):
                raise RuntimeError("hub.event.params 必须为对象。")
            await self._stream.queue.put(params)
            return

        if "id" not in root:
            raise RuntimeError("WebSocket JSON-RPC 响应缺少 id 字段。")
        request_id = normalize_jsonrpc_id(root["id"], context="WebSocket JSON-RPC 响应的 id")

        self._prune_abandoned_request_ids()
        future = self._pending.get(request_id)
        if future is None:
            if self._is_tracked_abandoned_request(request_id):
                return
            raise RuntimeError("WebSocket JSON-RPC 响应 id 未匹配任何挂起请求。")
        if future.done():
            self._pending.pop(request_id, None)
            if self._is_tracked_abandoned_request(request_id):
                return
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
        self._clear_abandoned_request_ids()
        self._stream = _EventStreamState()
        self._terminated = False

    def is_terminated(self) -> bool:
        return self._terminated

    async def _abort_connection(self, error: BaseException) -> None:
        self._terminated = True
        self._fail_pending(error)
        self._clear_abandoned_request_ids()
        await self._shutdown_connection()
        self._complete_event_stream(self._stream, error)

    async def _disconnect_current_connection(self, reason: str) -> None:
        self._terminated = True
        self._fail_pending(RuntimeError("WebSocket 连接已关闭。"))
        self._clear_abandoned_request_ids()
        await self._shutdown_connection(reason)
        self._complete_event_stream(self._stream)

    async def _acquire_event_reader(self) -> None:
        async with self._event_reader_lock:
            if self._event_reader_active:
                raise RuntimeError("当前事件流已存在活动读取器。")
            self._event_reader_active = True

    async def _release_event_reader(self) -> None:
        async with self._event_reader_lock:
            self._event_reader_active = False

    def _mark_request_abandoned(self, request_id: str, method: str, params: dict[str, Any] | None) -> None:
        self._abandoned_request_ids[request_id] = _AbandonedRequestEntry(
            request_id=request_id,
            method=method,
            abandoned_at=time.monotonic(),
            app_id=self._try_extract_app_id(params),
        )

    def _is_tracked_abandoned_request(self, request_id: str) -> bool:
        return request_id in self._abandoned_request_ids

    def _normalize_abandoned_request_filter(
        self,
        filter: AbandonedRequestFilter | None,
    ) -> AbandonedRequestFilter | None:
        if filter is None:
            return None
        if not isinstance(filter, AbandonedRequestFilter):
            raise TypeError("filter 必须为 AbandonedRequestFilter 或 None。")
        return AbandonedRequestFilter(
            older_than_seconds=filter.older_than_seconds,
            app_id=filter.app_id,
            method=filter.method,
        )

    def _matches_abandoned_request(
        self,
        entry: _AbandonedRequestEntry,
        filter: AbandonedRequestFilter | None,
        now: float,
    ) -> bool:
        if filter is None:
            return True

        older_than_seconds = filter.older_than_seconds
        if older_than_seconds is not None and now - entry.abandoned_at < float(older_than_seconds):
            return False

        if filter.app_id is not None and entry.app_id != filter.app_id:
            return False

        if filter.method is not None and entry.method != filter.method:
            return False

        return True

    def _prune_abandoned_request_ids(self) -> None:
        if not self._abandoned_request_ids:
            return

        now = time.monotonic()
        expired_request_ids = [
            request_id
            for request_id, entry in self._abandoned_request_ids.items()
            if entry.abandoned_at + self._abandoned_request_ttl_seconds <= now
        ]
        for request_id in expired_request_ids:
            self._abandoned_request_ids.pop(request_id, None)

    def _clear_abandoned_request_ids(self) -> None:
        self._abandoned_request_ids.clear()

    @staticmethod
    def _try_extract_app_id(params: dict[str, Any] | None) -> str | None:
        if not isinstance(params, dict):
            return None

        app_id = params.get("appId")
        if not isinstance(app_id, str) or not app_id.strip():
            return None
        return app_id

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
