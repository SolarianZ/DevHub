from __future__ import annotations

import asyncio

import pytest

from devhub_sdk import (
    APP_INSTANCE_REGISTERED,
    AppInstanceRegistration,
    InvokeCapability,
)

from ._host import DevHubHostFixture


@pytest.mark.asyncio
async def test_M5_E2E_004_ws_authenticate_subscribe_unsubscribe_should_control_delivery() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.flow.app", "displayName": "events.flow.app"})

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([APP_INSTANCE_REGISTERED])
            event_task = asyncio.create_task(anext(events_client.read_events()))

            client = host.create_client("events-http-client")
            client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-inst-1",
                    app_id="events.flow.app",
                    pid=99999,
                    invoke=InvokeCapability(poll=True, respond=True),
                )
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
                )
            )

            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(anext(events_client.read_events()), timeout=0.6)
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_M5_E2E_005_ws_subscribe_unknown_type_should_raise_value_error_before_request() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-invalid-client")
        try:
            await events_client.authenticate()

            with pytest.raises(ValueError, match="受支持的 DevHub 事件类型"):
                await events_client.subscribe(["unknown.type"])
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_M5_E2E_010_ws_disconnect_cleanup_should_require_resubscribe_after_reconnect() -> None:
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
                )
            )

            await second_client.subscribe([APP_INSTANCE_REGISTERED])

            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-reconnect-inst-2",
                    app_id="events.reconnect.app",
                    pid=99996,
                    invoke=InvokeCapability(poll=True, respond=True),
                )
            )

            event = await asyncio.wait_for(anext(second_client.read_events()), timeout=2)
        finally:
            await second_client.close()

    assert event.type == APP_INSTANCE_REGISTERED
    assert event.payload["instanceId"] == "events-reconnect-inst-2"


@pytest.mark.asyncio
async def test_M5_E2E_004_ws_readable_methods_should_match_published_surface() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.ws.read.app", "displayName": "events.ws.read.app"})

        client = host.create_client("events-http-client")
        client.register_instance(
            AppInstanceRegistration(
                instance_id="events-ws-read-inst-1",
                app_id="events.ws.read.app",
                pid=99995,
                invoke=InvokeCapability(poll=True, respond=True),
            )
        )

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            ping = await events_client.ping({"source": "ws"})
            definitions = await events_client.list_definitions()
            definition = await events_client.get_definition("events.ws.read.app")
            instances = await events_client.list_instances()
        finally:
            await events_client.close()

    assert ping.ok is True
    assert any(item.app_id == "events.ws.read.app" for item in definitions)
    assert definition.app_id == "events.ws.read.app"
    assert any(item.instance_id == "events-ws-read-inst-1" for item in instances)


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

            await events_client_b.authenticate()
            subscription_id_b = await events_client_b.subscribe([APP_INSTANCE_REGISTERED])

            http_client_a = host_a.create_client("parallel-events-http-a")
            http_client_b = host_b.create_client("parallel-events-http-b")

            http_client_a.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-a",
                    app_id="parallel.events.app",
                    pid=99994,
                    invoke=InvokeCapability(poll=True, respond=True),
                )
            )

            event_a = await asyncio.wait_for(anext(events_client_a.read_events()), timeout=3)

            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(anext(events_client_b.read_events()), timeout=0.6)

            http_client_b.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-b",
                    app_id="parallel.events.app",
                    pid=99993,
                    invoke=InvokeCapability(poll=True, respond=True),
                )
            )

            event_b = await asyncio.wait_for(anext(events_client_b.read_events()), timeout=3)
        finally:
            await events_client_a.close()
            await events_client_b.close()

    assert event_a.subscription_id == subscription_id_a
    assert event_a.payload["instanceId"] == "parallel-events-inst-a"
    assert event_b.subscription_id == subscription_id_b
    assert event_b.payload["instanceId"] == "parallel-events-inst-b"
