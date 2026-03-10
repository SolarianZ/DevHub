from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any

import pytest
import websockets

from devhub_sdk import DevHubClientOptions, DevHubEventsClient, DevHubRpcException, INVOCATION_COMPLETED


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
