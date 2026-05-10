from __future__ import annotations

import asyncio
from dataclasses import FrozenInstanceError, dataclass, field
from datetime import datetime, timezone
from typing import Any, AsyncIterator

import pytest

from devhub_sdk import (
    AbandonedRequestFilter,
    DevHubClientOptions,
    DevHubEventsClient,
    DevHubEventsClientDependencies,
    DevHubRpcException,
    HubRuntime,
    HubRuntimeTuning,
    INVOCATION_COMPLETED,
    ListDefinitionsRequest,
    ListInstancesRequest,
    RuntimeConnectionInfo,
)


@dataclass(slots=True)
class FakeRuntimeResolver:
    """用于验证依赖注入的运行时解析器。"""

    connection_info: RuntimeConnectionInfo
    calls: list[DevHubClientOptions] = field(default_factory=list)

    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        self.calls.append(options.clone())
        return self.connection_info


@dataclass(slots=True)
class FakeWsSession:
    """用于验证依赖注入的 WebSocket 会话。"""

    responses: dict[str, dict[str, Any] | BaseException]
    events: list[dict[str, Any] | BaseException]
    requests: list[dict[str, Any]] = field(default_factory=list)
    disconnect_reasons: list[str] = field(default_factory=list)
    abandoned_request_count_result: int = 0
    clear_abandoned_requests_result: int = 0
    abandoned_request_count_filters: list[AbandonedRequestFilter | None] = field(default_factory=list)
    clear_abandoned_request_filters: list[AbandonedRequestFilter | None] = field(default_factory=list)
    terminated: bool = False
    reopen_calls: int = 0
    closed: bool = False

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.requests.append({"method": method, "params": params})
        response = self.responses[method]
        if isinstance(response, BaseException):
            raise response
        return response

    async def disconnect(self, reason: str) -> None:
        self.disconnect_reasons.append(reason)

    async def read_events(self) -> AsyncIterator[dict[str, Any]]:
        for event in self.events:
            if isinstance(event, BaseException):
                self.terminated = True
                raise event
            yield event
        if self.terminated:
            return

    async def close(self) -> None:
        self.closed = True

    def reopen(self) -> None:
        self.reopen_calls += 1
        self.terminated = False

    def is_terminated(self) -> bool:
        return self.terminated

    def get_abandoned_request_count(self, filter: AbandonedRequestFilter | None = None) -> int:
        self.abandoned_request_count_filters.append(filter)
        return self.abandoned_request_count_result

    def clear_abandoned_requests(self, filter: AbandonedRequestFilter | None = None) -> int:
        self.clear_abandoned_request_filters.append(filter)
        return self.clear_abandoned_requests_result


@dataclass(slots=True)
class FakeWsSessionFactory:
    """用于验证会话工厂接线的 WebSocket 工厂。"""

    session: FakeWsSession
    calls: list[dict[str, Any]] = field(default_factory=list)

    def __call__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
    ) -> FakeWsSession:
        self.calls.append(
            {
                "connection_info": connection_info,
                "options": options.clone(),
            }
        )
        return self.session


@pytest.mark.asyncio
async def test_events_client_with_injected_resolver_and_session_should_use_abstractions() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.events.subscribe": {"ok": True, "subscriptionId": "sub-fake"},
        },
        events=[
            {
                "subscriptionId": "sub-fake",
                "type": INVOCATION_COMPLETED,
                "timeUtc": "2026-03-09T00:00:00Z",
                "payload": {"invocationId": "invk-fake"},
            }
        ],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        subscription_id = await client.subscribe([INVOCATION_COMPLETED])
        event = await anext(client.read_events())
    finally:
        await client.close()

    assert len(resolver.calls) == 1
    assert len(session_factory.calls) == 1
    assert subscription_id == "sub-fake"
    assert event.type == INVOCATION_COMPLETED
    assert event.payload["invocationId"] == "invk-fake"
    assert session.requests[0]["method"] == "hub.ws.authenticate"
    assert session.requests[0]["params"]["token"] == "token-fake"
    assert session.requests[1]["params"] == {"types": [INVOCATION_COMPLETED]}
    assert session.closed is True
    assert session_factory.calls[0]["options"].client_id == "ws-client"


