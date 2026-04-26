from __future__ import annotations

import asyncio

import pytest

from devhub_sdk import (
    APP_DEFINITION_DELETED,
    APP_DEFINITION_UPSERTED,
    APP_INSTANCE_REGISTERED,
    AppDefinition,
    AppInstanceRegistration,
    DevHubRpcErrorCode,
    DevHubRpcException,
    InvokeCapability,
    ListDefinitionsRequest,
    ListInstancesRequest,
    SDK_VERSION,
    VersionCompatibilityStatus,
)

from ._host import DevHubHostFixture


@pytest.mark.asyncio
async def test_ws_authenticate_subscribe_unsubscribe_should_control_delivery() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.flow.app", "displayName": "events.flow.app"})

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([APP_INSTANCE_REGISTERED])
            reader = events_client.read_events()
            event_task = asyncio.create_task(anext(reader))

            client = host.create_client("events-http-client")
            client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-inst-1",
                    app_id="events.flow.app",
                    pid=99999,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-inst-1"),
            )

            event = await asyncio.wait_for(event_task, timeout=3)
            assert event.subscription_id == subscription_id
            assert event.type == APP_INSTANCE_REGISTERED

            await events_client.unsubscribe(subscription_id)
            client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-inst-2",
                    app_id="events.flow.app",
                    pid=99998,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-inst-2"),
            )

            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(anext(reader), timeout=0.6)
            await reader.aclose()
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_ws_read_events_should_reject_concurrent_reader_and_allow_new_reader_after_close() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.concurrent-reader.app", "displayName": "events.concurrent-reader.app"})

        events_client = await host.create_events_client("events-concurrent-reader-client")
        try:
            await events_client.authenticate()
            await events_client.subscribe([APP_INSTANCE_REGISTERED])

            first_reader = events_client.read_events()
            first_event_task = asyncio.create_task(anext(first_reader))
            await asyncio.sleep(0)

            with pytest.raises(RuntimeError, match="活动读取器"):
                await anext(events_client.read_events())

            http_client = host.create_client("events-concurrent-reader-http-client")
            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-concurrent-reader-inst-1",
                    app_id="events.concurrent-reader.app",
                    pid=99992,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-concurrent-reader-inst-1"),
            )

            first_event = await asyncio.wait_for(first_event_task, timeout=3)
            await first_reader.aclose()

            second_reader = events_client.read_events()
            second_event_task = asyncio.create_task(anext(second_reader))
            await asyncio.sleep(0)

            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-concurrent-reader-inst-2",
                    app_id="events.concurrent-reader.app",
                    pid=99991,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-concurrent-reader-inst-2"),
            )

            second_event = await asyncio.wait_for(second_event_task, timeout=3)
            await second_reader.aclose()
        finally:
            await events_client.close()

    assert first_event.type == APP_INSTANCE_REGISTERED
    assert first_event.payload["instanceId"] == "events-concurrent-reader-inst-1"
    assert second_event.type == APP_INSTANCE_REGISTERED
    assert second_event.payload["instanceId"] == "events-concurrent-reader-inst-2"


@pytest.mark.asyncio
async def test_ws_subscribe_unknown_type_should_raise_value_error_before_request() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-invalid-client")
        try:
            await events_client.authenticate()

            with pytest.raises(ValueError, match="受支持的 DevHub 事件类型"):
                await events_client.subscribe(["unknown.type"])
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_ws_disconnect_cleanup_should_require_resubscribe_after_reconnect() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.reconnect.app", "displayName": "events.reconnect.app"})

        first_client = await host.create_events_client("events-client-1")
        try:
            await first_client.authenticate()
            await first_client.subscribe([APP_INSTANCE_REGISTERED])
        finally:
            await first_client.close()

        second_client = await host.create_events_client("events-client-2")
        try:
            await second_client.authenticate()

            http_client = host.create_client("events-reconnect-http-client")
            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-reconnect-inst-1",
                    app_id="events.reconnect.app",
                    pid=99997,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-reconnect-inst-1"),
            )

            await second_client.subscribe([APP_INSTANCE_REGISTERED])

            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-reconnect-inst-2",
                    app_id="events.reconnect.app",
                    pid=99996,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-reconnect-inst-2"),
            )

            event = await asyncio.wait_for(anext(second_client.read_events()), timeout=2)
        finally:
            await second_client.close()

    assert event.type == APP_INSTANCE_REGISTERED
    assert event.payload["instanceId"] == "events-reconnect-inst-2"


