from __future__ import annotations

from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, field
from typing import Any, AsyncIterator

from ._parsing import (
    parse_definition_result,
    parse_definitions_result,
    parse_instances_result,
    parse_ping_result,
    require_bool,
    require_mapping,
    require_str,
)
from .constants import DevHubEventType, ensure_supported_event_type
from ._validation import ensure_json_value, require_non_empty_string
from ._ws_session import JsonRpcWsSession, WebSocketJsonRpcSession
from .models import (
    AppDefinition,
    AppInstance,
    DevHubClientOptions,
    DevHubEvent,
    HubRuntime,
    ListInstancesRequest,
    PingResult,
    RuntimeConnectionInfo,
)
from .runtime import FileSystemRuntimeResolver, RuntimeResolver


_ECHO_UNSET = object()


def _create_default_ws_session(
    options: DevHubClientOptions,
    connection_info: RuntimeConnectionInfo,
) -> JsonRpcWsSession:
    return WebSocketJsonRpcSession(connection_info, options)


@dataclass(slots=True)
class DevHubEventsClientDependencies:
    """创建 WebSocket 事件客户端时可注入的依赖项。"""

    runtime_resolver: RuntimeResolver = field(default_factory=FileSystemRuntimeResolver)
    session_factory: Callable[[DevHubClientOptions, RuntimeConnectionInfo], JsonRpcWsSession] = (
        _create_default_ws_session
    )

    def __post_init__(self) -> None:
        if self.runtime_resolver is None:
            raise ValueError("runtime_resolver 不能为空。")
        if not callable(self.session_factory):
            raise TypeError("session_factory 必须为可调用对象。")


class DevHubEventsClient:
    """DevHub WebSocket 客户端。"""

    def __init__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
        session: JsonRpcWsSession,
    ) -> None:
        """初始化 WebSocket 客户端。"""

        self._options = options.clone()
        self._options.validate()
        self._connection_info = connection_info
        self._session = session
        self._authenticated = False
        self._event_stream_available = False
        self._closed = False

    @classmethod
    async def from_runtime(
        cls,
        options: DevHubClientOptions,
        dependencies: DevHubEventsClientDependencies | None = None,
    ) -> "DevHubEventsClient":
        """根据数据根目录创建 WebSocket 客户端。"""

        cloned_options = options.clone()
        cloned_options.validate()
        resolved_dependencies = dependencies or DevHubEventsClientDependencies()
        connection_info = resolved_dependencies.runtime_resolver.resolve(cloned_options)
        session = resolved_dependencies.session_factory(cloned_options, connection_info)
        return cls(cloned_options, connection_info, session)

    @property
    def runtime(self) -> HubRuntime:
        """返回发现到的运行时信息。"""

        return self._connection_info.runtime

    async def authenticate(self) -> None:
        """执行 `hub.ws.authenticate`。"""

        self._ensure_not_closed()
        self._refresh_session_state()
        if self._authenticated:
            raise RuntimeError("当前 WebSocket 客户端已完成认证。")

        self._reopen_session()
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
            self._authenticated = False
            self._event_stream_available = False
            try:
                await self._session.disconnect("authenticate_failed")
            except Exception:
                pass
            raise

        self._authenticated = True
        self._event_stream_available = True

    async def ping(self, echo: Any = _ECHO_UNSET) -> PingResult:
        """通过 WebSocket 调用 `hub.ping`。"""

        self._ensure_authenticated()
        params = None if echo is _ECHO_UNSET else {"echo": ensure_json_value(echo, "echo")}
        result = await self._send_request("hub.ping", params, require_authenticated=True)
        return parse_ping_result(result, path="hub.ping.result")

    async def list_definitions(self) -> list[AppDefinition]:
        """通过 WebSocket 调用 `hub.apps.listDefinitions`。"""

        self._ensure_authenticated()
        result = await self._send_request("hub.apps.listDefinitions", None, require_authenticated=True)
        return parse_definitions_result(result, path="hub.apps.listDefinitions.result")

    async def get_definition(self, app_id: str) -> AppDefinition:
        """通过 WebSocket 调用 `hub.apps.getDefinition`。"""

        self._ensure_authenticated()
        normalized_app_id = require_non_empty_string(app_id, "app_id")
        result = await self._send_request(
            "hub.apps.getDefinition",
            {"appId": normalized_app_id},
            require_authenticated=True,
        )
        return parse_definition_result(result, path="hub.apps.getDefinition.result")

    async def list_instances(self, request: ListInstancesRequest | None = None) -> list[AppInstance]:
        """通过 WebSocket 调用 `hub.apps.listInstances`。"""

        self._ensure_authenticated()
        params: dict[str, Any] | None = None
        if request is not None:
            params = {}
            if request.app_id is not None:
                params["appId"] = require_non_empty_string(request.app_id, "request.app_id")
            if request.scope is not None:
                if not isinstance(request.scope, str):
                    raise ValueError("request.scope 类型非法。")
                params["scope"] = request.scope
            if request.include_all_scopes:
                params["includeAllScopes"] = True
            if request.include_offline:
                params["includeOffline"] = True
            if not params:
                params = None

        result = await self._send_request("hub.apps.listInstances", params, require_authenticated=True)
        return parse_instances_result(result, path="hub.apps.listInstances.result")

    async def subscribe(self, types: Iterable[DevHubEventType] | None = None) -> str:
        """订阅事件。"""

        self._ensure_authenticated()
        params: dict[str, Any] | None = None
        if types is not None:
            if isinstance(types, str | bytes | bytearray):
                raise ValueError("types 必须为事件类型序列。")
            if isinstance(types, Mapping):
                raise ValueError("types 必须为事件类型序列。")
            types_list = list(types)
            normalized_types = [
                ensure_supported_event_type(item, f"types[{index}]")
                for index, item in enumerate(types_list)
            ]
            if types_list:
                params = {"types": normalized_types}
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
        """关闭 WebSocket 客户端。"""

        if self._closed:
            return

        self._closed = True
        self._authenticated = False
        self._event_stream_available = False
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
        self._refresh_session_state()
        if require_authenticated:
            self._ensure_authenticated()
        return await self._session.send_request(method, params)

    def _ensure_not_closed(self) -> None:
        if self._closed:
            raise RuntimeError("当前事件客户端已关闭。")

    def _ensure_authenticated(self) -> None:
        self._refresh_session_state()
        self._ensure_not_closed()
        if not self._authenticated:
            raise RuntimeError("当前 WebSocket 尚未通过鉴权。")

    def _ensure_event_stream_available(self) -> None:
        self._refresh_session_state()
        self._ensure_not_closed()
        if self._event_stream_available:
            return
        self._ensure_authenticated()

    def _refresh_session_state(self) -> None:
        is_terminated = getattr(self._session, "is_terminated", None)
        if callable(is_terminated) and is_terminated():
            self._authenticated = False

    def _reopen_session(self) -> None:
        reopen = getattr(self._session, "reopen", None)
        if callable(reopen):
            reopen()
