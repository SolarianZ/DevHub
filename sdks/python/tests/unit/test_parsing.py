from __future__ import annotations

import pytest

from devhub_sdk import DevHubEventType
from devhub_sdk._parsing import (
    parse_app_definition,
    parse_app_instance,
    parse_callee_error,
    parse_datetime,
    parse_definition_validation_result,
    parse_event,
    parse_invocation,
    parse_launch_result,
    parse_notify_result,
    parse_ping_result,
    parse_request_result,
)


def test_M5_PY_UT_005_parse_app_definition_when_launch_missing_exe_path_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_app_definition(
            {
                "appId": "test.app",
                "displayName": "Test App",
                "launch": {},
            },
            path="app.definition",
        )


def test_M5_PY_UT_005_parse_app_definition_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_app_definition(
            {
                "appId": "Test.App",
                "displayName": "Test App",
            },
            path="app.definition",
        )


def test_M5_PY_UT_005_parse_app_definition_when_capabilities_missing_should_apply_rpc_default() -> None:
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


def test_M5_PY_UT_005_parse_app_definition_when_capabilities_rpc_missing_should_apply_rpc_default() -> None:
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


def test_M5_PY_UT_005_parse_app_definition_when_launch_exe_path_empty_should_allow_spec_value() -> None:
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


def test_M6_PY_UT_005_parse_definition_validation_result_should_round_trip_issues() -> None:
    result = parse_definition_validation_result(
        {
            "ok": True,
            "valid": False,
            "errors": [
                {
                    "path": "definition.appId",
                    "code": "invalid_app_id",
                    "message": "appId must match ^[a-z0-9][a-z0-9.-]*$",
                }
            ],
        },
        path="hub.apps.validateDefinition.result",
    )

    assert result.ok is True
    assert result.valid is False
    assert result.errors[0].path == "definition.appId"
    assert result.errors[0].code == "invalid_app_id"


def test_M6_PY_UT_005_parse_definition_validation_result_when_valid_contains_errors_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"errors"):
        parse_definition_validation_result(
            {
                "ok": True,
                "valid": True,
                "errors": [
                    {
                        "path": "definition.appId",
                        "code": "invalid_app_id",
                        "message": "invalid",
                    }
                ],
            },
            path="hub.apps.validateDefinition.result",
        )


@pytest.mark.parametrize(
    ("mutator",),
    [
        (lambda payload: payload.__setitem__("description", None),),
        (lambda payload: payload.__setitem__("capabilities", None),),
        (lambda payload: payload["capabilities"].__setitem__("rpc", None),),
        (lambda payload: payload["capabilities"].__setitem__("events", None),),
        (lambda payload: payload.__setitem__("launch", None),),
        (lambda payload: payload["launch"].__setitem__("argsTemplate", None),),
    ],
)
def test_M5_PY_UT_005_parse_app_definition_when_optional_non_nullable_field_is_null_should_raise(mutator) -> None:
    payload = {
        "appId": "test.app",
        "displayName": "Test App",
        "capabilities": {
            "rpc": True,
            "events": False,
        },
        "launch": {
            "exePath": "app.exe",
            "argsTemplate": "--scope {scope}",
        },
    }
    mutator(payload)

    with pytest.raises(RuntimeError):
        parse_app_definition(payload, path="app.definition")


def test_M5_PY_UT_005_parse_launch_result_when_pid_is_bool_should_raise() -> None:
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


def test_M5_PY_UT_005_parse_launch_result_when_pid_is_not_positive_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_launch_result(
            {
                "ok": True,
                "status": "started",
                "launchId": "launch-1",
                "pid": 0,
            },
            path="hub.apps.launch.result",
        )


def test_M5_PY_UT_005_parse_app_instance_when_pid_is_not_positive_should_raise() -> None:
    payload = _app_instance_payload()
    payload["pid"] = 0

    with pytest.raises(RuntimeError, match=r"pid 必须为正整数。"):
        parse_app_instance(payload, path="hub.apps.listInstances.result.instances[0]")


