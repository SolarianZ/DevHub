from __future__ import annotations

import asyncio
import json
from dataclasses import FrozenInstanceError, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, AsyncIterator

import pytest
import websockets

from devhub_sdk import (
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
    events: list[dict[str, Any]]
    requests: list[dict[str, Any]] = field(default_factory=list)
    disconnect_reasons: list[str] = field(default_factory=list)
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
            yield event

    async def close(self) -> None:
        self.closed = True


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
            await client.get_definition("Test.App", "")
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
            await client.get_instance("inst/1")
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
            await client.list_instances(ListInstancesRequest(scope=None, app_id="Test.App"))
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
async def test_events_client_before_authenticate_should_reject_read_events(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            with pytest.raises(RuntimeError, match="WebSocket 尚未通过鉴权"):
                await anext(client.read_events())
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_authenticate_subscribe_and_read_event(tmp_path: Path) -> None:
    received_messages: list[dict[str, Any]] = []

    async def handler(websocket) -> None:
        async for raw in websocket:
            message = json.loads(raw)
            received_messages.append(message)
            if message["method"] == "hub.ws.authenticate":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "protocolVersion": 1},
                        }
                    )
                )
            elif message["method"] == "hub.events.subscribe":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "subscriptionId": "sub-1"},
                        }
                    )
                )
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "method": "hub.event",
                            "params": {
                                "subscriptionId": "sub-1",
                                "type": INVOCATION_COMPLETED,
                                "timeUtc": "2026-03-09T00:00:00Z",
                                "payload": {"invocationId": "invk-1"},
                            },
                        }
                    )
                )
                break

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            await client.authenticate()
            subscription_id = await client.subscribe([INVOCATION_COMPLETED])
            event = await asyncio.wait_for(anext(client.read_events()), timeout=2)
        finally:
            await client.close()

    assert subscription_id == "sub-1"
    assert event.type == INVOCATION_COMPLETED
    assert event.payload["invocationId"] == "invk-1"
    assert received_messages[0]["params"]["clientId"] == "ws-client"


@pytest.mark.asyncio
async def test_events_client_runtime_view_should_be_immutable_and_keep_original_endpoint(tmp_path: Path) -> None:
    authenticate_calls = 0

    async def handler(websocket) -> None:
        nonlocal authenticate_calls
        raw = await websocket.recv()
        message = json.loads(raw)
        authenticate_calls += 1
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"ok": True, "protocolVersion": 1},
                }
            )
        )
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            with pytest.raises(FrozenInstanceError):
                client.runtime.ws_url = "ws://127.0.0.1:1/ws"  # type: ignore[misc]

            await client.authenticate()
        finally:
            await client.close()

    assert authenticate_calls == 1


@pytest.mark.asyncio
async def test_events_client_when_unknown_notification_received_should_fail_stream(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        async for raw in websocket:
            message = json.loads(raw)
            if message["method"] == "hub.ws.authenticate":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "protocolVersion": 1},
                        }
                    )
                )
            elif message["method"] == "hub.events.subscribe":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "subscriptionId": "sub-1"},
                        }
                    )
                )
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "method": "hub.future.notification",
                            "params": {"value": 1},
                        }
                    )
                )
                break

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            await client.authenticate()
            iterator = client.read_events()
            event_task = asyncio.create_task(anext(iterator))
            await client.subscribe([INVOCATION_COMPLETED])
            with pytest.raises(RuntimeError, match="hub.event|响应"):
                await asyncio.wait_for(event_task, timeout=2)

            with pytest.raises(RuntimeError, match="尚未通过鉴权"):
                await client.subscribe([INVOCATION_COMPLETED])
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_when_connection_closes_after_buffered_event_should_end_active_reader_and_reject_new_reads(
    tmp_path: Path,
) -> None:
    async def handler(websocket) -> None:
        async for raw in websocket:
            message = json.loads(raw)
            if message["method"] == "hub.ws.authenticate":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "protocolVersion": 1},
                        }
                    )
                )
            elif message["method"] == "hub.events.subscribe":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "subscriptionId": "sub-1"},
                        }
                    )
                )
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "method": "hub.event",
                            "params": {
                                "subscriptionId": "sub-1",
                                "type": INVOCATION_COMPLETED,
                                "timeUtc": "2026-03-09T00:00:00Z",
                                "payload": {"invocationId": "invk-1"},
                            },
                        }
                    )
                )
                await websocket.close()
                break

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            await client.authenticate()
            await client.subscribe([INVOCATION_COMPLETED])
            iterator = client.read_events()

            event = await asyncio.wait_for(anext(iterator), timeout=2)

            assert event.type == INVOCATION_COMPLETED
            assert event.subscription_id == "sub-1"

            with pytest.raises(StopAsyncIteration):
                await asyncio.wait_for(anext(iterator), timeout=2)
            with pytest.raises(RuntimeError, match="尚未通过鉴权"):
                await asyncio.wait_for(anext(client.read_events()), timeout=2)
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_when_connection_terminated_should_allow_reauthenticate_and_require_resubscribe(
    tmp_path: Path,
) -> None:
    connection_count = 0

    async def handler(websocket) -> None:
        nonlocal connection_count
        connection_count += 1
        current_connection = connection_count

        async for raw in websocket:
            message = json.loads(raw)
            if message["method"] == "hub.ws.authenticate":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "protocolVersion": 1},
                        }
                    )
                )
            elif message["method"] == "hub.events.subscribe":
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": message["id"],
                            "result": {"ok": True, "subscriptionId": f"sub-{current_connection}"},
                        }
                    )
                )
                if current_connection == 1:
                    await websocket.close()
                    break
                await websocket.send(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "method": "hub.event",
                            "params": {
                                "subscriptionId": f"sub-{current_connection}",
                                "type": INVOCATION_COMPLETED,
                                "timeUtc": "2026-03-09T00:00:00Z",
                                "payload": {"invocationId": f"invk-{current_connection}"},
                            },
                        }
                    )
                )
                break

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            await client.authenticate()
            await client.subscribe([INVOCATION_COMPLETED])
            iterator = client.read_events()

            with pytest.raises(StopAsyncIteration):
                await asyncio.wait_for(anext(iterator), timeout=2)

            with pytest.raises(RuntimeError, match="尚未通过鉴权"):
                await client.subscribe([INVOCATION_COMPLETED])

            await client.authenticate()
            subscription_id = await client.subscribe([INVOCATION_COMPLETED])
            event = await asyncio.wait_for(anext(client.read_events()), timeout=2)
        finally:
            await client.close()

    assert subscription_id == "sub-2"
    assert event.type == INVOCATION_COMPLETED
    assert event.payload["invocationId"] == "invk-2"


