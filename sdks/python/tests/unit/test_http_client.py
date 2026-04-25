from __future__ import annotations

import json
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable

import pytest

from devhub_sdk import (
    AppDefinition,
    AppInstanceRegistration,
    DevHubClient,
    DevHubClientDependencies,
    DevHubClientOptions,
    DevHubRpcException,
    HubRuntime,
    HubRuntimeTuning,
    InvokeCapability,
    InvokeRequest,
    InvocationTarget,
    ListDefinitionsRequest,
    ListInstancesRequest,
    LaunchRequest,
    RuntimeConnectionInfo,
)


@dataclass(slots=True)
class HttpScenario:
    """HTTP 场景数据。"""

    responder: Callable[[dict[str, Any]], dict[str, Any] | str]
    requests: list[dict[str, Any]] = field(default_factory=list)
    headers: list[dict[str, str]] = field(default_factory=list)


@dataclass(slots=True)
class FakeRuntimeResolver:
    """用于验证依赖注入的运行时解析器。"""

    connection_info: RuntimeConnectionInfo
    calls: list[DevHubClientOptions] = field(default_factory=list)

    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        self.calls.append(options.clone())
        return self.connection_info


@dataclass(slots=True)
class FakeHttpTransport:
    """用于验证依赖注入的 HTTP 传输。"""

    response: dict[str, Any]
    calls: list[dict[str, Any]] = field(default_factory=list)
    close_calls: int = 0

    def send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.calls.append(
            {
                "method": method,
                "params": params,
            }
        )
        return self.response

    def close(self) -> None:
        self.close_calls += 1


@dataclass(slots=True)
class FakeHttpTransportFactory:
    """用于验证传输工厂接线的 HTTP 工厂。"""

    transport: FakeHttpTransport
    calls: list[dict[str, Any]] = field(default_factory=list)

    def __call__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
    ) -> FakeHttpTransport:
        self.calls.append(
            {
                "connection_info": connection_info,
                "options": options.clone(),
            }
        )
        return self.transport


def test_http_client_with_injected_resolver_and_transport_should_use_abstractions() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "echo": {"source": "fake"},
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)

    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(
            runtime_resolver=resolver,
            transport_factory=transport_factory,
        ),
    )

    ping = client.ping({"source": "fake"})

    assert ping.ok is True
    assert ping.echo == {"source": "fake"}
    assert client.runtime.http_base_url == "http://127.0.0.1:57231"
    assert len(resolver.calls) == 1
    assert len(transport_factory.calls) == 1
    assert transport.calls[0]["method"] == "hub.ping"
    assert transport.calls[0]["params"] == {"echo": {"source": "fake"}}
    assert transport_factory.calls[0]["connection_info"].token == "token-fake"
    assert transport_factory.calls[0]["options"].client_id == "http-client"


def test_http_client_close_should_forward_to_transport_once() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)

    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(
            runtime_resolver=resolver,
            transport_factory=transport_factory,
        ),
    )

    client.close()
    client.close()

    assert transport.close_calls == 1


def test_http_client_context_manager_should_close_transport_on_exit() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)

    with DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(
            runtime_resolver=resolver,
            transport_factory=transport_factory,
        ),
    ) as client:
        assert client.runtime.http_base_url == "http://127.0.0.1:57231"

    assert transport.close_calls == 1


def test_http_client_after_close_should_reject_rpc_without_calling_transport() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)

    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(
            runtime_resolver=resolver,
            transport_factory=transport_factory,
        ),
    )

    client.close()

    with pytest.raises(RuntimeError, match="HTTP 客户端已关闭"):
        client.ping()

    assert transport.calls == []


def test_http_client_ping_should_send_headers_and_parse_result(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_ping_success_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        ping = client.ping({"value": 1})

        assert ping.ok is True
        assert ping.echo == {"value": 1}
        assert scenario.headers[0]["x-devhub-protocol"] == "1"
        assert scenario.headers[0]["x-devhub-clientid"] == "http-client"
        assert scenario.headers[0]["authorization"] == "Bearer token-1"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_ping_when_echo_is_none_should_send_null(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_ping_success_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        ping = client.ping(None)

        assert ping.ok is True
        assert "params" in scenario.requests[0]
        assert scenario.requests[0]["params"]["echo"] is None
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_ping_when_echo_contains_unsupported_json_should_raise_before_transport() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)

    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(
            runtime_resolver=resolver,
            transport_factory=transport_factory,
        ),
    )

    with pytest.raises(ValueError, match=r"echo\.callback 包含不支持的 JSON 类型。"):
        client.ping({"callback": lambda: "ignored"})

    assert transport.calls == []


