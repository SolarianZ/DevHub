from __future__ import annotations

from types import SimpleNamespace

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
    build_heartbeat_params,
    build_get_definition_params,
    build_get_instance_params,
    build_list_definitions_params,
    build_list_instances_params,
    build_ping_params,
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
from devhub_sdk.models import (
    AppInstanceRegistration,
    InvokeRequest,
    ListDefinitionsRequest,
    ListInstancesRequest,
    PollRequest,
    RespondRequest,
)


def test_notify_builder_should_apply_default_options() -> None:
    payload = build_notify_params(
        InvokeRequest(
            app_id="test.app",
            method="test.notify",
            target=InvocationTarget(scope=""),
        )
    )

    assert payload["options"]["ttlMs"] == 60000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True
    assert "args" not in payload
    assert payload["target"]["scope"] == ""


def test_ping_builder_should_preserve_explicit_null_and_omit_unset() -> None:
    assert build_ping_params() is None
    assert build_ping_params(None) == {"echo": None}


def test_ping_builder_when_echo_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match="echo.value 必须为有限数字"):
        build_ping_params({"value": float("nan")})


def test_request_builder_should_apply_default_options() -> None:
    payload = build_request_params(
        InvokeRequest(
            app_id="test.app",
            method="test.request",
            target=InvocationTarget(scope=""),
        )
    )

    assert payload["options"]["ttlMs"] == 300000
    assert payload["options"]["waitTimeoutMs"] == 120000
    assert payload["options"]["queueIfOffline"] is True
    assert payload["options"]["autoLaunch"] is True
    assert "args" not in payload


def test_request_builder_should_preserve_case_sensitive_canonical_identifiers() -> None:
    payload = build_request_params(
        InvokeRequest(
            app_id="Sample.App",
            method="test.request",
            target=InvocationTarget(scope="Workspace-A.v2", instance_id="NODE_01.alpha"),
        )
    )

    assert payload["appId"] == "Sample.App"
    assert payload["target"] == {
        "scope": "Workspace-A.v2",
        "instanceId": "NODE_01.alpha",
    }


def test_notify_builder_when_wait_timeout_specified_should_raise() -> None:
    with pytest.raises(ValueError, match="wait_timeout_ms"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope=""),
                options=InvocationOptions(wait_timeout_ms=1000),
            )
        )


def test_notify_builder_should_preserve_explicit_null_args() -> None:
    payload = build_notify_params(
        InvokeRequest(
            app_id="test.app",
            method="test.notify",
            target=InvocationTarget(scope=""),
            args=None,
        )
    )

    assert "args" in payload
    assert payload["args"] is None


def test_request_builder_should_preserve_explicit_empty_scope() -> None:
    payload = build_notify_params(
        InvokeRequest(
            app_id="test.app",
            method="test.notify",
            target=InvocationTarget(scope="", instance_id=None),
        )
    )

    assert payload["target"]["scope"] == ""


def test_request_builder_when_target_missing_should_raise() -> None:
    with pytest.raises(ValueError, match="request.target"):
        build_notify_params(InvokeRequest(app_id="test.app", method="test.notify"))


def test_request_builder_when_target_scope_is_null_should_raise() -> None:
    with pytest.raises(ValueError, match="scope"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope=None),  # type: ignore[arg-type]
            )
        )


def test_request_builder_when_auto_launch_enabled_with_instance_id_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope="", instance_id="inst-1"),
                options=InvocationOptions(auto_launch=True),
            )
        )


def test_request_builder_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_request_params(InvokeRequest(app_id="Test.App-", method="test.request", target=InvocationTarget(scope="")))


def test_notify_builder_when_target_instance_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope="", instance_id="inst-1."),
            )
        )


def test_notify_builder_when_target_scope_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError, match="scope"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope=".workspace"),
            )
        )


def test_request_builder_when_auto_launch_requires_queue_if_offline_true_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope=""),
                options=InvocationOptions(auto_launch=True, queue_if_offline=False),
            )
        )


def test_poll_builder_should_apply_defaults() -> None:
    payload = build_poll_params(PollRequest(instance_id="inst-1", instance_session_token="token-1"))

    assert payload["instanceSessionToken"] == "token-1"
    assert payload["maxCount"] == 10
    assert payload["waitMs"] == 25000


