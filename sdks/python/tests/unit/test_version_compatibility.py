from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, AsyncIterator

import pytest

from devhub_sdk import (
    DevHubClient,
    DevHubClientDependencies,
    DevHubClientOptions,
    DevHubEventsClient,
    DevHubEventsClientDependencies,
    DevHubRpcErrorCode,
    DevHubRpcException,
    HubRuntime,
    HubRuntimeTuning,
    RuntimeConnectionInfo,
    VersionCompatibilityResult,
    VersionCompatibilityStatus,
)
from devhub_sdk import _versioning


@dataclass(slots=True)
class FakeRuntimeResolver:
    connection_info: RuntimeConnectionInfo

    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        return self.connection_info


@dataclass(slots=True)
class FakeHttpTransport:
    responses: dict[str, dict[str, Any] | BaseException]
    calls: list[dict[str, Any]] = field(default_factory=list)

    def send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.calls.append({"method": method, "params": params})
        response = self.responses[method]
        if isinstance(response, BaseException):
            raise response
        return response

    def close(self) -> None:
        pass


@dataclass(slots=True)
class FakeHttpTransportFactory:
    transport: FakeHttpTransport

    def __call__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
    ) -> FakeHttpTransport:
        return self.transport


@dataclass(slots=True)
class FakeWsSession:
    responses: dict[str, dict[str, Any] | BaseException]
    requests: list[dict[str, Any]] = field(default_factory=list)

    async def send_request(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self.requests.append({"method": method, "params": params})
        response = self.responses[method]
        if isinstance(response, BaseException):
            raise response
        return response

    async def disconnect(self, reason: str) -> None:
        return None

    async def read_events(self) -> AsyncIterator[dict[str, Any]]:
        if False:
            yield {}

    async def close(self) -> None:
        return None


@dataclass(slots=True)
class FakeWsSessionFactory:
    session: FakeWsSession

    def __call__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
    ) -> FakeWsSession:
        return self.session


@pytest.mark.parametrize(
    ("sdk_version", "host_version", "expected_status"),
    [
        ("1.4.0", "2.0.0", VersionCompatibilityStatus.INCOMPATIBLE),
        ("1.4.0", "1.5.9-beta.1", VersionCompatibilityStatus.UPDATE_RECOMMENDED),
        ("1.4.0-alpha.1+build.1", "1.4.9+build.2", VersionCompatibilityStatus.COMPATIBLE),
        ("1.4.0", "invalid", VersionCompatibilityStatus.UNKNOWN),
        ("invalid", "1.4.0", VersionCompatibilityStatus.UNKNOWN),
    ],
)
def test_evaluate_version_compatibility_should_follow_semver_rules(
    sdk_version: str,
    host_version: str,
    expected_status: VersionCompatibilityStatus,
) -> None:
    result = _versioning.evaluate_version_compatibility(sdk_version, host_version)

    assert result == VersionCompatibilityResult(
        sdk_version=sdk_version,
        host_version=host_version,
        status=expected_status,
    )


def test_http_client_get_host_version_should_send_hub_get_version() -> None:
    transport = FakeHttpTransport(
        responses={
            "hub.getVersion": {"ok": True, "version": "0.7.1"},
        }
    )
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="version-http-client"),
        DevHubClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info()),
            transport_factory=FakeHttpTransportFactory(transport),
        ),
    )

    version = client.get_host_version()

    assert version == "0.7.1"
    assert transport.calls == [{"method": "hub.getVersion", "params": None}]


def test_http_client_check_version_compatibility_should_fallback_to_runtime_hub_version(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(_versioning, "SDK_VERSION", "0.7.0")
    transport = FakeHttpTransport(
        responses={
            "hub.getVersion": DevHubRpcException(
                code=DevHubRpcErrorCode.METHOD_NOT_FOUND,
                message="method_not_found",
                data=None,
                request_id="req-version-http-fallback",
            )
        }
    )
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="version-http-client"),
        DevHubClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info(hub_version="0.7.9-beta.1")),
            transport_factory=FakeHttpTransportFactory(transport),
        ),
    )

    result = client.check_version_compatibility()

    assert result == VersionCompatibilityResult(
        sdk_version="0.7.0",
        host_version="0.7.9-beta.1",
        status=VersionCompatibilityStatus.COMPATIBLE,
    )


