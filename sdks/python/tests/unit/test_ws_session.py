from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import pytest

from devhub_sdk import AbandonedRequestFilter, DevHubClientOptions, HubRuntime, HubRuntimeTuning, RuntimeConnectionInfo
from devhub_sdk._ws_session import WebSocketJsonRpcSession


_STREAM_EOF = object()


@dataclass(slots=True)
class FakeWebSocket:
    incoming: asyncio.Queue[object] = field(default_factory=asyncio.Queue)
    sent_messages: list[str] = field(default_factory=list)
    close_reasons: list[str | None] = field(default_factory=list)
    close_calls: int = 0
    block_close_until_cancelled: bool = False
    close_attempted: asyncio.Event = field(default_factory=asyncio.Event)
    close_cancelled: asyncio.Event = field(default_factory=asyncio.Event)

    async def send(self, message: str) -> None:
        self.sent_messages.append(message)

    async def close(self, reason: str | None = None) -> None:
        self.close_calls += 1
        self.close_reasons.append(reason)
        self.close_attempted.set()
        if self.block_close_until_cancelled:
            try:
                await asyncio.Future()
            except asyncio.CancelledError:
                self.close_cancelled.set()
                raise
        await self.incoming.put(_STREAM_EOF)

    def __aiter__(self) -> "FakeWebSocket":
        return self

    async def __anext__(self) -> str:
        item = await self.incoming.get()
        if item is _STREAM_EOF:
            raise StopAsyncIteration
        if isinstance(item, BaseException):
            raise item
        return item  # type: ignore[return-value]

    async def emit_json(self, payload: dict[str, Any]) -> None:
        await self.incoming.put(json.dumps(payload))


@dataclass(slots=True)
class ControlledConnect:
    websocket: FakeWebSocket
    connect_started: asyncio.Event = field(default_factory=asyncio.Event)
    allow_connect: asyncio.Event = field(default_factory=asyncio.Event)
    calls: int = 0

    async def __call__(self, *_args, **_kwargs) -> FakeWebSocket:
        self.calls += 1
        self.connect_started.set()
        await self.allow_connect.wait()
        return self.websocket


async def _wait_until(predicate, *, timeout: float = 1.0) -> None:
    deadline = asyncio.get_running_loop().time() + timeout
    while not predicate():
        if asyncio.get_running_loop().time() >= deadline:
            raise AssertionError("等待条件成立超时。")
        await asyncio.sleep(0.01)


async def _create_abandoned_request(
    session: WebSocketJsonRpcSession,
    websocket: FakeWebSocket,
    method: str,
    params: dict[str, Any] | None,
) -> dict[str, Any]:
    with pytest.raises(asyncio.TimeoutError):
        await session.send_request(method, params)
    return json.loads(websocket.sent_messages[-1])


def test_ws_session_abandoned_request_maintenance_should_revalidate_mutated_filter() -> None:
    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
    )
    request_filter = AbandonedRequestFilter(app_id="valid.app")
    request_filter.app_id = "Invalid App Id"

    with pytest.raises(ValueError, match="app_id 必须符合 appId 格式要求"):
        session.get_abandoned_request_count(request_filter)


@pytest.mark.asyncio
async def test_ws_session_when_first_requests_are_concurrent_should_share_single_connection() -> None:
    websocket = FakeWebSocket()
    connect = ControlledConnect(websocket)
    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
        connect=connect,
    )

    first_task = asyncio.create_task(session.send_request("hub.ping", {"echo": 1}))
    second_task = asyncio.create_task(session.send_request("hub.ping", {"echo": 2}))

    await connect.connect_started.wait()
    connect.allow_connect.set()
    await _wait_until(lambda: len(websocket.sent_messages) == 2)

    first_request = json.loads(websocket.sent_messages[0])
    second_request = json.loads(websocket.sent_messages[1])
    assert first_request["id"] != second_request["id"]

    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": first_request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": second_request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:01Z"},
        }
    )

    first_result, second_result = await asyncio.gather(first_task, second_task)
    await session.close()

    assert connect.calls == 1
    assert first_result["ok"] is True
    assert second_result["ok"] is True


