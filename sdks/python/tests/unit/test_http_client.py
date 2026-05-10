from __future__ import annotations

from dataclasses import FrozenInstanceError, dataclass, field
from datetime import datetime, timezone
from typing import Any

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
    DevHubCalleeError,
    InvokeCapability,
    InvokeRequest,
    InvocationTarget,
    ListDefinitionsRequest,
    ListInstancesRequest,
    LaunchRequest,
    PollRequest,
    RespondRequest,
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
class FakeHttpTransport:
    """用于验证依赖注入的 HTTP 传输。"""

    response: dict[str, Any] | BaseException
    calls: list[dict[str, Any]] = field(default_factory=list)
    close_calls: int = 0

    def send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.calls.append(
            {
                "method": method,
                "params": params,
            }
        )
        if isinstance(self.response, BaseException):
            raise self.response
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


def test_http_client_with_injected_resolver_should_reject_invalid_websocket_endpoint() -> None:
    connection_info = _create_connection_info(ws_url="ws://127.0.0.1:57231/ws?")
    resolver = FakeRuntimeResolver(connection_info)
    transport_factory = FakeHttpTransportFactory(FakeHttpTransport({"ok": True}))

    with pytest.raises(RuntimeError, match="wsUrl"):
        DevHubClient.from_runtime(
            DevHubClientOptions(client_id="http-client"),
            DevHubClientDependencies(
                runtime_resolver=resolver,
                transport_factory=transport_factory,
            ),
        )

    assert len(transport_factory.calls) == 0


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


def test_http_client_runtime_view_should_be_immutable_and_keep_original_endpoint() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "echo": {"source": "immutable-runtime"},
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(FrozenInstanceError):
        client.runtime.http_base_url = "http://127.0.0.1:1"  # type: ignore[misc]

    ping = client.ping({"source": "immutable-runtime"})

    assert ping.ok is True
    assert transport.calls[0]["method"] == "hub.ping"


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


def test_http_client_ping_should_send_params_and_parse_result() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "echo": {"value": 1},
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    ping = client.ping({"value": 1})

    assert ping.ok is True
    assert ping.echo == {"value": 1}
    assert transport.calls[0] == {
        "method": "hub.ping",
        "params": {"echo": {"value": 1}},
    }


def test_http_client_ping_when_echo_is_none_should_send_null() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "echo": None,
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    ping = client.ping(None)

    assert ping.ok is True
    assert transport.calls[0] == {
        "method": "hub.ping",
        "params": {"echo": None},
    }


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


def test_http_client_when_transport_returns_error_should_raise_devhub_rpc_exception() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        DevHubRpcException(
            code=-32001,
            message="unauthorized",
            data={"reason": "invalid_token"},
            request_id="req-http-1",
        )
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(DevHubRpcException) as exc_info:
        client.ping()

    assert exc_info.value.code == -32001
    assert exc_info.value.reason == "invalid_token"


def test_http_client_list_definitions_should_send_explicit_null_scope_filter() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "definitions": [],
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    definitions = client.list_definitions(ListDefinitionsRequest(scope=None))

    assert definitions == []
    assert transport.calls[0] == {
        "method": "hub.apps.listDefinitions",
        "params": {"scope": None},
    }


def test_http_client_when_request_result_missing_value_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "invocationId": "invk-1",
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(RuntimeError):
        client.request(InvokeRequest(app_id="test.app", method="test.request", target=InvocationTarget(scope="")))


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


def test_http_client_get_instance_should_send_request_and_parse_instance() -> None:
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
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    instance = client.get_instance("inst-1")

    assert instance.instance_id == "inst-1"
    assert instance.instance_session_token is None
    assert transport.calls[0] == {
        "method": "hub.apps.getInstance",
        "params": {"instanceId": "inst-1"},
    }


def test_http_client_get_instance_should_reject_invalid_instance_id_before_request() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport({"ok": True, "instance": {}})
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(ValueError, match="instance_id"):
        client.get_instance("inst-1.")

    assert transport.calls == []


@pytest.mark.parametrize("method_name", ["notify", "request"])
def test_http_client_invoke_should_validate_mutated_target_instance_id_before_sending(method_name: str) -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport({"ok": True, "invocationId": "invk-1", "value": None})
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )
    target = InvocationTarget(scope="", instance_id="inst-1")
    target.instance_id = "a" * 257

    with pytest.raises(ValueError, match="target.instance_id"):
        getattr(client, method_name)(InvokeRequest(app_id="test.app", method="test.invoke", target=target))

    assert transport.calls == []


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