@pytest.mark.asyncio
async def test_events_client_with_injected_resolver_should_reject_invalid_websocket_endpoint() -> None:
    connection_info = _create_connection_info(ws_url=" ws://127.0.0.1:57231/ws")
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={}, events=[])
    session_factory = FakeWsSessionFactory(session)

    with pytest.raises(RuntimeError, match="wsUrl"):
        await DevHubEventsClient.from_runtime(
            DevHubClientOptions(client_id="ws-client"),
            DevHubEventsClientDependencies(
                runtime_resolver=resolver,
                session_factory=session_factory,
            ),
        )

    assert len(session_factory.calls) == 0


@pytest.mark.asyncio
async def test_events_client_with_injected_session_should_support_ws_readable_methods() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.ping": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z", "echo": {"value": 1}},
            "hub.apps.listDefinitions": {
                "ok": True,
                "definitions": [{"appId": "ws.app", "scope": "", "displayName": "WS App"}],
            },
            "hub.apps.getDefinition": {
                "ok": True,
                "definition": {"appId": "ws.app", "scope": "", "displayName": "WS App"},
            },
            "hub.apps.getInstance": {
                "ok": True,
                "instance": {
                    "instanceId": "inst-1",
                    "appId": "ws.app",
                    "scope": "",
                    "pid": 12345,
                    "registeredAtUtc": "2026-03-09T00:00:00Z",
                    "lastSeenUtc": "2026-03-09T00:00:01Z",
                    "invoke": {"poll": True, "respond": True},
                },
            },
            "hub.apps.listInstances": {
                "ok": True,
                "instances": [
                    {
                        "instanceId": "inst-1",
                        "appId": "ws.app",
                        "scope": "",
                        "pid": 12345,
                        "registeredAtUtc": "2026-03-09T00:00:00Z",
                        "lastSeenUtc": "2026-03-09T00:00:01Z",
                        "invoke": {"poll": True, "respond": True},
                    }
                ],
            },
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        ping = await client.ping({"value": 1})
        definitions = await client.list_definitions(ListDefinitionsRequest(scope=None))
        definition = await client.get_definition("ws.app", "")
        instance = await client.get_instance("inst-1")
        instances = await client.list_instances(ListInstancesRequest(scope=None))
    finally:
        await client.close()

    assert ping.ok is True
    assert definitions[0].app_id == "ws.app"
    assert definitions[0].scope == ""
    assert definition.app_id == "ws.app"
    assert definition.scope == ""
    assert instance.instance_id == "inst-1"
    assert instance.instance_session_token is None
    assert instances[0].instance_id == "inst-1"
    assert instances[0].scope == ""
    assert [request["method"] for request in session.requests] == [
        "hub.ws.authenticate",
        "hub.ping",
        "hub.apps.listDefinitions",
        "hub.apps.getDefinition",
        "hub.apps.getInstance",
        "hub.apps.listInstances",
    ]
    assert session.requests[2]["params"] == {"scope": None}
    assert session.requests[3]["params"] == {"appId": "ws.app", "scope": ""}
    assert session.requests[4]["params"] == {"instanceId": "inst-1"}
    assert session.requests[5]["params"] == {"scope": None}


@pytest.mark.asyncio
async def test_events_client_should_delegate_local_abandoned_request_maintenance_to_session() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={},
        events=[],
        abandoned_request_count_result=3,
        clear_abandoned_requests_result=2,
    )
    session_factory = FakeWsSessionFactory(session)
    request_filter = AbandonedRequestFilter(older_than_seconds=5, app_id="ws.app", method="hub.apps.getDefinition")

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        assert client.get_abandoned_request_count(request_filter) == 3
        assert client.clear_abandoned_requests(request_filter) == 2
    finally:
        await client.close()

    assert session.requests == []
    assert session.abandoned_request_count_filters == [request_filter]
    assert session.clear_abandoned_request_filters == [request_filter]


