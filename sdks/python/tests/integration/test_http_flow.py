from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

from devhub_sdk import (
    AppDefinition,
    AppInstanceRegistration,
    DevHubRpcErrorCode,
    DevHubRpcException,
    InvokeCapability,
    ListDefinitionsRequest,
    ListInstancesRequest,
)
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

        definitions = client.list_definitions(ListDefinitionsRequest(scope=None))
        assert any(definition.app_id == "http.flow.app" for definition in definitions)

        definition = client.get_definition("http.flow.app", "")
        assert definition.display_name == "HTTP Flow App"
        assert definition.scope == ""

        registered = client.register_instance(
            AppInstanceRegistration(
                instance_id="http-flow-inst-1",
                app_id="http.flow.app",
                pid=99999,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
                meta={"source": "integration"},
            ),
            _instance_password("http-flow-inst-1"),
        )
        assert registered.instance_id == "http-flow-inst-1"
        instance_session_token = registered.instance_session_token
        assert instance_session_token is not None

        instances = client.list_instances(ListInstancesRequest(scope=None, app_id="http.flow.app"))
        assert len(instances) == 1

        last_seen_utc = client.heartbeat("http-flow-inst-1", instance_session_token)
        assert last_seen_utc is not None

        client.unregister_instance("http-flow-inst-1", instance_session_token)
        instances_after_unregister = client.list_instances(ListInstancesRequest(scope=None, app_id="http.flow.app"))
        assert instances_after_unregister == []


