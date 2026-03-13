from __future__ import annotations

import pytest

from devhub_sdk._parsing import parse_app_definition, parse_app_instance, parse_invocation, parse_launch_result, parse_notify_result


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


def test_parse_app_definition_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_app_definition(
            {
                "appId": "Test.App",
                "displayName": "Test App",
            },
            path="app.definition",
        )


def test_parse_app_definition_when_capabilities_missing_should_apply_rpc_default() -> None:
    definition = parse_app_definition(
        {
            "appId": "test.app",
            "displayName": "Test App",
        },
        path="app.definition",
    )

    assert definition.capabilities is not None
    assert definition.capabilities.rpc is True
    assert definition.capabilities.events is None


def test_parse_app_definition_when_capabilities_rpc_missing_should_apply_rpc_default() -> None:
    definition = parse_app_definition(
        {
            "appId": "test.app",
            "displayName": "Test App",
            "capabilities": {
                "events": False,
            },
        },
        path="app.definition",
    )

    assert definition.capabilities is not None
    assert definition.capabilities.rpc is True
    assert definition.capabilities.events is False


def test_parse_app_definition_when_launch_exe_path_empty_should_allow_spec_value() -> None:
    definition = parse_app_definition(
        {
            "appId": "test.app",
            "displayName": "Test App",
            "launch": {
                "exePath": "",
            },
        },
        path="app.definition",
    )

    assert definition.launch is not None
    assert definition.launch.exe_path == ""


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


def test_parse_notify_result_when_invocation_id_violates_spec_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_notify_result(
            {
                "ok": True,
                "invocationId": "request-1",
            },
            path="hub.invoke.notify.result",
        )


@pytest.mark.parametrize(
    ("mutator",),
    [
        (lambda payload: payload.__setitem__("invocationId", "request-1"),),
        (lambda payload: payload["target"].__setitem__("instanceId", "inst/1"),),
        (lambda payload: payload["caller"].__setitem__("clientSessionId", "not-a-uuid"),),
    ],
)
def test_parse_invocation_when_identifier_violates_spec_should_raise(mutator) -> None:
    payload = _invocation_payload()
    mutator(payload)

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