@pytest.mark.asyncio
async def test_events_client_get_definition_should_reuse_shared_payload_builder_validation() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(ValueError, match="appId 格式要求"):
            await client.get_definition(".Test.App", "")
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == ["hub.ws.authenticate"]


@pytest.mark.asyncio
async def test_events_client_get_instance_should_reuse_shared_payload_builder_validation() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(ValueError, match="instance_id"):
            await client.get_instance("inst-1.")
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == ["hub.ws.authenticate"]


@pytest.mark.asyncio
async def test_events_client_list_instances_should_reuse_shared_payload_builder_validation() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(ValueError, match="appId 格式要求"):
            await client.list_instances(ListInstancesRequest(scope=None, app_id="Test.App-"))
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == ["hub.ws.authenticate"]


@pytest.mark.asyncio
async def test_events_client_get_instance_should_propagate_remote_instance_not_found() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.apps.getInstance": DevHubRpcException(
                code=-32010,
                message="instance_not_found",
                data={"reason": "unknown_instance", "instanceId": "missing-inst"},
                request_id="ws-get-instance-fake",
            ),
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(DevHubRpcException) as exc_info:
            await client.get_instance("missing-inst")
    finally:
        await client.close()

    assert exc_info.value.code == -32010
    assert exc_info.value.message == "instance_not_found"
    assert exc_info.value.reason == "unknown_instance"
    assert exc_info.value.try_get_data_string("instanceId") == "missing-inst"
    assert [request["method"] for request in session.requests] == [
        "hub.ws.authenticate",
        "hub.apps.getInstance",
    ]


@pytest.mark.asyncio
async def test_events_client_get_instance_when_result_contains_sensitive_fields_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.apps.getInstance": {
                "ok": True,
                "password": "secret-1",
                "instance": {
                    "instanceId": "inst-1",
                    "appId": "ws.app",
                    "scope": "",
                    "pid": 12345,
                    "registeredAtUtc": "2026-03-09T00:00:00Z",
                    "lastSeenUtc": "2026-03-09T00:00:01Z",
                    "invoke": {"poll": True, "respond": True},
                },
            },
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(RuntimeError, match="password"):
            await client.get_instance("inst-1")
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == [
        "hub.ws.authenticate",
        "hub.apps.getInstance",
    ]


@pytest.mark.asyncio
async def test_events_client_subscribe_without_types_should_request_all_events() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.events.subscribe": {"ok": True, "subscriptionId": "sub-all"},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        subscription_id = await client.subscribe()
    finally:
        await client.close()

    assert subscription_id == "sub-all"
    assert session.requests[1]["params"] is None


@pytest.mark.asyncio
async def test_events_client_subscribe_with_empty_types_should_request_all_events() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.events.subscribe": {"ok": True, "subscriptionId": "sub-empty"},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        subscription_id = await client.subscribe([])
    finally:
        await client.close()

    assert subscription_id == "sub-empty"
    assert session.requests[1]["params"] is None


@pytest.mark.asyncio
async def test_events_client_unsubscribe_should_send_subscription_id() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.events.unsubscribe": {"ok": True},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        await client.unsubscribe("sub-1")
    finally:
        await client.close()

    assert session.requests[1] == {
        "method": "hub.events.unsubscribe",
        "params": {"subscriptionId": "sub-1"},
    }


@pytest.mark.asyncio
async def test_events_client_before_authenticate_should_reject_read_events() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={}, events=[])
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    try:
        with pytest.raises(RuntimeError, match="WebSocket 尚未通过鉴权"):
            await anext(client.read_events())
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_events_client_runtime_view_should_be_immutable_and_keep_original_endpoint() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={"hub.ws.authenticate": {"ok": True, "protocolVersion": 1}}, events=[])
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    try:
        with pytest.raises(FrozenInstanceError):
            client.runtime.ws_url = "ws://127.0.0.1:1/ws"  # type: ignore[misc]

        await client.authenticate()
    finally:
        await client.close()

    assert session.requests[0]["method"] == "hub.ws.authenticate"