def test_http_client_when_server_returns_error_should_raise_devhub_rpc_exception(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_unauthorized_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        with pytest.raises(DevHubRpcException) as exc_info:
            client.ping()

        assert exc_info.value.code == -32001
        assert exc_info.value.reason == "invalid_token"
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_when_error_data_is_not_object_should_raise_runtime_error(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_invalid_error_data_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        with pytest.raises(RuntimeError, match="error.data"):
            client.ping()
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_when_response_contains_non_standard_json_constant_should_raise(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_ping_response_with_non_standard_json_constant)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        with pytest.raises(RuntimeError, match="不是合法 JSON"):
            client.ping({"value": 1})
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_list_definitions_should_send_explicit_null_scope_filter(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_list_definitions_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        definitions = client.list_definitions(ListDefinitionsRequest(scope=None))

        assert definitions == []
        assert scenario.requests[0]["params"] == {"scope": None}
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_when_request_result_missing_value_should_raise(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_request_missing_value_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        with pytest.raises(RuntimeError):
            client.request(InvokeRequest(app_id="test.app", method="test.request", target=InvocationTarget(scope="")))
    finally:
        server.shutdown()
        thread.join(timeout=5)


def test_http_client_validate_definition_should_send_params_and_parse_result() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "valid": False,
            "errors": [
                {
                    "path": "definition.appId",
                    "code": "invalid_app_id",
                    "message": "invalid",
                }
            ],
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    result = client.validate_definition(AppDefinition(app_id="test.app", display_name="Test App", scope=""))

    assert result.valid is False
    assert result.errors[0].code == "invalid_app_id"
    assert transport.calls[0] == {
        "method": "hub.apps.validateDefinition",
        "params": {
            "definition": {
                "appId": "test.app",
                "scope": "",
                "displayName": "Test App",
            }
        },
    }


def test_http_client_upsert_definition_should_send_request_and_parse_definition() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "definition": {
                "appId": "test.app",
                "scope": "",
                "displayName": "Test App",
            },
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    definition = client.upsert_definition(AppDefinition(app_id="test.app", display_name="Test App", scope=""))

    assert definition.app_id == "test.app"
    assert definition.scope == ""
    assert transport.calls[0]["method"] == "hub.apps.upsertDefinition"


def test_http_client_get_definition_should_send_request_and_parse_definition() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "definition": {
                "appId": "test.app",
                "scope": "workspace-a",
                "displayName": "Test App",
            },
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    definition = client.get_definition("test.app", "workspace-a")

    assert definition.scope == "workspace-a"
    assert transport.calls[0] == {
        "method": "hub.apps.getDefinition",
        "params": {"appId": "test.app", "scope": "workspace-a"},
    }


def test_http_client_delete_definition_should_send_request() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport({"ok": True})
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    client.delete_definition("test.app", "")

    assert transport.calls[0] == {
        "method": "hub.apps.deleteDefinition",
        "params": {"appId": "test.app", "scope": ""},
    }


def test_http_client_list_instances_should_send_explicit_scope_filter() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "instances": [],
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    instances = client.list_instances(ListInstancesRequest(scope=None, app_id="test.app"))

    assert instances == []
    assert transport.calls[0] == {
        "method": "hub.apps.listInstances",
        "params": {"appId": "test.app", "scope": None},
    }


def test_http_client_instance_lifecycle_methods_should_forward_instance_session_token() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "instance": {
                "instanceId": "inst-1",
                "appId": "test.app",
                "scope": "",
                "pid": 1234,
                "registeredAtUtc": "2026-03-09T00:00:00Z",
                "lastSeenUtc": "2026-03-09T00:00:01Z",
                "invoke": {"poll": True, "respond": True},
            },
            "instanceSessionToken": "token-1",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    registered = client.register_instance(
        AppInstanceRegistration(
            instance_id="inst-1",
            app_id="test.app",
            pid=1234,
            invoke=InvokeCapability(poll=True, respond=True),
            scope="",
        ),
        "secret-1",
    )
    transport.response = {
        "ok": True,
        "lastSeenUtc": "2026-03-09T00:00:02Z",
    }
    client.heartbeat("inst-1", "token-1")
    transport.response = {"ok": True}
    client.unregister_instance("inst-1", "token-1")

    assert registered.instance_session_token == "token-1"
    assert transport.calls[0]["params"]["password"] == "secret-1"
    assert "password" not in transport.calls[0]["params"]["instance"]
    assert transport.calls[1] == {
        "method": "hub.apps.heartbeat",
        "params": {
            "instanceId": "inst-1",
            "instanceSessionToken": "token-1",
        },
    }
    assert transport.calls[2] == {
        "method": "hub.apps.unregisterInstance",
        "params": {
            "instanceId": "inst-1",
            "instanceSessionToken": "token-1",
        },
    }


def test_http_client_when_launch_status_invalid_should_raise(tmp_path: Path) -> None:
    scenario = HttpScenario(responder=_launch_invalid_status_response)
    server, thread = _start_http_server(scenario)
    try:
        data_dir = _write_data_directory(tmp_path, server.server_address[1])
        client = DevHubClient.from_runtime(DevHubClientOptions(client_id="http-client", data_dir=str(data_dir)))

        with pytest.raises(RuntimeError):
            client.launch(LaunchRequest(app_id="test.app", scope=""))
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
            body = response.encode("utf-8") if isinstance(response, str) else json.dumps(response).encode("utf-8")
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
    return data_dir


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


def _invalid_error_data_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "error": {
            "code": -32001,
            "message": "unauthorized",
            "data": "invalid_token",
        },
    }


def _ping_response_with_non_standard_json_constant(request: dict[str, Any]) -> str:
    return json.dumps(
        {
            "jsonrpc": "2.0",
            "id": request["id"],
            "result": {
                "ok": True,
                "serverTimeUtc": "2026-03-09T00:00:00Z",
                "echo": request["params"]["echo"],
            },
        }
    ).replace('"result": {', '"result": {"extra": NaN, ', 1)


def _list_definitions_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "result": {
            "ok": True,
            "definitions": [],
        },
    }


def _request_missing_value_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "result": {
            "ok": True,
            "invocationId": "invk-1",
        },
    }


def _launch_invalid_status_response(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request["id"],
        "result": {
            "ok": True,
            "status": "invalid",
            "launchId": "launch-1",
            "pid": 123,
        },
    }


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
