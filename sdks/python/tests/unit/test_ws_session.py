from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import pytest

from devhub_sdk import DevHubClientOptions, HubRuntime, HubRuntimeTuning, RuntimeConnectionInfo
from devhub_sdk._ws_session import WebSocketJsonRpcSession


_STREAM_EOF = object()


@dataclass(slots=True)
class FakeWebSocket:
    incoming: asyncio.Queue[object] = field(default_factory=asyncio.Queue)
    sent_messages: list[str] = field(default_factory=list)
    close_reasons: list[str | None] = field(default_factory=list)

    async def send(self, message: str) -> None:
        self.sent_messages.append(message)

    async def close(self, reason: str | None = None) -> None:
        self.close_reasons.append(reason)
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
async def test_ws_session_should_enforce_single_active_reader_until_reader_is_closed() -> None:
    websocket = FakeWebSocket()

    async def connect(*_args, **_kwargs) -> FakeWebSocket:
        return websocket

    session = WebSocketJsonRpcSession(
        _create_connection_info(),
        DevHubClientOptions(client_id="ws-session-client", request_timeout=1),
        connect=connect,
    )

    first_reader = session.read_events()
    first_event_task = asyncio.create_task(anext(first_reader))
    await asyncio.sleep(0)

    second_reader = session.read_events()
    with pytest.raises(RuntimeError, match="活动读取器"):
        await anext(second_reader)

    session._stream.queue.put_nowait({"sequence": 1})
    assert await first_event_task == {"sequence": 1}

    await first_reader.aclose()

    third_reader = session.read_events()
    third_event_task = asyncio.create_task(anext(third_reader))
    await asyncio.sleep(0)
    session._stream.queue.put_nowait({"sequence": 2})
    assert await third_event_task == {"sequence": 2}

    await third_reader.aclose()
    await session.close()


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