@pytest.mark.asyncio
async def test_events_client_when_event_stream_fails_should_require_reauthenticate() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.events.subscribe": {"ok": True, "subscriptionId": "sub-1"},
        },
        events=[RuntimeError("hub.event invalid")],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    try:
        await client.authenticate()
        await client.subscribe([INVOCATION_COMPLETED])
        with pytest.raises(RuntimeError, match="hub.event invalid"):
            await anext(client.read_events())

        with pytest.raises(RuntimeError, match="尚未通过鉴权"):
            await client.subscribe([INVOCATION_COMPLETED])
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_events_client_when_authenticate_fails_should_allow_retry_on_same_client() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": DevHubRpcException(
                code=-32001,
                message="unauthorized",
                data={"reason": "invalid_token"},
                request_id="ws-auth-fake",
            )
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )

    try:
        with pytest.raises(DevHubRpcException):
            await client.authenticate()

        assert session.disconnect_reasons == ["authenticate_failed"]
        assert session.closed is False

        session.responses["hub.ws.authenticate"] = {"ok": True, "protocolVersion": 1}
        await client.authenticate()
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == [
        "hub.ws.authenticate",
        "hub.ws.authenticate",
    ]


@pytest.mark.asyncio
async def test_events_client_when_authenticate_called_twice_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={"hub.ws.authenticate": {"ok": True, "protocolVersion": 1}}, events=[])
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    try:
        await client.authenticate()
        with pytest.raises(RuntimeError, match="已完成认证"):
            await client.authenticate()
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_events_client_subscribe_when_types_is_single_string_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={"hub.ws.authenticate": {"ok": True, "protocolVersion": 1}}, events=[])
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    try:
        await client.authenticate()
        with pytest.raises(ValueError, match="事件类型序列"):
            await client.subscribe(INVOCATION_COMPLETED)
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_events_client_subscribe_when_types_contains_unknown_event_should_raise_before_request() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
        },
        events=[],
    )
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=resolver,
            session_factory=session_factory,
        ),
    )
    try:
        await client.authenticate()
        with pytest.raises(ValueError, match="受支持的 DevHub 事件类型"):
            await client.subscribe([INVOCATION_COMPLETED, "future.event"])
    finally:
        await client.close()

    assert [request["method"] for request in session.requests] == ["hub.ws.authenticate"]


@pytest.mark.asyncio
async def test_events_client_after_close_should_reject_subscribe_and_read() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    session = FakeWsSession(responses={"hub.ws.authenticate": {"ok": True, "protocolVersion": 1}}, events=[])
    session_factory = FakeWsSessionFactory(session)

    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="ws-client"),
        DevHubEventsClientDependencies(runtime_resolver=resolver, session_factory=session_factory),
    )
    await client.authenticate()
    await client.close()

    with pytest.raises(RuntimeError, match="事件客户端已关闭"):
        await client.subscribe([INVOCATION_COMPLETED])

    with pytest.raises(RuntimeError, match="事件客户端已关闭"):
        await anext(client.read_events())


def _create_connection_info(ws_url: str = "ws://127.0.0.1:57231/ws") -> RuntimeConnectionInfo:
    return RuntimeConnectionInfo(
        runtime_directory="D:/runtime",
        token="token-fake",
        runtime=HubRuntime(
            protocol_version=1,
            pid=12345,
            http_base_url="http://127.0.0.1:57231",
            ws_url=ws_url,
            token_file="D:/runtime/token.txt",
            started_at_utc=datetime(2026, 3, 9, tzinfo=timezone.utc),
            runtime_tuning=HubRuntimeTuning(
                lease_seconds=30,
                online_threshold_seconds=30,
                launch_dedupe_window_seconds=30,
                launch_register_timeout_seconds=30,
            ),
        ),
    )