def test_http_client_get_instance_should_propagate_remote_instance_not_found() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        DevHubRpcException(
            code=-32010,
            message="instance_not_found",
            data={"reason": "unknown_instance", "instanceId": "missing-inst"},
            request_id="req-http-1",
        )
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(DevHubRpcException) as exc_info:
        client.get_instance("missing-inst")

    assert exc_info.value.code == -32010
    assert exc_info.value.message == "instance_not_found"
    assert exc_info.value.reason == "unknown_instance"
    assert exc_info.value.try_get_data_string("instanceId") == "missing-inst"
    assert transport.calls[0] == {
        "method": "hub.apps.getInstance",
        "params": {"instanceId": "missing-inst"},
    }


def test_http_client_get_instance_when_result_contains_sensitive_fields_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "instanceSessionToken": "token-1",
            "instance": {
                "instanceId": "inst-1",
                "appId": "test.app",
                "scope": "",
                "pid": 1234,
                "registeredAtUtc": "2026-03-09T00:00:00Z",
                "lastSeenUtc": "2026-03-09T00:00:01Z",
                "invoke": {"poll": True, "respond": True},
            },
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(RuntimeError, match="instanceSessionToken"):
        client.get_instance("inst-1")

    assert transport.calls[0]["method"] == "hub.apps.getInstance"


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
        launch_id="launch-1",
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
    assert transport.calls[0]["params"]["launchId"] == "launch-1"
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


def test_http_client_launch_should_send_request_and_parse_result() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "status": "started",
            "launchId": "launch-1",
            "dedupeKey": "dedupe-1",
            "pid": 1234,
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    result = client.launch(
        LaunchRequest(
            app_id="test.app",
            scope="workspace-a",
            dedupe_key="dedupe-1",
            wait_for_register_ms=1500,
        )
    )

    assert result.status == "started"
    assert result.launch_id == "launch-1"
    assert transport.calls[0] == {
        "method": "hub.apps.launch",
        "params": {
            "appId": "test.app",
            "scope": "workspace-a",
            "dedupeKey": "dedupe-1",
            "waitForRegisterMs": 1500,
        },
    }


def test_http_client_when_launch_status_invalid_should_raise() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "status": "invalid",
            "launchId": "launch-1",
            "pid": 123,
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    with pytest.raises(RuntimeError):
        client.launch(LaunchRequest(app_id="test.app", scope=""))


def test_http_client_poll_should_send_request_and_parse_result() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport(
        {
            "ok": True,
            "serverTimeUtc": "2026-03-09T00:00:00Z",
            "items": [],
        }
    )
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    result = client.poll(
        PollRequest(
            instance_id="inst-1",
            instance_session_token="token-1",
            max_count=3,
            wait_ms=50,
        )
    )

    assert result.ok is True
    assert result.items == []
    assert transport.calls[0] == {
        "method": "hub.invoke.poll",
        "params": {
            "instanceId": "inst-1",
            "instanceSessionToken": "token-1",
            "maxCount": 3,
            "waitMs": 50,
        },
    }


def test_http_client_respond_should_send_value_and_error_requests() -> None:
    connection_info = _create_connection_info()
    resolver = FakeRuntimeResolver(connection_info)
    transport = FakeHttpTransport({"ok": True})
    transport_factory = FakeHttpTransportFactory(transport)
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="http-client"),
        DevHubClientDependencies(runtime_resolver=resolver, transport_factory=transport_factory),
    )

    client.respond(
        RespondRequest(
            instance_id="inst-1",
            instance_session_token="token-1",
            invocation_id="invk-1",
            lease_token="lease-1",
            value={"ok": True},
        )
    )
    client.respond(
        RespondRequest(
            instance_id="inst-1",
            instance_session_token="token-1",
            invocation_id="invk-2",
            lease_token="lease-2",
            error=DevHubCalleeError.create(1001, "app_error", {"reason": "boom"}),
        )
    )

    assert transport.calls[0] == {
        "method": "hub.invoke.respond",
        "params": {
            "instanceId": "inst-1",
            "instanceSessionToken": "token-1",
            "invocationId": "invk-1",
            "leaseToken": "lease-1",
            "value": {"ok": True},
        },
    }
    assert transport.calls[1] == {
        "method": "hub.invoke.respond",
        "params": {
            "instanceId": "inst-1",
            "instanceSessionToken": "token-1",
            "invocationId": "invk-2",
            "leaseToken": "lease-2",
            "error": {
                "code": 1001,
                "message": "app_error",
                "data": {"reason": "boom"},
            },
        },
    }


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