@pytest.mark.asyncio
async def test_ws_readable_methods_should_match_published_surface() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.ws.read.app", "displayName": "events.ws.read.app"})

        client = host.create_client("events-http-client")
        client.register_instance(
            AppInstanceRegistration(
                instance_id="events-ws-read-inst-1",
                app_id="events.ws.read.app",
                pid=99995,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            _instance_password("events-ws-read-inst-1"),
        )

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            ping = await events_client.ping({"source": "ws"})
            definitions = await events_client.list_definitions(ListDefinitionsRequest(scope=None))
            definition = await events_client.get_definition("events.ws.read.app", "")
            instance = await events_client.get_instance("events-ws-read-inst-1")
            instances = await events_client.list_instances(ListInstancesRequest(scope=None))
        finally:
            await events_client.close()

    assert ping.ok is True
    assert any(item.app_id == "events.ws.read.app" for item in definitions)
    assert definition.app_id == "events.ws.read.app"
    assert definition.scope == ""
    assert instance.instance_id == "events-ws-read-inst-1"
    assert instance.instance_session_token is None
    assert any(item.instance_id == "events-ws-read-inst-1" for item in instances)


@pytest.mark.asyncio
async def test_ws_version_methods_should_use_rpc_or_runtime_fallback() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-version-client")
        try:
            await events_client.authenticate()

            rpc_version: str | None = None
            try:
                rpc_version = await events_client.get_host_version()
            except DevHubRpcException as exc:
                assert exc.code == DevHubRpcErrorCode.METHOD_NOT_FOUND

            compatibility = await events_client.check_version_compatibility()
        finally:
            await events_client.close()

    expected_host_version = rpc_version if rpc_version is not None else events_client.runtime.hub_version
    assert compatibility.sdk_version == SDK_VERSION
    assert compatibility.host_version == expected_host_version
    assert compatibility.status == _expected_version_status(SDK_VERSION, expected_host_version)


@pytest.mark.asyncio
async def test_ws_get_instance_missing_should_surface_instance_not_found() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-get-instance-client")
        try:
            await events_client.authenticate()

            with pytest.raises(DevHubRpcException) as exc_info:
                await events_client.get_instance("missing-events-inst")
        finally:
            await events_client.close()

    assert exc_info.value.code == DevHubRpcErrorCode.INSTANCE_NOT_FOUND
    assert exc_info.value.message == "instance_not_found"
    assert exc_info.value.reason == "unknown_instance"
    assert exc_info.value.try_get_data_string("instanceId") == "missing-events-inst"


@pytest.mark.asyncio
async def test_ws_should_receive_definition_lifecycle_events() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-definition-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([APP_DEFINITION_UPSERTED, APP_DEFINITION_DELETED])
            reader = events_client.read_events()

            http_client = host.create_client("events-definition-http-client")
            http_client.upsert_definition(
                AppDefinition(
                    app_id="events.definition.app",
                    display_name="Events Definition App",
                    scope="",
                )
            )
            upserted = await asyncio.wait_for(anext(reader), timeout=3)

            http_client.delete_definition("events.definition.app", "")
            deleted = await asyncio.wait_for(anext(reader), timeout=3)
            await reader.aclose()
        finally:
            await events_client.close()

    assert upserted.subscription_id == subscription_id
    assert upserted.type == APP_DEFINITION_UPSERTED
    assert upserted.payload["appId"] == "events.definition.app"
    assert upserted.payload["scope"] == ""
    assert upserted.payload["definition"]["scope"] == ""
    assert upserted.payload["definition"]["displayName"] == "Events Definition App"
    assert deleted.subscription_id == subscription_id
    assert deleted.type == APP_DEFINITION_DELETED
    assert deleted.payload["appId"] == "events.definition.app"
    assert deleted.payload["scope"] == ""


@pytest.mark.asyncio
async def test_two_hosts_with_different_data_dirs_should_isolate_event_streams() -> None:
    with DevHubHostFixture.start() as host_a, DevHubHostFixture.start() as host_b:
        host_a.write_definition({"appId": "parallel.events.app", "displayName": "parallel.events.app.a"})
        host_b.write_definition({"appId": "parallel.events.app", "displayName": "parallel.events.app.b"})

        events_client_a = await host_a.create_events_client("parallel-events-client-a")
        events_client_b = await host_b.create_events_client("parallel-events-client-b")
        try:
            await events_client_a.authenticate()
            subscription_id_a = await events_client_a.subscribe([APP_INSTANCE_REGISTERED])
            reader_a = events_client_a.read_events()

            await events_client_b.authenticate()
            subscription_id_b = await events_client_b.subscribe([APP_INSTANCE_REGISTERED])
            reader_b = events_client_b.read_events()

            http_client_a = host_a.create_client("parallel-events-http-a")
            http_client_b = host_b.create_client("parallel-events-http-b")

            http_client_a.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-a",
                    app_id="parallel.events.app",
                    pid=99994,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("parallel-events-inst-a"),
            )

            event_a = await asyncio.wait_for(anext(reader_a), timeout=3)

            pending_event_b = asyncio.create_task(anext(reader_b))
            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(asyncio.shield(pending_event_b), timeout=0.6)

            http_client_b.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-b",
                    app_id="parallel.events.app",
                    pid=99993,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("parallel-events-inst-b"),
            )

            event_b = await asyncio.wait_for(pending_event_b, timeout=3)
            await reader_a.aclose()
            await reader_b.aclose()
        finally:
            await events_client_a.close()
            await events_client_b.close()

    assert event_a.subscription_id == subscription_id_a
    assert event_a.payload["instanceId"] == "parallel-events-inst-a"
    assert event_b.subscription_id == subscription_id_b
    assert event_b.payload["instanceId"] == "parallel-events-inst-b"


def _instance_password(instance_id: str) -> str:
    return f"python-sdk-{instance_id}"


def _expected_version_status(
    sdk_version: str,
    host_version: str | None,
) -> VersionCompatibilityStatus:
    sdk_parts = _parse_major_minor(sdk_version)
    host_parts = _parse_major_minor(host_version)
    if sdk_parts is None or host_parts is None:
        return VersionCompatibilityStatus.UNKNOWN
    if sdk_parts[0] != host_parts[0]:
        return VersionCompatibilityStatus.INCOMPATIBLE
    if sdk_parts[1] != host_parts[1]:
        return VersionCompatibilityStatus.UPDATE_RECOMMENDED
    return VersionCompatibilityStatus.COMPATIBLE


def _parse_major_minor(version: str | None) -> tuple[int, int] | None:
    if not isinstance(version, str):
        return None

    parts = version.split(".", 2)
    if len(parts) < 3 or not parts[0].isdigit() or not parts[1].isdigit():
        return None
    return int(parts[0]), int(parts[1])
