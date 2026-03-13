from __future__ import annotations

import pytest

from devhub_sdk._parsing import parse_app_definition, parse_app_instance, parse_invocation, parse_launch_result


def test_parse_app_definition_when_launch_missing_exe_path_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_app_definition(
            {
                "appId": "test.app",
                "displayName": "Test App",
                "launch": {},
            },
            path="app.definition",
        )


def test_parse_launch_result_when_pid_is_bool_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_launch_result(
            {
                "ok": True,
                "status": "started",
                "launchId": "launch-1",
                "pid": True,
            },
            path="hub.apps.launch.result",
        )


def test_parse_app_instance_when_pid_is_not_positive_should_raise() -> None:
    payload = _app_instance_payload()
    payload["pid"] = 0

    with pytest.raises(RuntimeError):
        parse_app_instance(payload, path="hub.apps.listInstances.result.instances[0]")


@pytest.mark.parametrize(
    ("field_name", "value"),
    [
        ("ttlMs", 999),
        ("waitTimeoutMs", 0),
    ],
)
def test_parse_invocation_when_option_is_out_of_range_should_raise(field_name: str, value: int) -> None:
    payload = _invocation_payload()
    payload["options"][field_name] = value

    with pytest.raises(RuntimeError):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


@pytest.mark.parametrize(
    ("field_name", "value"),
    [
        ("leaseSeconds", 0),
        ("attempt", 0),
    ],
)
def test_parse_invocation_when_delivery_is_not_positive_should_raise(field_name: str, value: int) -> None:
    payload = _invocation_payload()
    payload["delivery"][field_name] = value

    with pytest.raises(RuntimeError):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


def _app_instance_payload() -> dict[str, object]:
    return {
        "instanceId": "inst-1",
        "appId": "test.app",
        "scope": None,
        "pid": 12345,
        "registeredAtUtc": "2026-03-09T00:00:00Z",
        "lastSeenUtc": "2026-03-09T00:00:01Z",
        "invoke": {
            "poll": True,
            "respond": True,
        },
    }


def _invocation_payload() -> dict[str, object]:
    return {
        "invocationId": "invk-1",
        "appId": "test.app",
        "target": {
            "scope": None,
            "instanceId": "inst-1",
        },
        "method": "test.method",
        "kind": "request",
        "createdAtUtc": "2026-03-09T00:00:00Z",
        "options": {
            "ttlMs": 1000,
            "waitTimeoutMs": 1,
            "queueIfOffline": True,
            "autoLaunch": False,
        },
        "delivery": {
            "leaseSeconds": 30,
            "attempt": 1,
        },
        "caller": {
            "clientId": "client-1",
            "clientSessionId": "11111111-1111-1111-1111-111111111111",
        },
    }