@pytest.mark.asyncio
async def test_ws_session_when_late_response_matches_timed_out_request_should_ignore_it() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=0.05),
        connect=connect,
    )

    with pytest.raises(asyncio.TimeoutError):
        await session.send_request("hub.ping", {"echo": "slow"})

    first_request = json.loads(websocket.sent_messages[0])
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": first_request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )
    await asyncio.sleep(0)
    assert session.get_abandoned_request_count() == 1

    second_task = asyncio.create_task(session.send_request("hub.ping", {"echo": "fast"}))
    await _wait_until(lambda: len(websocket.sent_messages) == 2)
    second_request = json.loads(websocket.sent_messages[1])
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": second_request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:01Z", "echo": "fast"},
        }
    )

    second_result = await second_task

    assert second_result["echo"] == "fast"
    assert session.is_terminated() is False
    await session.close()


@pytest.mark.asyncio
async def test_ws_session_should_count_and_clear_abandoned_requests_by_filter() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=0.02),
        connect=connect,
    )

    first_request = await _create_abandoned_request(
        session,
        websocket,
        "hub.apps.getDefinition",
        {"appId": "app-a", "scope": ""},
    )
    await _create_abandoned_request(
        session,
        websocket,
        "hub.apps.listInstances",
        {"scope": None, "appId": "app-b"},
    )
    third_request = await _create_abandoned_request(
        session,
        websocket,
        "hub.apps.getInstance",
        {"instanceId": "inst-1"},
    )

    session._abandoned_request_ids[first_request["id"]].abandoned_at -= 10
    session._abandoned_request_ids[third_request["id"]].abandoned_at -= 10

    assert session.get_abandoned_request_count() == 3
    assert session.get_abandoned_request_count(AbandonedRequestFilter(app_id="app-a")) == 1
    assert session.get_abandoned_request_count(AbandonedRequestFilter(app_id="app-b")) == 1
    assert session.get_abandoned_request_count(AbandonedRequestFilter(method="hub.apps.getInstance")) == 1
    assert session.get_abandoned_request_count(AbandonedRequestFilter(older_than_seconds=5)) == 2
    assert session.get_abandoned_request_count(
        AbandonedRequestFilter(older_than_seconds=5, app_id="app-a")
    ) == 1

    assert session.clear_abandoned_requests(
        AbandonedRequestFilter(older_than_seconds=5, app_id="app-a")
    ) == 1
    assert session.get_abandoned_request_count() == 2
    assert session.clear_abandoned_requests(AbandonedRequestFilter(app_id="app-b")) == 1
    assert session.clear_abandoned_requests(AbandonedRequestFilter(method="hub.apps.getInstance")) == 1
    assert session.get_abandoned_request_count() == 0

    await session.close()


@pytest.mark.asyncio
async def test_ws_session_when_manually_cleared_request_receives_late_response_should_fault_session() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=0.05),
        connect=connect,
    )

    request = await _create_abandoned_request(session, websocket, "hub.ping", {"echo": "slow"})

    assert session.get_abandoned_request_count() == 1
    assert session.clear_abandoned_requests() == 1
    assert session.get_abandoned_request_count() == 0

    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )
    await asyncio.sleep(0)

    assert session.is_terminated() is True
    with pytest.raises(RuntimeError, match="事件流已终止"):
        await session.send_request("hub.ping", None)

    await session.close()


