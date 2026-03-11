from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, AsyncIterator

import pytest
import websockets

from devhub_sdk import (
    DevHubClientOptions,
    DevHubEvent,
    DevHubEventsClient,
    DevHubRpcException,
    HubRuntime,
    HubRuntimeTuning,
    INVOCATION_COMPLETED,
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
    events: list[DevHubEvent]
    requests: list[dict[str, Any]] = field(default_factory=list)
    closed: bool = False

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.requests.append({"method": method, "params": params})
        response = self.responses[method]
        if isinstance(response, BaseException):
            raise response
        return response

    async def read_events(self) -> AsyncIterator[DevHubEvent]:
        for event in self.events:
            yield event

    async def close(self) -> None:
        self.closed = True


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
            DevHubEvent(
                subscription_id="sub-fake",
                type=INVOCATION_COMPLETED,
                time_utc=datetime(2026, 3, 9, tzinfo=timezone.utc),
                payload={"invocationId": "invk-fake"},
            )
        ],
    )

    client = DevHubEventsClient(
        DevHubClientOptions(client_id="ws-client"),
        runtime_resolver=resolver,
        session=session,
    )
    try:
        await client.authenticate()
        subscription_id = await client.subscribe([INVOCATION_COMPLETED])
        event = await anext(client.read_events())
    finally:
        await client.close()

    assert len(resolver.calls) == 1
    assert subscription_id == "sub-fake"
    assert event.type == INVOCATION_COMPLETED
    assert event.payload["invocationId"] == "invk-fake"
    assert session.requests[0]["method"] == "hub.ws.authenticate"
    assert session.requests[0]["params"]["token"] == "token-fake"
    assert session.requests[1]["params"] == {"types": [INVOCATION_COMPLETED]}
    assert session.closed is True


@pytest.mark.asyncio
async def test_events_client_before_authenticate_should_reject_read_events(tmp_path: Path) -> None:
    async def handler(websocket) -> None:
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
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
async def test_events_client_when_connection_closes_after_queued_event_should_end_stream_repeatedly(tmp_path: Path) -> None:
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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
        try:
            await client.authenticate()
            await client.subscribe([INVOCATION_COMPLETED])

            event = await asyncio.wait_for(anext(client.read_events()), timeout=2)

            assert event.type == INVOCATION_COMPLETED
            assert event.subscription_id == "sub-1"

            with pytest.raises(StopAsyncIteration):
                await asyncio.wait_for(anext(client.read_events()), timeout=2)
            with pytest.raises(StopAsyncIteration):
                await asyncio.wait_for(anext(client.read_events()), timeout=2)
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_when_connection_terminated_should_raise_runtime_error_on_followup_request(tmp_path: Path) -> None:
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
                await websocket.close()
                break

    async with websockets.serve(handler, "127.0.0.1", 0) as server:
        port = server.sockets[0].getsockname()[1]
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
        try:
            await client.authenticate()
            await client.subscribe([INVOCATION_COMPLETED])

            with pytest.raises(StopAsyncIteration):
                await asyncio.wait_for(anext(client.read_events()), timeout=2)

            with pytest.raises(RuntimeError, match="事件流已终止"):
                await client.subscribe([INVOCATION_COMPLETED])
        finally:
            await client.close()


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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
        try:
            with pytest.raises(DevHubRpcException) as exc_info:
                await client.authenticate()
        finally:
            await client.close()

    assert exc_info.value.code == -32001
    assert exc_info.value.reason == "invalid_token"


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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(
            DevHubClientOptions(
                client_id="ws-client",
                runtime_dir=str(runtime_dir),
                request_timeout=2,
            )
        )
        try:
            with pytest.raises(RuntimeError, match="id .*挂起请求"):
                await client.authenticate()
        finally:
            await client.close()


@pytest.mark.asyncio
async def test_events_client_when_authenticate_fails_should_close_session() -> None:
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

    client = DevHubEventsClient(
        DevHubClientOptions(client_id="ws-client"),
        runtime_resolver=resolver,
        session=session,
    )

    with pytest.raises(DevHubRpcException):
        await client.authenticate()

    assert session.closed is True


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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
        try:
            await client.authenticate()
            with pytest.raises(ValueError, match="事件类型字符串序列"):
                await client.subscribe(INVOCATION_COMPLETED)
        finally:
            await client.close()


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
        runtime_dir = _write_runtime(tmp_path, port)

        client = await DevHubEventsClient.from_runtime(DevHubClientOptions(client_id="ws-client", runtime_dir=str(runtime_dir)))
        await client.authenticate()
        await client.close()

        with pytest.raises(RuntimeError, match="事件客户端已关闭"):
            await client.subscribe([INVOCATION_COMPLETED])

        with pytest.raises(RuntimeError, match="事件客户端已关闭"):
            await anext(client.read_events())


def _write_runtime(tmp_path: Path, port: int) -> Path:
    runtime_dir = tmp_path / "runtime"
    runtime_dir.mkdir()
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
    return runtime_dir


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