@pytest.mark.asyncio
async def test_events_client_when_authenticate_fails_should_raise_devhub_rpc_exception(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "error": {
                        "code": -32001,
                        "message": "unauthorized",
                        "data": {"reason": "invalid_token"},
                    },
                }
            )
        )

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            with pytest.raises(DevHubRpcException) as exc_info:
                await client.authenticate()
        finally:
            await client.close()

    assert exc_info.value.code == -32001
    assert exc_info.value.reason == "invalid_token"


@pytest.mark.asyncio
async def test_events_client_when_authenticate_response_contains_non_standard_json_constant_should_raise(
    tmp_path: Path,
) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"ok": True, "protocolVersion": 1},
                }
            ).replace('"result": {', '"result": {"extra": NaN, ', 1)
        )

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            with pytest.raises(RuntimeError, match="不是合法 JSON"):
                await client.authenticate()
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_when_ws_response_id_unknown_should_raise_protocol_error(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": f"{message['id']}-unexpected",
                    "result": {"ok": True, "protocolVersion": 1},
                }
            )
        )

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(
            DevHubClientOptions(
                client_id="ws-client",
                data_dir=str(data_dir),
                request_timeout=2,
            )
        )
        try:
            with pytest.raises(RuntimeError, match="挂起请求|pending request"):
                await client.authenticate()
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
async def test_events_client_when_authenticate_called_twice_should_raise(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"ok": True, "protocolVersion": 1},
                }
            )
        )
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        try:
            await client.authenticate()
            with pytest.raises(RuntimeError, match="已完成认证"):
                await client.authenticate()
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_subscribe_when_types_is_single_string_should_raise(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"ok": True, "protocolVersion": 1},
                }
            )
        )
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
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
async def test_events_client_after_close_should_reject_subscribe_and_read(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        raw = await websocket.recv()
        message = json.loads(raw)
        await websocket.send(
            json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"ok": True, "protocolVersion": 1},
                }
            )
        )
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        data_dir = _write_data_directory(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", data_dir=str(data_dir)))
        await client.authenticate()
        await client.close()

        with pytest.raises(RuntimeError, match="事件客户端已关闭"):
            await client.subscribe([INVOCATION_COMPLETED])

        with pytest.raises(RuntimeError, match="事件客户端已关闭"):
            await anext(client.read_events())


def _write_data_directory(tmp_path: Path, port: int) -> Path:
    data_dir = tmp_path / "devhub-data"
    runtime_dir = data_dir / "runtime"
    runtime_dir.mkdir(parents=True)
    token_file = runtime_dir / "token.txt"
    token_file.write_text("token-1", encoding="utf-8")
    (runtime_dir / "hub.json").write_text(
        json.dumps(
            {
                "protocolVersion": 1,
                "pid": 12345,
                "httpBaseUrl": "http://127.0.0.1:1",
                "wsUrl": f"ws://127.0.0.1:{port}/ws",
                "tokenFile": str(token_file),
                "startedAtUtc": "2026-03-09T00:00:00Z",
                "runtimeTuning": {
                    "leaseSeconds": 30,
                    "onlineThresholdSeconds": 30,
                    "launchDedupeWindowSeconds": 30,
                },
            }
        ),
        encoding="utf-8",
    )
    return data_dir


def _create_connection_info() -> RuntimeConnectionInfo:
    return RuntimeConnectionInfo(
        runtime_directory="D:/runtime",
        token="token-fake",
        runtime=HubRuntime(
            protocol_version=1,
            pid=12345,
            http_base_url="http://127.0.0.1:57231",
            ws_url="ws://127.0.0.1:57231/ws",
            token_file="D:/runtime/token.txt",
            started_at_utc=datetime(2026, 3, 9, tzinfo=timezone.utc),
            runtime_tuning=HubRuntimeTuning(
                lease_seconds=30,
                online_threshold_seconds=30,
                launch_dedupe_window_seconds=30,
            ),
        ),
    )
