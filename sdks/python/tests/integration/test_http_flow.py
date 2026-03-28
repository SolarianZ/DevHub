from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

from devhub_sdk import AppInstanceRegistration, InvokeCapability, ListInstancesRequest
from devhub_sdk.models import LaunchRequest

from ._host import DevHubHostFixture


def test_M5_E2E_001_And_002_ping_and_apps_flow_should_succeed() -> None:
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


def test_M5_E2E_002_launch_should_round_trip_and_apply_dedupe_window() -> None:
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
    return Path(__file__).resolve().parents[4] / "host" / "tests" / "assets" / "launch_noop.py"


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