def test_M5_PY_UT_005_parse_app_instance_when_meta_is_null_should_raise() -> None:
    payload = _app_instance_payload()
    payload["meta"] = None

    with pytest.raises(RuntimeError):
        parse_app_instance(payload, path="hub.apps.listInstances.result.instances[0]")


def test_M5_PY_UT_005_parse_app_instance_when_meta_contains_unsupported_json_should_raise() -> None:
    payload = _app_instance_payload()
    payload["meta"] = {"callback": lambda: "ignored"}

    with pytest.raises(RuntimeError, match=r"meta\.callback 包含不支持的 JSON 类型。"):
        parse_app_instance(payload, path="hub.apps.listInstances.result.instances[0]")


def test_M6_PY_UT_005_parse_app_instance_when_password_present_should_raise() -> None:
    payload = _app_instance_payload()
    payload["password"] = "secret-1"

    with pytest.raises(RuntimeError, match=r"password"):
        parse_app_instance(payload, path="hub.apps.registerInstance.result.instance")


@pytest.mark.parametrize(
    "value",
    [
        "2026-03-09 00:00:00Z",
        "2026-03-09T08:00:00+08:00",
    ],
)
def test_M5_PY_UT_005_parse_datetime_when_value_is_not_rfc3339_utc_should_raise(value: str) -> None:
    with pytest.raises(RuntimeError):
        parse_datetime(value, "timeUtc")


@pytest.mark.parametrize(
    ("field_name", "value"),
    [
        ("ttlMs", 999),
        ("waitTimeoutMs", 0),
    ],
)
def test_M5_PY_UT_005_parse_invocation_when_option_is_out_of_range_should_raise(field_name: str, value: int) -> None:
    payload = _invocation_payload()
    payload["options"][field_name] = value

    with pytest.raises(RuntimeError):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


def test_M5_PY_UT_005_parse_invocation_when_wait_timeout_exceeds_ttl_should_raise() -> None:
    payload = _invocation_payload()
    payload["options"]["ttlMs"] = 1000
    payload["options"]["waitTimeoutMs"] = 1001

    with pytest.raises(RuntimeError, match=r"waitTimeoutMs"):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


@pytest.mark.parametrize(
    ("field_name", "value"),
    [
        ("leaseSeconds", 0),
        ("attempt", 0),
    ],
)
def test_M5_PY_UT_005_parse_invocation_when_delivery_is_not_positive_should_raise(field_name: str, value: int) -> None:
    payload = _invocation_payload()
    payload["delivery"][field_name] = value

    with pytest.raises(RuntimeError, match=r"必须为正整数。"):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


def test_M5_PY_UT_005_parse_notify_result_when_invocation_id_violates_spec_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_notify_result(
            {
                "ok": True,
                "invocationId": "request-1",
            },
            path="hub.invoke.notify.result",
        )


def test_M5_PY_UT_005_parse_ping_result_when_echo_contains_unsupported_json_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"echo\.callback 包含不支持的 JSON 类型。"):
        parse_ping_result(
            {
                "ok": True,
                "serverTimeUtc": "2026-03-09T00:00:00Z",
                "echo": {"callback": lambda: "ignored"},
            },
            path="hub.ping.result",
        )


def test_M5_PY_UT_005_parse_request_result_when_value_contains_unsupported_json_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"value\.callback 包含不支持的 JSON 类型。"):
        parse_request_result(
            {
                "ok": True,
                "invocationId": "invk-1",
                "value": {"callback": lambda: "ignored"},
            },
            path="hub.invoke.request.result",
        )


def test_M5_PY_UT_005_parse_event_when_type_is_not_supported_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_event(
            {
                "subscriptionId": "sub-1",
                "type": "future.event",
                "timeUtc": "2026-03-09T00:00:00Z",
            },
            path="hub.event.params",
        )