def test_instance_session_token_mismatch_should_surface_forbidden_reason() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition(
            {
                "appId": "http.token.app",
                "displayName": "HTTP Token App",
            }
        )

        client = host.create_client("http-token-client")
        registered = client.register_instance(
            AppInstanceRegistration(
                instance_id="http-token-inst-1",
                app_id="http.token.app",
                pid=99998,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            _instance_password("http-token-inst-1"),
        )

        with pytest.raises(DevHubRpcException) as heartbeat_error:
            client.heartbeat("http-token-inst-1", "wrong-token")
        assert heartbeat_error.value.code == DevHubRpcErrorCode.FORBIDDEN
        assert heartbeat_error.value.reason == "instance_session_token_mismatch"

        with pytest.raises(DevHubRpcException) as unregister_error:
            client.unregister_instance("http-token-inst-1", "wrong-token")
        assert unregister_error.value.code == DevHubRpcErrorCode.FORBIDDEN
        assert unregister_error.value.reason == "instance_session_token_mismatch"

        instance_session_token = registered.instance_session_token
        assert instance_session_token is not None
        client.unregister_instance("http-token-inst-1", instance_session_token)


def test_definition_management_should_round_trip_and_surface_host_validation() -> None:
    with DevHubHostFixture.start() as host:
        client = host.create_client("http-definition-client")

        invalid_definition = AppDefinition(app_id="http.invalid.app", display_name=" ", scope="")
        invalid = client.validate_definition(invalid_definition)
        assert invalid.ok is True
        assert invalid.valid is False
        assert invalid.errors
        assert invalid.errors[0].path.startswith("definition.")

        definition = AppDefinition(
            app_id="http.manage.app",
            display_name="Managed HTTP App",
            scope="",
            description="通过 Python SDK 写入。",
        )
        valid = client.validate_definition(definition)
        assert valid.valid is True
        assert valid.errors == []

        upserted = client.upsert_definition(definition)
        assert upserted.app_id == "http.manage.app"
        assert upserted.scope == ""
        assert client.get_definition("http.manage.app", "").display_name == "Managed HTTP App"

        with pytest.raises(DevHubRpcException) as upsert_error:
            client.upsert_definition(invalid_definition)
        assert upsert_error.value.code == DevHubRpcErrorCode.INVALID_PARAMS
        assert upsert_error.value.reason == "definition_invalid"
        assert isinstance(upsert_error.value.try_get_data_property("errors"), list)

        client.delete_definition("http.manage.app", "")
        with pytest.raises(DevHubRpcException) as deleted_error:
            client.get_definition("http.manage.app", "")
        assert deleted_error.value.code == DevHubRpcErrorCode.APP_DEFINITION_NOT_FOUND


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
                scope="",
                dedupe_key="python-sdk-launch-dedupe",
                wait_for_register_ms=800,
            )
        )
        second = client.launch(
            LaunchRequest(
                app_id="http.launch.app",
                scope="",
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


def test_host_fixture_close_should_cleanup_launch_process_tree_and_temp_dir() -> None:
    host = DevHubHostFixture.start()
    data_directory = host.data_directory
    child_pid: int | None = None

    try:
        ready_file = host.data_directory / "launch-probe" / "pid.txt"
        host.write_definition(
            {
                "appId": "http.launch.cleanup.app",
                "displayName": "HTTP Launch Cleanup App",
                "launch": {
                    "exePath": sys.executable,
                    "argsTemplate": f'"{_launch_probe_script_path()}" "{ready_file}"',
                },
            }
        )

        client = host.create_client("http-launch-cleanup-client")
        result = client.launch(
            LaunchRequest(
                app_id="http.launch.cleanup.app",
                scope="",
                dedupe_key="python-sdk-launch-cleanup",
                wait_for_register_ms=0,
            )
        )

        assert result.ok is True
        child_pid = _wait_for_child_pid(ready_file)
        assert _process_exists(child_pid) is True
    finally:
        host.close()

    child_terminated = child_pid is not None and _wait_until(lambda: not _process_exists(child_pid), timeout_seconds=5)
    if child_pid is not None and not child_terminated:
        _kill_process(child_pid)

    assert child_pid is not None
    assert child_terminated is True
    assert data_directory.exists() is False


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
        assert client_a.get_definition("parallel.http.app", "").display_name == "Parallel HTTP App A"
        assert client_b.get_definition("parallel.http.app", "").display_name == "Parallel HTTP App B"

        client_a.register_instance(
            AppInstanceRegistration(
                instance_id="parallel-http-inst-a",
                app_id="parallel.http.app",
                pid=99994,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            _instance_password("parallel-http-inst-a"),
        )

        instances_a = client_a.list_instances(ListInstancesRequest(scope=None, app_id="parallel.http.app"))
        instances_b = client_b.list_instances(ListInstancesRequest(scope=None, app_id="parallel.http.app"))

        assert [instance.instance_id for instance in instances_a] == ["parallel-http-inst-a"]
        assert instances_b == []


def _launch_script_path() -> Path:
    return Path(__file__).resolve().parents[4] / "host" / "tests" / "assets" / "launch_noop.py"


def _instance_password(instance_id: str) -> str:
    return f"python-sdk-{instance_id}"


def _launch_probe_script_path() -> Path:
    return Path(__file__).resolve().parents[1] / "assets" / "launch_probe.py"


def _wait_for_child_pid(ready_file: Path, timeout_seconds: float = 10) -> int:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if ready_file.is_file():
            return int(ready_file.read_text(encoding="utf-8").strip())
        time.sleep(0.1)
    raise TimeoutError(f"等待子进程 ready 文件超时：{ready_file}")


def _wait_until(condition, timeout_seconds: float) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if condition():
            return True
        time.sleep(0.1)
    return condition()


def _process_exists(pid: int) -> bool:
    if pid < 1:
        return False
    if os.name == "nt":
        completed = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            check=False,
            capture_output=True,
            text=True,
            stdin=subprocess.DEVNULL,
        )
        return f'"{pid}"' in completed.stdout
    try:
        os.kill(pid, 0)
    except OSError:
        return False
    return True


def _kill_process(pid: int) -> None:
    if pid < 1:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            check=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        return
    try:
        os.kill(pid, 9)
    except OSError:
        return