def test_definition_builder_should_preserve_supported_fields() -> None:
    payload = build_upsert_definition_params(
        AppDefinition(
            app_id="Sample.App",
            display_name="Test App",
            scope="Workspace-A.v2",
            description="用于测试。",
            capabilities=AppCapabilities(rpc=False, events=True),
            launch=LaunchConfiguration(
                exe_path="python",
                args=["-m", "app", "--scope", "{scope}"],
                args_template="-m app",
                working_directory="/tmp",
                dedupe_key_template="Sample.App",
            ),
        )
    )

    assert payload == {
        "definition": {
            "appId": "Sample.App",
            "scope": "Workspace-A.v2",
            "displayName": "Test App",
            "description": "用于测试。",
            "capabilities": {
                "rpc": False,
                "events": True,
            },
            "launch": {
                "exePath": "python",
                "args": ["-m", "app", "--scope", "{scope}"],
                "argsTemplate": "-m app",
                "workingDirectory": "/tmp",
                "dedupeKeyTemplate": "Sample.App",
            },
        }
    }


def test_definition_builder_should_allow_missing_launch_and_missing_exe_path() -> None:
    assert build_upsert_definition_params(
        AppDefinition(
            app_id="test.app",
            display_name="Test App",
            scope="",
        )
    ) == {
        "definition": {
            "appId": "test.app",
            "scope": "",
            "displayName": "Test App",
        }
    }

    assert build_upsert_definition_params(
        AppDefinition(
            app_id="test.app",
            display_name="Test App",
            scope="",
            launch=LaunchConfiguration(args=["--workspace", "{scope}"]),
        )
    ) == {
        "definition": {
            "appId": "test.app",
            "scope": "",
            "displayName": "Test App",
            "launch": {
                "args": ["--workspace", "{scope}"],
            },
        }
    }


def test_definition_builder_when_display_name_is_blank_should_raise() -> None:
    with pytest.raises(ValueError, match="definition.display_name"):
        build_upsert_definition_params(
            SimpleNamespace(
                app_id="test.app",
                display_name=" ",
                scope="",
                description=None,
                capabilities=None,
                launch=None,
            )
        )


def test_definition_builder_should_allow_empty_launch_exe_path() -> None:
    assert build_upsert_definition_params(
        AppDefinition(
            app_id="test.app",
            display_name="Test App",
            scope="",
            launch=LaunchConfiguration(exe_path=""),
        )
    ) == {
        "definition": {
            "appId": "test.app",
            "scope": "",
            "displayName": "Test App",
            "launch": {
                "exePath": "",
            },
        }
    }


@pytest.mark.parametrize("args", ["--flag", [1], ["ok", 1]])
def test_definition_builder_when_launch_args_is_not_string_list_should_raise(args: object) -> None:
    with pytest.raises(ValueError, match="args"):
        build_upsert_definition_params(
            AppDefinition(
                app_id="test.app",
                display_name="Test App",
                scope="",
                launch=LaunchConfiguration(args=args),  # type: ignore[arg-type]
            )
        )


def test_validate_definition_builder_when_app_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_validate_definition_params(AppDefinition(app_id=".Broken.App", display_name="Broken", scope=""))


def test_validate_definition_builder_when_scope_is_blank_should_raise() -> None:
    with pytest.raises(ValueError, match="scope"):
        build_validate_definition_params(AppDefinition(app_id="test.app", display_name="Broken", scope=" "))


def test_app_definition_should_preserve_existing_positional_description() -> None:
    definition = AppDefinition("demo.app", "Demo App", "Description", scope="")

    assert definition.description == "Description"
    assert definition.scope == ""


def test_app_definition_should_preserve_keyword_scope() -> None:
    definition = AppDefinition("Sample.App", "Demo App", "Description", scope="Workspace-A.v2")

    assert definition.description == "Description"
    assert definition.app_id == "Sample.App"
    assert definition.scope == "Workspace-A.v2"


def test_delete_definition_builder_should_validate_app_id() -> None:
    assert build_delete_definition_params("test.app", "") == {"appId": "test.app", "scope": ""}
    assert build_delete_definition_params("test.app", "workspace-a") == {
        "appId": "test.app",
        "scope": "workspace-a",
    }

    with pytest.raises(ValueError):
        build_delete_definition_params(".Test.App", "")

    with pytest.raises(ValueError, match="scope"):
        build_delete_definition_params("test.app", None)  # type: ignore[arg-type]


def test_get_definition_builder_should_validate_app_id() -> None:
    assert build_get_definition_params("test.app", "") == {"appId": "test.app", "scope": ""}
    assert build_get_definition_params("Sample.App", "Workspace-A.v2") == {
        "appId": "Sample.App",
        "scope": "Workspace-A.v2",
    }
    assert build_get_definition_params("test.app", "workspace-a") == {
        "appId": "test.app",
        "scope": "workspace-a",
    }

    with pytest.raises(ValueError):
        build_get_definition_params("Test.App-", "")

    with pytest.raises(ValueError, match="scope"):
        build_get_definition_params("test.app", None)  # type: ignore[arg-type]


