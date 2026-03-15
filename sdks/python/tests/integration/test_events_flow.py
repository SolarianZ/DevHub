from __future__ import annotations

import asyncio

import pytest

from devhub_sdk import (
    APP_INSTANCE_REGISTERED,
    AppInstanceRegistration,
    DevHubRpcException,
    InvokeCapability,
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
async def test_ws_subscribe_unknown_type_should_return_invalid_params() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-invalid-client")
        try:
            await events_client.authenticate()

            with pytest.raises(DevHubRpcException) as exc_info:
                await events_client.subscribe(["unknown.type"])
        finally:
            await events_client.close()

    assert exc_info.value.code == -32602
    assert exc_info.value.message == "invalid_params"


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
