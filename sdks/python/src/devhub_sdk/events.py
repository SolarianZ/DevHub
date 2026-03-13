from __future__ import annotations

from collections.abc import Iterable, Mapping
from typing import Any, AsyncIterator

from .constants import ALL_EVENT_TYPES
from ._parsing import require_bool, require_mapping, require_str
from ._validation import require_non_empty_string
from ._ws_session import JsonRpcWsSession, WebSocketJsonRpcSession
from .models import DevHubClientOptions, DevHubEvent, HubRuntime
from .runtime import FileSystemRuntimeResolver, RuntimeResolver


_DEFAULT_RUNTIME_RESOLVER = FileSystemRuntimeResolver()


class DevHubEventsClient:
    """DevHub WebSocket 事件客户端。"""

    def __init__(
        self,
        options: DevHubClientOptions,
        *,
        runtime_resolver: RuntimeResolver | None = None,
        session: JsonRpcWsSession | None = None,
    ) -> None:
        """初始化事件客户端。"""

        self._options = options.clone()
        self._options.validate()
        self._runtime_resolver = runtime_resolver or _DEFAULT_RUNTIME_RESOLVER
        self._connection_info = self._runtime_resolver.resolve(self._options)
        self._session = session or WebSocketJsonRpcSession(self._connection_info, self._options)
        self._authenticated = False
        self._closed = False

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

        self._ensure_not_closed()
        if self._authenticated:
            raise RuntimeError("当前事件客户端已完成认证。")

        try:
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
        except Exception:
            await self.close()
            raise

        self._authenticated = True

    async def subscribe(self, types: Iterable[str] | None = None) -> str:
        """订阅事件。"""

        self._ensure_authenticated()
        params: dict[str, Any] = {}
        if types is not None:
            if isinstance(types, str | bytes | bytearray):
                raise ValueError("types 必须为事件类型字符串序列。")
            if isinstance(types, Mapping):
                raise ValueError("types 必须为事件类型字符串序列。")
            types_list = list(types)
            if any(not isinstance(item, str) or not item.strip() for item in types_list):
                raise ValueError("types 只能包含非空字符串。")
            if any(item not in ALL_EVENT_TYPES for item in types_list):
                raise ValueError("types 只能包含规范定义的事件类型。")
            if types_list:
                params["types"] = types_list
        result = await self._send_request("hub.events.subscribe", params, require_authenticated=True)
        root = require_mapping(result, "hub.events.subscribe.result")
        if not require_bool(root, "ok", "hub.events.subscribe.result"):
            raise RuntimeError("hub.events.subscribe 返回结果非法。")
        return require_str(root, "subscriptionId", "hub.events.subscribe.result")

    async def unsubscribe(self, subscription_id: str) -> None:
        """取消订阅。"""

        self._ensure_authenticated()
        normalized_subscription_id = require_non_empty_string(subscription_id, "subscription_id")
        result = await self._send_request(
            "hub.events.unsubscribe",
            {"subscriptionId": normalized_subscription_id},
            require_authenticated=True,
        )
        root = require_mapping(result, "hub.events.unsubscribe.result")
        if not require_bool(root, "ok", "hub.events.unsubscribe.result"):
            raise RuntimeError("hub.events.unsubscribe 返回结果非法。")

    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        """读取事件流。"""

        self._ensure_event_stream_available()
        iterator = self._session.read_events()
        try:
            async for event in iterator:
                yield event
        finally:
            aclose = getattr(iterator, "aclose", None)
            if aclose is not None:
                await aclose()

    async def close(self) -> None:
        """关闭事件客户端。"""

        if self._closed:
            return

        self._closed = True
        self._authenticated = False
        await self._session.close()

    async def __aenter__(self) -> "DevHubEventsClient":
        """异步上下文入口。"""

        return self

    async def __aexit__(self, exc_type, exc, tb) -> None:
        """异步上下文出口。"""

        await self.close()

    async def _send_request(
        self,
        method: str,
        params: dict[str, Any] | None,
        *,
        require_authenticated: bool,
    ) -> dict[str, Any]:
        self._ensure_not_closed()
        if require_authenticated:
            self._ensure_authenticated()
        return await self._session.send_request(method, params)

    def _ensure_not_closed(self) -> None:
        if self._closed:
            raise RuntimeError("当前事件客户端已关闭。")

    def _ensure_authenticated(self) -> None:
        self._ensure_not_closed()
        if not self._authenticated:
            raise RuntimeError("当前 WebSocket 尚未通过鉴权。")

    def _ensure_event_stream_available(self) -> None:
        self._ensure_authenticated()
