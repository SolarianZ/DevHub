from __future__ import annotations

import sys
from pathlib import Path

from devhub_sdk import AppInstanceRegistration, InvokeCapability, ListInstancesRequest
from devhub_sdk.models import LaunchRequest

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


def test_launch_should_round_trip_and_apply_dedupe_window() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition(
            {
                "appId": "http.launch.app",
                "displayName": "HTTP Launch App",
                "launch": {
                    "exePath": sys.executable,
                    "argsTemplate": str(_launch_script_path()),
                },
            }
        )

        client = host.create_client("http-launch-client")

        first = client.launch(
            LaunchRequest(
                app_id="http.launch.app",
                dedupe_key="python-sdk-launch-dedupe",
                wait_for_register_ms=800,
            )
        )
        second = client.launch(
            LaunchRequest(
                app_id="http.launch.app",
                dedupe_key="python-sdk-launch-dedupe",
                wait_for_register_ms=0,
            )
        )

        assert first.ok is True
        assert first.status in {"started", "starting"}
        assert first.launch_id
        assert second.ok is True
        assert second.status == "already_running"
        assert second.launch_id == first.launch_id


def test_two_hosts_with_different_data_dirs_should_isolate_http_state() -> None:
    with DevHubHostFixture.start() as host_a, DevHubHostFixture.start() as host_b:
        host_a.write_definition(
            {
                "appId": "parallel.http.app",
                "displayName": "Parallel HTTP App A",
            }
        )
        host_b.write_definition(
            {
                "appId": "parallel.http.app",
                "displayName": "Parallel HTTP App B",
            }
        )

        client_a = host_a.create_client("parallel-http-client-a")
        client_b = host_b.create_client("parallel-http-client-b")

        assert client_a.ping().ok is True
        assert client_b.ping().ok is True
        assert client_a.get_definition("parallel.http.app").display_name == "Parallel HTTP App A"
        assert client_b.get_definition("parallel.http.app").display_name == "Parallel HTTP App B"

        client_a.register_instance(
            AppInstanceRegistration(
                instance_id="parallel-http-inst-a",
                app_id="parallel.http.app",
                pid=99994,
                invoke=InvokeCapability(poll=True, respond=True),
            )
        )

        instances_a = client_a.list_instances(ListInstancesRequest(app_id="parallel.http.app"))
        instances_b = client_b.list_instances(ListInstancesRequest(app_id="parallel.http.app"))

        assert [instance.instance_id for instance in instances_a] == ["parallel-http-inst-a"]
        assert instances_b == []


def _launch_script_path() -> Path:
    return Path(__file__).resolve().parents[4] / "tests" / "assets" / "launch_noop.py"
