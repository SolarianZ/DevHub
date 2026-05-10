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
    SDK_VERSION,
    VersionCompatibilityStatus,
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

        exact_instance = client.get_instance("http-flow-inst-1")
        assert exact_instance.instance_id == "http-flow-inst-1"
        assert exact_instance.meta == {"source": "integration"}
        assert exact_instance.instance_session_token is None

        instances = client.list_instances(ListInstancesRequest(scope=None, app_id="http.flow.app"))
        assert len(instances) == 1

        last_seen_utc = client.heartbeat("http-flow-inst-1", instance_session_token)
        assert last_seen_utc is not None

        client.unregister_instance("http-flow-inst-1", instance_session_token)
        instances_after_unregister = client.list_instances(ListInstancesRequest(scope=None, app_id="http.flow.app"))
        assert instances_after_unregister == []


def test_version_methods_should_use_rpc_or_runtime_fallback() -> None:
    with DevHubHostFixture.start() as host:
        client = host.create_client("http-version-client")

        rpc_version: str | None = None
        try:
            rpc_version = client.get_host_version()
        except DevHubRpcException as exc:
            assert exc.code == DevHubRpcErrorCode.METHOD_NOT_FOUND

        compatibility = client.check_version_compatibility()

    expected_host_version = rpc_version if rpc_version is not None else client.runtime.hub_version
    assert compatibility.sdk_version == SDK_VERSION
    assert compatibility.host_version == expected_host_version
    assert compatibility.status == _expected_version_status(SDK_VERSION, expected_host_version)


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


def test_get_instance_missing_should_surface_instance_not_found() -> None:
    with DevHubHostFixture.start() as host:
        client = host.create_client("http-get-instance-client")

        with pytest.raises(DevHubRpcException) as exc_info:
            client.get_instance("missing-http-inst")

    assert exc_info.value.code == DevHubRpcErrorCode.INSTANCE_NOT_FOUND
    assert exc_info.value.message == "instance_not_found"
    assert exc_info.value.reason == "unknown_instance"
    assert exc_info.value.try_get_data_string("instanceId") == "missing-http-inst"


def test_definition_management_should_round_trip_and_surface_host_validation() -> None:
    with DevHubHostFixture.start() as host:
        client = host.create_client("http-definition-client")

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

        with pytest.raises(ValueError, match="display_name"):
            AppDefinition(app_id="http.invalid.app", display_name=" ", scope="")

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
                    "args": [str(_launch_script_path())],
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
                    "args": [str(_launch_probe_script_path()), str(ready_file)],
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