@pytest.mark.parametrize("hub_version", [None, "not-a-semver"])
def test_http_client_check_version_compatibility_when_fallback_is_not_comparable_should_be_unknown(
    monkeypatch: pytest.MonkeyPatch,
    hub_version: str | None,
) -> None:
    monkeypatch.setattr(_versioning, "SDK_VERSION", "0.7.0")
    transport = FakeHttpTransport(
        responses={
            "hub.getVersion": DevHubRpcException(
                code=DevHubRpcErrorCode.METHOD_NOT_FOUND,
                message="method_not_found",
                data=None,
                request_id="req-version-http-unknown",
            )
        }
    )
    client = DevHubClient.from_runtime(
        DevHubClientOptions(client_id="version-http-client"),
        DevHubClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info(hub_version=hub_version)),
            transport_factory=FakeHttpTransportFactory(transport),
        ),
    )

    result = client.check_version_compatibility()

    assert result == VersionCompatibilityResult(
        sdk_version="0.7.0",
        host_version=hub_version,
        status=VersionCompatibilityStatus.UNKNOWN,
    )


@pytest.mark.asyncio
async def test_events_client_get_host_version_should_require_authentication_without_sending() -> None:
    session = FakeWsSession(responses={})
    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="version-ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info()),
            session_factory=FakeWsSessionFactory(session),
        ),
    )
    try:
        with pytest.raises(RuntimeError, match="尚未通过鉴权"):
            await client.get_host_version()
    finally:
        await client.close()

    assert session.requests == []


@pytest.mark.asyncio
async def test_events_client_get_host_version_should_send_hub_get_version_after_authenticate() -> None:
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.getVersion": {"ok": True, "version": "0.7.2+build.3"},
        }
    )
    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="version-ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info()),
            session_factory=FakeWsSessionFactory(session),
        ),
    )
    try:
        await client.authenticate()
        version = await client.get_host_version()
    finally:
        await client.close()

    assert version == "0.7.2+build.3"
    assert [request["method"] for request in session.requests] == [
        "hub.ws.authenticate",
        "hub.getVersion",
    ]
    assert session.requests[1]["params"] is None


@pytest.mark.asyncio
async def test_events_client_check_version_compatibility_should_fallback_to_runtime_hub_version(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(_versioning, "SDK_VERSION", "0.7.0")
    session = FakeWsSession(
        responses={
            "hub.ws.authenticate": {"ok": True, "protocolVersion": 1},
            "hub.getVersion": DevHubRpcException(
                code=DevHubRpcErrorCode.METHOD_NOT_FOUND,
                message="method_not_found",
                data=None,
                request_id="req-version-ws-fallback",
            ),
        }
    )
    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="version-ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info(hub_version="0.8.0")),
            session_factory=FakeWsSessionFactory(session),
        ),
    )
    try:
        await client.authenticate()
        result = await client.check_version_compatibility()
    finally:
        await client.close()

    assert result == VersionCompatibilityResult(
        sdk_version="0.7.0",
        host_version="0.8.0",
        status=VersionCompatibilityStatus.UPDATE_RECOMMENDED,
    )


@pytest.mark.asyncio
async def test_events_client_check_version_compatibility_should_require_authentication_without_sending() -> None:
    session = FakeWsSession(responses={})
    client = await DevHubEventsClient.from_runtime(
        DevHubClientOptions(client_id="version-ws-client"),
        DevHubEventsClientDependencies(
            runtime_resolver=FakeRuntimeResolver(_create_connection_info()),
            session_factory=FakeWsSessionFactory(session),
        ),
    )
    try:
        with pytest.raises(RuntimeError, match="尚未通过鉴权"):
            await client.check_version_compatibility()
    finally:
        await client.close()

    assert session.requests == []


def _create_connection_info(hub_version: str | None = "0.7.0") -> RuntimeConnectionInfo:
    return RuntimeConnectionInfo(
        runtime_directory="/tmp/devhub-version-tests/runtime",
        token="token-version-tests",
        runtime=HubRuntime(
            protocol_version=1,
            pid=12345,
            http_base_url="http://127.0.0.1:57231",
            ws_url="ws://127.0.0.1:57231/ws",
            token_file="/tmp/devhub-version-tests/runtime/token.txt",
            started_at_utc=datetime(2026, 3, 9, tzinfo=timezone.utc),
            runtime_tuning=HubRuntimeTuning(
                lease_seconds=15,
                online_threshold_seconds=30,
                launch_dedupe_window_seconds=45,
                launch_register_timeout_seconds=60,
            ),
            hub_version=hub_version,
        ),
    )
