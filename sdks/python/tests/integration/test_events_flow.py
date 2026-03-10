from __future__ import annotations

import asyncio

import pytest

from devhub_sdk import APP_INSTANCE_REGISTERED, AppInstanceRegistration, InvokeCapability

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