def test_get_instance_builder_should_validate_instance_id() -> None:
    assert build_get_instance_params("inst-1") == {"instanceId": "inst-1"}
    assert build_get_instance_params("NODE_01.alpha") == {"instanceId": "NODE_01.alpha"}

    with pytest.raises(ValueError, match="instance_id"):
        build_get_instance_params(".inst-1")

    with pytest.raises(ValueError, match="instance_id"):
        build_get_instance_params(None)  # type: ignore[arg-type]


def test_get_instance_builder_when_instance_id_exceeds_limit_should_raise() -> None:
    with pytest.raises(ValueError, match="instance_id"):
        build_get_instance_params("a" * 257)


def test_list_definitions_builder_should_include_explicit_scope_filter() -> None:
    assert build_list_definitions_params(ListDefinitionsRequest(scope=None)) == {"scope": None}
    assert build_list_definitions_params(ListDefinitionsRequest(scope="", app_id="test.app")) == {
        "appId": "test.app",
        "scope": "",
    }


def test_list_instances_builder_should_share_filter_validation_rules() -> None:
    assert build_list_instances_params(ListInstancesRequest(scope=None, app_id="test.app", include_offline=True)) == {
        "appId": "test.app",
        "scope": None,
        "includeOffline": True,
    }
    assert build_list_instances_params(ListInstancesRequest(scope="", app_id="test.app")) == {
        "appId": "test.app",
        "scope": "",
    }

    with pytest.raises(ValueError):
        build_list_instances_params(ListInstancesRequest(scope=None, app_id=".Test.App"))


def test_list_instances_request_should_not_accept_include_all_scopes() -> None:
    with pytest.raises(TypeError):
        ListInstancesRequest(scope=None, include_all_scopes=True)  # type: ignore[call-arg]


def test_register_instance_builder_should_place_password_at_top_level() -> None:
    payload = build_register_instance_params(
        AppInstanceRegistration(
            instance_id="inst-1",
            app_id="test.app",
            pid=1234,
            invoke=InvokeCapability(poll=True, respond=True),
            scope="",
        ),
        "secret-1",
    )

    assert payload["password"] == "secret-1"
    assert payload["instance"]["instanceId"] == "inst-1"
    assert "password" not in payload["instance"]
    assert "instanceSessionToken" not in payload["instance"]


def test_register_instance_builder_should_place_launch_id_at_top_level() -> None:
    payload = build_register_instance_params(
        AppInstanceRegistration(
            instance_id="inst-1",
            app_id="test.app",
            pid=1234,
            invoke=InvokeCapability(poll=True, respond=True),
            scope="",
            meta={"launchId": "business-meta-value"},
        ),
        "secret-1",
        launch_id="launch-1",
    )

    assert payload["launchId"] == "launch-1"
    assert payload["instance"]["meta"] == {"launchId": "business-meta-value"}


@pytest.mark.parametrize("launch_id", ["", "   ", 123])
def test_register_instance_builder_when_launch_id_invalid_should_raise(launch_id: object) -> None:
    with pytest.raises(ValueError, match=r"launch_id"):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            "secret-1",
            launch_id=launch_id,  # type: ignore[arg-type]
        )


def test_register_instance_builder_when_instance_payload_carries_password_should_raise() -> None:
    with pytest.raises(ValueError, match=r"instance\.password"):
        build_register_instance_params(
            SimpleNamespace(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
                meta=None,
                password="secret-1",
            ),
            "secret-1",
        )


def test_register_instance_builder_when_instance_payload_carries_instance_session_token_should_raise() -> None:
    with pytest.raises(ValueError, match=r"instance\.instance_session_token"):
        build_register_instance_params(
            SimpleNamespace(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
                meta=None,
                instance_session_token="token-1",
            ),
            "secret-1",
        )


def test_register_instance_builder_when_meta_is_not_object_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
                meta=[1, 2, 3],
            ),
            "secret-1",
        )


def test_register_instance_builder_when_meta_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"meta\.value 必须为有限数字。"):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
                meta={"value": float("nan")},
            ),
            "secret-1",
        )


def test_register_instance_builder_when_invoke_poll_is_not_bool_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst-1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll="true", respond=True),  # type: ignore[arg-type]
                scope="",
            ),
            "secret-1",
        )


