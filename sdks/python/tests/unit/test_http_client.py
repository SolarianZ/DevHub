from __future__ import annotations

import json
import threading
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable

import pytest

from devhub_sdk import DevHubClient, DevHubClientOptions, DevHubRpcException


@dataclass(slots=True)
class HttpScenario:
    """HTTP 场景数据。"""

    responder: Callable[[dict[str, Any]], dict[str, Any]]
    requests: list[dict[str, Any]] = field(default_factory=list)
    headers: list[dict[str, str]] = field(default_factory=list)


def test_http_client_ping_should_send_headers_and_parse_result(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_ping_success_response)
    server, thread = _start_http_server(scenario)
    try:
        runtime_dir = _write_runtime(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", runtime_dir=str(runtime_dir)))

        ping = client.ping({"value": 1})

        assert ping.ok is True
        assert ping.echo == {"value": 1}
        assert scenario.headers[0]["x-devhub-protocol"] == "1"
        assert scenario.headers[0]["x-devhub-clientid"] == "http-client"
        assert scenario.headers[0]["authorization"] == "Bearer token-1"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_when_server_returns_error_should_raise_devhub_rpc_exception(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_unauthorized_response)
    server, thread = _start_http_server(scenario)
    try:
        runtime_dir = _write_runtime(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", runtime_dir=str(runtime_dir)))

        with pytest.raises(DevHubRpcException) as exc_info:
            client.ping()

        assert exc_info.value.code == -32001
        assert exc_info.value.reason == "invalid_token"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def _start_http_server(scenario: HttpScenario) -> tuple[ThreadingHTTPServer, threading.Thread]:
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self) -> None:  # noqa: N802
            length = int(self.headers["Content-Length"])
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            scenario.requests.append(payload)
            scenario.headers.append({name.lower(): value for name, value in self.headers.items()})

            response = scenario.responder(payload)
            body = json.dumps(response).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, format: str, *args: Any) -> None:  # noqa: A003
            return

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread


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
                "httpBaseUrl": f"http://127.0.0.1:{port}",
                "wsUrl": "ws://127.0.0.1:1/ws",
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


def _ping_success_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "result": {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "echo": request["params"]["echo"],
        },
    }


def _unauthorized_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "error": {
            "code": -32001,
            "message": "unauthorized",
            "data": {"reason": "invalid_token"},
        },
    }
