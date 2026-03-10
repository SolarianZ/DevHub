from __future__ import annotations

from devhub_sdk import AppInstanceRegistration, InvokeCapability, ListInstancesRequest

from ._host import DevHubHostFixture


def test_ping_and_apps_flow_should_succeed() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition(
            {
                "appId": "http.flow.app",
                "displayName": "HTTP Flow App",
                "description": "用于 Python SDK HTTP 链路测试。",
            }
        )

        client = host.create_client("http-flow-client")

        ping = client.ping({"value": 1})
        assert ping.ok is True
        assert ping.echo["value"] == 1

        definitions = client.list_definitions()
        assert any(definition.app_id == "http.flow.app" for definition in definitions)

        definition = client.get_definition("http.flow.app")
        assert definition.display_name == "HTTP Flow App"

        registered = client.register_instance(
            AppInstanceRegistration(
                instance_id="http-flow-inst-1",
                app_id="http.flow.app",
                pid=99999,
                invoke=InvokeCapability(poll=True, respond=True),
                meta={"source": "integration"},
            )
        )
        assert registered.instance_id == "http-flow-inst-1"

        instances = client.list_instances(ListInstancesRequest(app_id="http.flow.app"))
        assert len(instances) == 1

        last_seen_utc = client.heartbeat("http-flow-inst-1")
        assert last_seen_utc is not None

        client.unregister_instance("http-flow-inst-1")
        instances_after_unregister = client.list_instances(ListInstancesRequest(app_id="http.flow.app"))
        assert instances_after_unregister == []