def test_M5_PY_UT_005_parse_event_when_payload_contains_unsupported_json_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"payload\.callback 包含不支持的 JSON 类型。"):
        parse_event(
            {
                "subscriptionId": "sub-1",
                "type": "invocation.completed",
                "timeUtc": "2026-03-09T00:00:00Z",
                "payload": {"callback": lambda: "ignored"},
            },
            path="hub.event.params",
        )


def test_M5_PY_UT_005_parse_event_should_return_supported_event_type() -> None:
    event = parse_event(
        {
            "subscriptionId": "sub-1",
            "type": "invocation.completed",
            "timeUtc": "2026-03-09T00:00:00Z",
            "payload": {"invocationId": "invk-1"},
        },
        path="hub.event.params",
    )

    assert event.type is DevHubEventType.INVOCATION_COMPLETED


def test_M6_PY_UT_005_parse_event_should_accept_definition_lifecycle_type() -> None:
    event = parse_event(
        {
            "subscriptionId": "sub-1",
            "type": "app.definition.upserted",
            "timeUtc": "2026-03-09T00:00:00Z",
            "payload": {
                "appId": "test.app",
                "definition": {
                    "appId": "test.app",
                    "displayName": "Test App",
                },
            },
        },
        path="hub.event.params",
    )

    assert event.type is DevHubEventType.APP_DEFINITION_UPSERTED


def test_M6_PY_UT_005_parse_event_when_definition_payload_missing_required_shape_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"definition"):
        parse_event(
            {
                "subscriptionId": "sub-1",
                "type": "app.definition.upserted",
                "timeUtc": "2026-03-09T00:00:00Z",
                "payload": {
                    "appId": "test.app",
                },
            },
            path="hub.event.params",
        )


def test_M6_PY_UT_005_parse_event_when_instance_payload_contains_password_should_raise() -> None:
    with pytest.raises(RuntimeError, match=r"password"):
        parse_event(
            {
                "subscriptionId": "sub-1",
                "type": "app.instance.registered",
                "timeUtc": "2026-03-09T00:00:00Z",
                "payload": {
                    "appId": "test.app",
                    "instanceId": "inst-1",
                    "password": "secret-1",
                },
            },
            path="hub.event.params",
        )


@pytest.mark.parametrize(
    ("mutator",),
    [
        (lambda payload: payload.__setitem__("invocationId", "request-1"),),
        (lambda payload: payload["target"].__setitem__("instanceId", "inst/1"),),
        (lambda payload: payload["caller"].__setitem__("clientSessionId", "not-a-uuid"),),
    ],
)
def test_M5_PY_UT_005_parse_invocation_when_identifier_violates_spec_should_raise(mutator) -> None:
    payload = _invocation_payload()
    mutator(payload)

    with pytest.raises(RuntimeError):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


@pytest.mark.parametrize(
    ("mutator",),
    [
        (lambda payload: payload.__setitem__("options", None),),
        (lambda payload: payload["options"].__setitem__("ttlMs", None),),
        (lambda payload: payload["options"].__setitem__("queueIfOffline", None),),
        (lambda payload: payload.__setitem__("delivery", None),),
    ],
)
def test_M5_PY_UT_005_parse_invocation_when_optional_non_nullable_field_is_null_should_raise(mutator) -> None:
    payload = _invocation_payload()
    mutator(payload)

    with pytest.raises(RuntimeError):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


def test_M5_PY_UT_005_parse_invocation_when_args_contains_unsupported_json_should_raise() -> None:
    payload = _invocation_payload()
    payload["args"] = {"callback": lambda: "ignored"}

    with pytest.raises(RuntimeError, match=r"args\.callback 包含不支持的 JSON 类型。"):
        parse_invocation(payload, path="hub.invoke.poll.result.items[0]")


@pytest.mark.parametrize("data", [None, {"callback": lambda: "ignored"}])
def test_M5_PY_UT_005_parse_callee_error_when_data_is_not_valid_json_object_should_raise(data) -> None:
    with pytest.raises(RuntimeError):
        parse_callee_error(
            {
                "code": 1001,
                "message": "app_error",
                "data": data,
            },
            path="error.data.calleeError",
        )


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
