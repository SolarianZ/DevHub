from __future__ import annotations

import pytest

from devhub_sdk import DevHubCalleeError, InvokeCapability, InvocationOptions, InvocationTarget
from devhub_sdk._payloads import (
    build_notify_params,
    build_poll_params,
    build_register_instance_params,
    build_request_params,
    build_respond_params,
)
from devhub_sdk.models import AppInstanceRegistration, InvokeRequest, PollRequest, RespondRequest


def test_notify_builder_should_apply_default_options() -> None:
    payload = build_notify_params(InvokeRequest(app_id="test.app", method="test.notify"))

    assert payload["options"]["ttlMs"] == 60000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True
    assert "target" not in payload


def test_request_builder_should_apply_default_options() -> None:
    payload = build_request_params(InvokeRequest(app_id="test.app", method="test.request"))

    assert payload["options"]["ttlMs"] == 300000
    assert payload["options"]["waitTimeoutMs"] == 120000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True


def test_request_builder_should_preserve_explicit_empty_scope() -> None:
    payload = build_notify_params(
        InvokeRequest(
            app_id="test.app",
            method="test.notify",
            target=InvocationTarget(scope="", instance_id=None),
        )
    )

    assert payload["target"]["scope"] == ""


def test_request_builder_when_auto_launch_enabled_with_instance_id_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(instance_id="inst-1"),
                options=InvocationOptions(auto_launch=True),
            )
        )


def test_request_builder_when_auto_launch_requires_queue_if_offline_true_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                options=InvocationOptions(auto_launch=True, queue_if_offline=False),
            )
        )


def test_poll_builder_should_apply_defaults() -> None:
    payload = build_poll_params(PollRequest(instance_id="inst-1"))

    assert payload["maxCount"] == 10
    assert payload["waitMs"] == 25000


def test_register_instance_builder_when_meta_is_not_object_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                meta=[1, 2, 3],
            )
        )


def test_register_instance_builder_when_meta_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"meta\.value 必须为有限数字。"):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                meta={"value": float("nan")},
            )
        )


def test_register_instance_builder_when_invoke_poll_is_not_bool_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll="true", respond=True),  # type: ignore[arg-type]
            )
        )


def test_respond_builder_when_error_message_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                error=DevHubCalleeError(code=1001, message=""),
            )
        )


def test_notify_builder_when_target_instance_id_is_not_string_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(instance_id=123),  # type: ignore[arg-type]
            )
        )


def test_notify_builder_when_args_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"args\.value 必须为有限数字。"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                args={"value": float("nan")},
            )
        )


def test_poll_builder_when_wait_ms_is_not_integer_should_raise() -> None:
    with pytest.raises(ValueError):
        build_poll_params(
            PollRequest(
                instance_id="inst-1",
                wait_ms=1.5,  # type: ignore[arg-type]
            )
        )


def test_respond_builder_should_allow_null_value() -> None:
    payload = build_respond_params(
        RespondRequest(
            instance_id="inst-1",
            invocation_id="invk-1",
            value=None,
        )
    )

    assert payload["value"] is None
    assert "error" not in payload


def test_respond_builder_when_value_and_error_both_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
            )
        )


def test_respond_builder_when_value_and_error_present_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                value={"ok": True},
                error=DevHubCalleeError(code=1001, message="app_error"),
            )
        )


def test_respond_builder_when_value_contains_unsupported_json_type_should_raise() -> None:
    with pytest.raises(ValueError, match=r"value\.callback 包含不支持的 JSON 类型。"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                invocation_id="invk-1",
                value={"callback": lambda: "ignored"},
            )
        )


def test_respond_builder_when_error_data_is_not_json_object_should_raise() -> None:
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