def test_register_instance_builder_when_instance_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="inst:1",
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            "secret-1",
        )


def test_register_instance_builder_when_instance_id_exceeds_limit_should_raise() -> None:
    with pytest.raises(ValueError):
        build_register_instance_params(
            AppInstanceRegistration(
                instance_id="a" * 257,
                app_id="test.app",
                pid=1234,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            "secret-1",
        )


def test_heartbeat_builder_should_include_instance_session_token() -> None:
    assert build_heartbeat_params("inst-1", "token-1") == {
        "instanceId": "inst-1",
        "instanceSessionToken": "token-1",
    }


def test_unregister_builder_when_instance_session_token_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_unregister_params("inst-1", "  ")


def test_unregister_builder_should_include_instance_session_token() -> None:
    assert build_unregister_params("inst-1", "token-1") == {
        "instanceId": "inst-1",
        "instanceSessionToken": "token-1",
    }


def test_respond_builder_when_error_message_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="lease-1",
                error=DevHubCalleeError(code=1001, message=""),
            )
        )


def test_notify_builder_when_target_instance_id_is_not_string_should_raise() -> None:
    with pytest.raises(ValueError):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope="", instance_id=123),  # type: ignore[arg-type]
            )
        )


@pytest.mark.parametrize("builder", [build_notify_params, build_request_params])
def test_invoke_builder_when_target_instance_id_exceeds_limit_should_raise(builder) -> None:
    with pytest.raises(ValueError, match="instance_id"):
        builder(
            InvokeRequest(
                app_id="test.app",
                method="test.invoke",
                target=InvocationTarget(scope="", instance_id="a" * 257),
            )
        )


def test_notify_builder_when_args_contains_non_finite_number_should_raise() -> None:
    with pytest.raises(ValueError, match=r"args\.value 必须为有限数字。"):
        build_notify_params(
            InvokeRequest(
                app_id="test.app",
                method="test.notify",
                target=InvocationTarget(scope=""),
                args={"value": float("nan")},
            )
        )


def test_poll_builder_when_wait_ms_is_not_integer_should_raise() -> None:
    with pytest.raises(ValueError):
        build_poll_params(
            PollRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                wait_ms=1.5,  # type: ignore[arg-type]
            )
        )


def test_respond_builder_should_allow_null_value() -> None:
    payload = build_respond_params(
        RespondRequest(
            instance_id="inst-1",
            instance_session_token="token-1",
            invocation_id="invk-1",
            lease_token="lease-1",
            value=None,
        )
    )

    assert payload["instanceSessionToken"] == "token-1"
    assert payload["leaseToken"] == "lease-1"
    assert payload["value"] is None
    assert "error" not in payload


def test_respond_builder_when_lease_token_missing_should_raise() -> None:
    with pytest.raises(ValueError, match="lease_token"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="",
                value={"ok": True},
            )
        )


def test_respond_builder_when_value_and_error_both_missing_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="lease-1",
            )
        )


def test_respond_builder_when_invocation_id_violates_spec_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="request-1",
                lease_token="lease-1",
                value={"ok": True},
            )
        )


def test_respond_builder_when_value_and_error_present_should_raise() -> None:
    with pytest.raises(ValueError):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="lease-1",
                value={"ok": True},
                error=DevHubCalleeError(code=1001, message="app_error"),
            )
        )


def test_respond_builder_when_value_contains_unsupported_json_type_should_raise() -> None:
    with pytest.raises(ValueError, match=r"value\.callback 包含不支持的 JSON 类型。"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="lease-1",
                value={"callback": lambda: "ignored"},
            )
        )


def test_respond_builder_when_error_data_is_not_json_value_should_raise() -> None:
    with pytest.raises(ValueError, match=r"error\.data\.callback 包含不支持的 JSON 类型。"):
        build_respond_params(
            RespondRequest(
                instance_id="inst-1",
                instance_session_token="token-1",
                invocation_id="invk-1",
                lease_token="lease-1",
                error=DevHubCalleeError(
                    code=1001,
                    message="app_error",
                    data={"callback": lambda: "ignored"},
                ),
            )
        )


def test_respond_builder_when_error_data_is_scalar_should_preserve_value() -> None:
    payload = build_respond_params(
        RespondRequest(
            instance_id="inst-1",
            instance_session_token="token-1",
            invocation_id="invk-1",
            lease_token="lease-1",
            error=DevHubCalleeError(
                code=1001,
                message="app_error",
                data="invalid-name",
            ),
        )
    )

    assert payload["error"]["data"] == "invalid-name"
