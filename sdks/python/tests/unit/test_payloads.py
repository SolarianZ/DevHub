from __future__ import annotations

import pytest

from devhub_sdk import (
    AppCapabilities,
    AppDefinition,
    DevHubCalleeError,
    InvokeCapability,
    InvocationOptions,
    InvocationTarget,
    LaunchConfiguration,
)
from devhub_sdk._payloads import (
    build_delete_definition_params,
    build_notify_params,
    build_poll_params,
    build_register_instance_params,
    build_request_params,
    build_respond_params,
    build_unregister_params,
    build_upsert_definition_params,
    build_validate_definition_params,
)
from devhub_sdk.models import AppInstanceRegistration, InvokeRequest, PollRequest, RespondRequest


def test_M5_PY_UT_004_notify_builder_should_apply_default_options() -> None:
    payload = build_notify_params(InvokeRequest(app_id="test.app", method="test.notify"))

    assert payload["options"]["ttlMs"] == 60000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True
    assert "args" not in payload
    assert "target" not in payload


def test_M5_PY_UT_004_request_builder_should_apply_default_options() -> None:
    payload = build_request_params(InvokeRequest(app_id="test.app", method="test.request"))

    assert payload["options"]["ttlMs"] == 300000
    assert payload["options"]["waitTimeoutMs"] == 120000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True
    assert "args" not in payload


def test_M5_PY_UT_004_notify_builder_when_wait_timeout_specified_should_raise() -> None:
    with pytest.raises(ValueError, match="wait_timeout_ms"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                options=InvocationOptions(wait_timeout_ms=1000),
            )
        )


def test_M5_PY_UT_004_notify_builder_should_preserve_explicit_null_args() -> None:
    payload = build_notify_params(InvokeRequest(app_id="test.app", method="test.notify", args=None))

    assert "args" in payload
    assert payload["args"] is None


def test_M5_PY_UT_004_request_builder_should_preserve_explicit_empty_scope() -> None:
    payload = build_notify_params(
        InvokeRequest(
            app_id="test.app",
            method="test.notify",
            target=InvocationTarget(scope="", instance_id=None),
        )
    )

    assert payload["target"]["scope"] == ""


def test_M5_PY_UT_004_request_builder_when_auto_launch_enabled_with_instance_id_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(instance_id="inst-1"),
                options=InvocationOptions(auto_launch=True),
            )
        )


def test_M5_PY_UT_004_request_builder_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_request_params(InvokeRequest(app_id="Test.App", method="test.request"))


def test_M5_PY_UT_004_notify_builder_when_target_instance_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(instance_id="inst/1"),
            )
        )


def test_M5_PY_UT_004_request_builder_when_auto_launch_requires_queue_if_offline_true_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                options=InvocationOptions(auto_launch=True, queue_if_offline=False),
            )
        )


def test_M5_PY_UT_004_poll_builder_should_apply_defaults() -> None:
    payload = build_poll_params(PollRequest(instance_id="inst-1"))

    assert payload["maxCount"] == 10
    assert payload["waitMs"] == 25000


def test_M6_PY_UT_004_definition_builder_should_preserve_supported_fields() -> None:
    payload = build_upsert_definition_params(
        AppDefinition(
            app_id="test.app",
            display_name="Test App",
            description="用于测试。",
            capabilities=AppCapabilities(rpc=False, events=True),
            launch=LaunchConfiguration(
                exe_path="python",
                args_template="-m app",
                working_directory="/tmp",
                dedupe_key_template="test.app",
            ),
        )
    )

    assert payload == {
        "definition": {
            "appId": "test.app",
            "displayName": "Test App",
            "description": "用于测试。",
            "capabilities": {
                "rpc": False,
                "events": True,
            },
            "launch": {
                "exePath": "python",
                "argsTemplate": "-m app",
                "workingDirectory": "/tmp",
                "dedupeKeyTemplate": "test.app",
            },
        }
    }


def test_M6_PY_UT_004_validate_definition_builder_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_validate_definition_params(AppDefinition(app_id="Test.App", display_name="Broken"))


def test_M6_PY_UT_004_delete_definition_builder_should_validate_app_id() -> None:
    assert build_delete_definition_params("test.app") == {"appId": "test.app"}

    with pytest.raises(ValueError):
        build_delete_definition_params("Test.App")


def test_M6_PY_UT_004_register_instance_builder_should_place_password_at_top_level() -> None:
    payload = build_register_instance_params(
        AppInstanceRegistration(
            instance_id="inst-1",
            app_id="test.app",
            pid=1234,
            invoke=InvokeCapability(poll=True, respond=True),
        ),
        "secret-1",
    )

    assert payload["password"] == "secret-1"
    assert payload["instance"]["instanceId"] == "inst-1"
    assert "password" not in payload["instance"]


def test_M5_PY_UT_004_register_instance_builder_when_meta_is_not_object_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                meta=[1, 2, 3],
            ),
            "secret-1",
        )


def test_M5_PY_UT_004_register_instance_builder_when_meta_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"meta\.value 必须为有限数字。"):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                meta={"value": float("nan")},
            ),
            "secret-1",
        )


def test_M5_PY_UT_004_register_instance_builder_when_invoke_poll_is_not_bool_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll="true", respond=True),  # type: ignore[arg-type]
            ),
            "secret-1",
        )


def test_M5_PY_UT_004_register_instance_builder_when_instance_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst/1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
            ),
            "secret-1",
        )


def test_M6_PY_UT_004_unregister_builder_when_password_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_unregister_params("inst-1", "  ")


def test_M5_PY_UT_004_respond_builder_when_error_message_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                error=DevHubCalleeError(code=1001, message=""),
            )
        )


def test_M5_PY_UT_004_notify_builder_when_target_instance_id_is_not_string_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(instance_id=123),  # type: ignore[arg-type]
            )
        )


def test_M5_PY_UT_004_notify_builder_when_args_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"args\.value 必须为有限数字。"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                args={"value": float("nan")},
            )
        )


def test_M5_PY_UT_004_poll_builder_when_wait_ms_is_not_integer_should_raise() -> None:
    with pytest.raises(ValueError):
        build_poll_params(
            PollRequest(
                instance_id="inst-1",
                wait_ms=1.5,  # type: ignore[arg-type]
            )
        )


def test_M5_PY_UT_004_respond_builder_should_allow_null_value() -> None:
    payload = build_respond_params(
        RespondRequest(
            instance_id="inst-1",
            invocation_id="invk-1",
            value=None,
        )
    )

    assert payload["value"] is None
    assert "error" not in payload


def test_M5_PY_UT_004_respond_builder_when_value_and_error_both_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
            )
        )


def test_M5_PY_UT_004_respond_builder_when_invocation_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="request-1",
                value={"ok": True},
            )
        )


def test_M5_PY_UT_004_respond_builder_when_value_and_error_present_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                value={"ok": True},
                error=DevHubCalleeError(code=1001, message="app_error"),
            )
        )


def test_M5_PY_UT_004_respond_builder_when_value_contains_unsupported_json_type_should_raise() -> None:
    with pytest.raises(ValueError, match=r"value\.callback 包含不支持的 JSON 类型。"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                value={"callback": lambda: "ignored"},
            )
        )


def test_M5_PY_UT_004_respond_builder_when_error_data_is_not_json_object_should_raise() -> None:
    with pytest.raises(ValueError, match=r"error\.data\.callback 包含不支持的 JSON 类型。"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                error=DevHubCalleeError(
                    code=1001,
                    message="app_error",
                    data={"callback": lambda: "ignored"},
                ),
            )
        )