@pytest.mark.asyncio
async def test_ws_session_when_response_id_is_unknown_should_fault_session() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
        connect=connect,
    )

    request_task = asyncio.create_task(session.send_request("hub.ping", None))
    await _wait_until(lambda: len(websocket.sent_messages) == 1)

    request = json.loads(websocket.sent_messages[0])
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": f"{request['id']}-unexpected",
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )

    with pytest.raises(RuntimeError, match="挂起请求"):
        await request_task
    with pytest.raises(RuntimeError, match="事件流已终止"):
        await session.send_request("hub.ping", None)

    await session.close()


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("response_id", "message"),
    [
        (1.5, "Int64 范围内整数"),
        (9223372036854775808, "Int64 范围"),
    ],
)
async def test_ws_session_when_numeric_response_id_violates_protocol_should_fault_session(
    response_id,
    message: str,
) -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
        connect=connect,
    )

    request_task = asyncio.create_task(session.send_request("hub.ping", None))
    await _wait_until(lambda: len(websocket.sent_messages) == 1)

    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": response_id,
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )

    with pytest.raises(RuntimeError, match=message):
        await request_task
    with pytest.raises(RuntimeError, match="事件流已终止"):
        await session.send_request("hub.ping", None)

    await session.close()


@pytest.mark.asyncio
async def test_ws_session_should_enforce_single_active_reader_until_reader_is_closed() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
        connect=connect,
    )

    ping_task = asyncio.create_task(session.send_request("hub.ping", None))
    await _wait_until(lambda: len(websocket.sent_messages) == 1)
    ping_request = json.loads(websocket.sent_messages[0])
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "id": ping_request["id"],
            "result": {"ok": True, "serverTimeUtc": "2026-03-09T00:00:00Z"},
        }
    )
    assert (await ping_task)["ok"] is True

    first_reader = session.read_events()
    first_event_task = asyncio.create_task(anext(first_reader))
    await asyncio.sleep(0)

    second_reader = session.read_events()
    with pytest.raises(RuntimeError, match="活动读取器"):
        await anext(second_reader)

    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "method": "hub.event",
            "params": {"sequence": 1},
        }
    )
    assert await first_event_task == {"sequence": 1}

    await first_reader.aclose()

    third_reader = session.read_events()
    third_event_task = asyncio.create_task(anext(third_reader))
    await asyncio.sleep(0)
    await websocket.emit_json(
        {
            "jsonrpc": "2.0",
            "method": "hub.event",
            "params": {"sequence": 2},
        }
    )
    assert await third_event_task == {"sequence": 2}

    await third_reader.aclose()
    await session.close()


@pytest.mark.asyncio
async def test_ws_session_close_should_bound_cleanup_when_websocket_close_hangs() -> None:
    websocket = FakeWebSocket(block_close_until_cancelled=True)

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=None),
        connect=connect,
    )

    request_task = asyncio.create_task(session.send_request("hub.ping", None))
    await _wait_until(lambda: len(websocket.sent_messages) == 1)

    await asyncio.wait_for(session.close(), timeout=1.2)

    await websocket.close_attempted.wait()
    await websocket.close_cancelled.wait()
    assert websocket.close_calls == 1
    assert websocket.close_reasons == ["session_closed"]
    assert session.is_terminated() is True
    with pytest.raises(RuntimeError, match="WebSocket 连接已关闭"):
        await request_task


@pytest.mark.asyncio
async def test_ws_session_disconnect_should_bound_cleanup_when_websocket_close_hangs() -> None:
    websocket = FakeWebSocket(block_close_until_cancelled=True)

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=None),
        connect=connect,
    )

    request_task = asyncio.create_task(session.send_request("hub.ping", None))
    await _wait_until(lambda: len(websocket.sent_messages) == 1)

    await asyncio.wait_for(session.disconnect("manual_disconnect"), timeout=1.2)

    await websocket.close_attempted.wait()
    await websocket.close_cancelled.wait()
    assert websocket.close_calls == 1
    assert websocket.close_reasons == ["manual_disconnect"]
    assert session.is_terminated() is True
    with pytest.raises(RuntimeError, match="WebSocket 连接已关闭"):
        await request_task


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
                launch_register_timeout_seconds=30,
            ),
        ),
    )
