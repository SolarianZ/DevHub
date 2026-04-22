from __future__ import annotations

from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
from typing import Any, AsyncIterator

from ._parsing import (
    parse_definition_result,
    parse_definitions_result,
    parse_event,
    parse_instances_result,
    parse_ping_result,
    require_bool,
    require_mapping,
    require_non_empty_string,
)
from ._payloads import (
    build_get_definition_params,
    build_list_definitions_params,
    build_list_instances_params,
    build_ping_params,
    build_subscribe_params,
    build_unsubscribe_params,
    build_ws_authenticate_params,
)
from .constants import DevHubEventType
from ._ws_session import JsonRpcWsSession, WebSocketJsonRpcSession
from .models import (
    AppDefinition,
    AppInstance,
    DevHubClientOptions,
    DevHubEvent,
    HubRuntime,
    ListDefinitionsRequest,
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
                build_ws_authenticate_params(self._options, self._connection_info),
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
        params = build_ping_params() if echo is _ECHO_UNSET else build_ping_params(echo)
        result = await self._send_request("hub.ping", params, require_authenticated=True)
        return parse_ping_result(result, path="hub.ping.result")

    async def list_definitions(self, request: ListDefinitionsRequest) -> list[AppDefinition]:
        """通过 WebSocket 调用 `hub.apps.listDefinitions`。"""

        self._ensure_authenticated()
        result = await self._send_request(
            "hub.apps.listDefinitions",
            build_list_definitions_params(request),
            require_authenticated=True,
        )
        return parse_definitions_result(result, path="hub.apps.listDefinitions.result")

    async def get_definition(self, app_id: str, scope: str) -> AppDefinition:
        """通过 WebSocket 调用 `hub.apps.getDefinition`，按 `appId + scope` 精确读取 Definition。"""

        self._ensure_authenticated()
        result = await self._send_request(
            "hub.apps.getDefinition",
            build_get_definition_params(app_id, scope),
            require_authenticated=True,
        )
        return parse_definition_result(result, path="hub.apps.getDefinition.result")

    async def list_instances(self, request: ListInstancesRequest) -> list[AppInstance]:
        """通过 WebSocket 调用 `hub.apps.listInstances`。"""

        self._ensure_authenticated()
        result = await self._send_request(
            "hub.apps.listInstances",
            build_list_instances_params(request),
            require_authenticated=True,
        )
        return parse_instances_result(result, path="hub.apps.listInstances.result")

    async def subscribe(self, types: Iterable[DevHubEventType] | None = None) -> str:
        """订阅事件。"""

        self._ensure_authenticated()
        result = await self._send_request(
            "hub.events.subscribe",
            build_subscribe_params(types),
            require_authenticated=True,
        )
        root = require_mapping(result, "hub.events.subscribe.result")
        if not require_bool(root, "ok", "hub.events.subscribe.result"):
            raise RuntimeError("hub.events.subscribe 返回结果非法。")
        return require_non_empty_string(root, "subscriptionId", "hub.events.subscribe.result")

    async def unsubscribe(self, subscription_id: str) -> None:
        """取消订阅。"""

        self._ensure_authenticated()
        result = await self._send_request(
            "hub.events.unsubscribe",
            build_unsubscribe_params(subscription_id),
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
            async for raw_params in iterator:
                yield parse_event(raw_params, path="hub.event.params")
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
