from __future__ import annotations

from collections.abc import Iterable, Mapping
from typing import Any

from .constants import DevHubEventType, ensure_supported_event_type
from ._validation import (
    ensure_json_object,
    ensure_json_value,
    require_app_id,
    require_bool,
    require_instance_id,
    require_invocation_id,
    require_non_empty_string,
    require_optional_bool,
    require_optional_app_id,
    require_optional_int_at_least,
    require_optional_int_in_range,
    require_optional_instance_id,
    require_optional_string,
    require_protocol_version,
    require_scope_filter,
    require_scoped_string,
    require_uuid_string,
)
from .models import (
    AppCapabilities,
    AppDefinition,
    AppInstanceRegistration,
    DevHubClientOptions,
    DevHubCalleeError,
    InvokeRequest,
    InvocationTarget,
    ListDefinitionsRequest,
    LaunchConfiguration,
    LaunchRequest,
    ListInstancesRequest,
    PollRequest,
    RespondRequest,
    RuntimeConnectionInfo,
)


_MISSING = object()


def build_get_definition_params(app_id: str, scope: str) -> dict[str, Any]:
    """构造 `hub.apps.getDefinition` 参数。"""

    return {
        "appId": require_app_id(app_id, "app_id"),
        "scope": require_scoped_string(scope, "scope"),
    }


def build_get_instance_params(instance_id: str) -> dict[str, Any]:
    """构造 `hub.apps.getInstance` 参数。"""

    return {
        "instanceId": require_instance_id(instance_id, "instance_id"),
    }


def build_ping_params(echo: Any = _MISSING) -> dict[str, Any] | None:
    """构造 `hub.ping` 参数。"""

    return None if echo is _MISSING else {"echo": ensure_json_value(echo, "echo")}


def build_validate_definition_params(definition: AppDefinition) -> dict[str, Any]:
    """构造 `hub.apps.validateDefinition` 参数。"""

    return {"definition": _build_definition_payload(definition)}


def build_upsert_definition_params(definition: AppDefinition) -> dict[str, Any]:
    """构造 `hub.apps.upsertDefinition` 参数。"""

    return {"definition": _build_definition_payload(definition)}


def build_delete_definition_params(app_id: str, scope: str) -> dict[str, Any]:
    """构造 `hub.apps.deleteDefinition` 参数。"""

    return {
        "appId": require_app_id(app_id, "app_id"),
        "scope": require_scoped_string(scope, "scope"),
    }


def build_register_instance_params(instance: AppInstanceRegistration, password: str) -> dict[str, Any]:
    """构造 `hub.apps.registerInstance` 参数。"""

    if instance is None:
        raise ValueError("instance 不能为空。")
    if getattr(instance, "password", _MISSING) is not _MISSING:
        raise ValueError("instance.password 不得出现。")
    if getattr(instance, "instance_session_token", _MISSING) is not _MISSING:
        raise ValueError("instance.instance_session_token 不得出现。")

    normalized_password = require_non_empty_string(password, "password")
    instance_id = require_instance_id(instance.instance_id, "instance.instance_id")
    app_id = require_app_id(instance.app_id, "instance.app_id")
    scope = require_scoped_string(instance.scope, "instance.scope")
    if not isinstance(instance.pid, int) or isinstance(instance.pid, bool) or instance.pid < 1:
        raise ValueError("instance.pid 必须大于等于 1。")

    invoke = instance.invoke
    poll = require_bool(getattr(invoke, "poll", _MISSING), "invoke.poll")
    respond = require_bool(getattr(invoke, "respond", _MISSING), "invoke.respond")

    payload: dict[str, Any] = {
        "instanceId": instance_id,
        "appId": app_id,
        "scope": scope,
        "pid": instance.pid,
        "invoke": {
            "poll": poll,
            "respond": respond,
        },
    }
    if instance.meta is not None:
        meta = _ensure_json_object(instance.meta, "meta")
        payload["meta"] = meta
    return {
        "password": normalized_password,
        "instance": payload,
    }


def build_heartbeat_params(instance_id: str, instance_session_token: str) -> dict[str, Any]:
    """构造 `hub.apps.heartbeat` 参数。"""

    return {
        "instanceId": require_instance_id(instance_id, "instance_id"),
        "instanceSessionToken": require_non_empty_string(instance_session_token, "instance_session_token"),
    }


def build_unregister_params(instance_id: str, instance_session_token: str) -> dict[str, Any]:
    """构造 `hub.apps.unregisterInstance` 参数。"""

    return {
        "instanceId": require_instance_id(instance_id, "instance_id"),
        "instanceSessionToken": require_non_empty_string(instance_session_token, "instance_session_token"),
    }


def build_list_definitions_params(request: ListDefinitionsRequest) -> dict[str, Any]:
    """构造 `hub.apps.listDefinitions` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    payload: dict[str, Any] = {
        "scope": require_scope_filter(request.scope, "scope"),
    }
    app_id = require_optional_app_id(request.app_id, "app_id")
    if app_id is not None:
        payload["appId"] = app_id
    return payload


def build_list_instances_params(request: ListInstancesRequest) -> dict[str, Any]:
    """构造 `hub.apps.listInstances` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    payload: dict[str, Any] = {
        "scope": require_scope_filter(request.scope, "scope"),
    }
    app_id = require_optional_app_id(request.app_id, "app_id")
    include_offline = require_optional_bool(request.include_offline, "include_offline")

    if app_id is not None:
        payload["appId"] = app_id
    if include_offline:
        payload["includeOffline"] = True
    return payload


def build_ws_authenticate_params(
    options: DevHubClientOptions,
    connection_info: RuntimeConnectionInfo,
) -> dict[str, Any]:
    """构造 `hub.ws.authenticate` 参数。"""

    if options is None:
        raise ValueError("options 不能为空。")
    if connection_info is None:
        raise ValueError("connection_info 不能为空。")

    return {
        "token": require_non_empty_string(connection_info.token, "connection_info.token"),
        "protocolVersion": require_protocol_version(options.protocol_version),
        "clientId": require_non_empty_string(options.client_id, "options.client_id"),
        "clientSessionId": require_uuid_string(options.client_session_id, "options.client_session_id"),
    }


def build_subscribe_params(types: Iterable[DevHubEventType] | None = None) -> dict[str, Any] | None:
    """构造 `hub.events.subscribe` 参数。"""

    if types is None:
        return None
    if isinstance(types, str | bytes | bytearray):
        raise ValueError("types 必须为事件类型序列。")
    if isinstance(types, Mapping):
        raise ValueError("types 必须为事件类型序列。")

    types_list = list(types)
    if not types_list:
        return None

    return {
        "types": [
            ensure_supported_event_type(item, f"types[{index}]")
            for index, item in enumerate(types_list)
        ]
    }


def build_unsubscribe_params(subscription_id: str) -> dict[str, Any]:
    """构造 `hub.events.unsubscribe` 参数。"""

    return {"subscriptionId": require_non_empty_string(subscription_id, "subscription_id")}


def build_launch_params(request: LaunchRequest) -> dict[str, Any]:
    """构造 `hub.apps.launch` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    app_id = require_app_id(request.app_id, "request.app_id")
    scope = require_scoped_string(request.scope, "request.scope")
    dedupe_key = require_optional_string(request.dedupe_key, "request.dedupe_key")
    wait_for_register_ms = require_optional_int_at_least(
        request.wait_for_register_ms,
        0,
        "wait_for_register_ms 必须为大于等于 0 的整数。",
    )

    payload: dict[str, Any] = {
        "appId": app_id,
        "scope": scope,
    }
    if dedupe_key is not None:
        payload["dedupeKey"] = dedupe_key
    if wait_for_register_ms is not None:
        payload["waitForRegisterMs"] = wait_for_register_ms
    return payload


def build_notify_params(request: InvokeRequest) -> dict[str, Any]:
    """构造 `hub.invoke.notify` 参数。"""

    return _build_invoke_params(request, is_request=False)


def build_request_params(request: InvokeRequest) -> dict[str, Any]:
    """构造 `hub.invoke.request` 参数。"""

    return _build_invoke_params(request, is_request=True)


def build_poll_params(request: PollRequest) -> dict[str, Any]:
    """构造 `hub.invoke.poll` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    instance_id = require_instance_id(request.instance_id, "request.instance_id")
    max_count = require_optional_int_in_range(request.max_count, 1, 100, "max_count 必须位于 1..100。")
    wait_ms = require_optional_int_at_least(request.wait_ms, 0, "wait_ms 必须为大于等于 0 的整数。")

    return {
        "instanceId": instance_id,
        "instanceSessionToken": require_non_empty_string(
            request.instance_session_token,
            "request.instance_session_token",
        ),
        "maxCount": 10 if max_count is None else max_count,
        "waitMs": 25000 if wait_ms is None else wait_ms,
    }


def build_respond_params(request: RespondRequest) -> dict[str, Any]:
    """构造 `hub.invoke.respond` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    instance_id = require_instance_id(request.instance_id, "request.instance_id")
    invocation_id = require_invocation_id(request.invocation_id, "request.invocation_id")

    has_value = getattr(request, "_has_value", True)
    has_error = request.error is not None
    if has_value == has_error:
        raise ValueError("RespondRequest 必须且只能包含 value 或 error 之一。")

    payload: dict[str, Any] = {
        "instanceId": instance_id,
        "instanceSessionToken": require_non_empty_string(
            request.instance_session_token,
            "request.instance_session_token",
        ),
        "invocationId": invocation_id,
    }
    if has_error:
        payload["error"] = _callee_error_to_dict(request.error)
    else:
        payload["value"] = ensure_json_value(request.value, "value")
    return payload


def _build_definition_payload(definition: AppDefinition) -> dict[str, Any]:
    if definition is None:
        raise ValueError("definition 不能为空。")

    app_id = require_app_id(definition.app_id, "definition.app_id")
    scope = require_scoped_string(definition.scope, "definition.scope")
    display_name = require_optional_string(definition.display_name, "definition.display_name")
    if display_name is None:
        raise ValueError("definition.display_name 类型非法。")

    payload: dict[str, Any] = {
        "appId": app_id,
        "scope": scope,
        "displayName": display_name,
    }
    description = require_optional_string(definition.description, "definition.description")
    if description is not None:
        payload["description"] = description

    capabilities = definition.capabilities
    if capabilities is not None:
        payload["capabilities"] = _build_capabilities_payload(capabilities)

    launch = definition.launch
    if launch is not None:
        payload["launch"] = _build_launch_payload(launch)

    return payload


def _build_capabilities_payload(capabilities: AppCapabilities) -> dict[str, Any]:
    payload: dict[str, Any] = {}
    rpc = require_optional_bool(capabilities.rpc, "definition.capabilities.rpc")
    events = require_optional_bool(capabilities.events, "definition.capabilities.events")
    if rpc is not None:
        payload["rpc"] = rpc
    if events is not None:
        payload["events"] = events
    return payload


def _build_launch_payload(launch: LaunchConfiguration) -> dict[str, Any]:
    exe_path = require_optional_string(launch.exe_path, "definition.launch.exe_path")
    if exe_path is None:
        raise ValueError("definition.launch.exe_path 类型非法。")

    payload: dict[str, Any] = {"exePath": exe_path}
    args_template = require_optional_string(launch.args_template, "definition.launch.args_template")
    working_directory = require_optional_string(launch.working_directory, "definition.launch.working_directory")
    dedupe_key_template = require_optional_string(
        launch.dedupe_key_template,
        "definition.launch.dedupe_key_template",
    )
    if args_template is not None:
        payload["argsTemplate"] = args_template
    if working_directory is not None:
        payload["workingDirectory"] = working_directory
    if dedupe_key_template is not None:
        payload["dedupeKeyTemplate"] = dedupe_key_template
    return payload


def _build_invoke_params(request: InvokeRequest, *, is_request: bool) -> dict[str, Any]:
    if request is None:
        raise ValueError("request 不能为空。")

    app_id = require_app_id(request.app_id, "request.app_id")
    method = require_non_empty_string(request.method, "request.method")
    if request.target is None:
        raise ValueError("request.target 不能为空。")

    target_scope = require_scoped_string(getattr(request.target, "scope", _MISSING), "target.scope")
    target_instance_id = require_optional_instance_id(
        getattr(request.target, "instance_id", _MISSING),
        "target.instance_id",
    )

    options = request.options
    ttl_ms = require_optional_int_at_least(
        getattr(options, "ttl_ms", None) if options is not None else None,
        1000,
        "ttl_ms 必须大于等于 1000。",
    )
    notify_wait_timeout_ms = getattr(options, "wait_timeout_ms", None) if options is not None else None
    if not is_request and notify_wait_timeout_ms is not None:
        raise ValueError("hub.invoke.notify 不支持 wait_timeout_ms。")
    wait_timeout_ms = getattr(options, "wait_timeout_ms", None) if options is not None else None
    if is_request and wait_timeout_ms is None:
        wait_timeout_ms = 120000
    wait_timeout_ms = require_optional_int_at_least(
        wait_timeout_ms,
        1,
        "wait_timeout_ms 必须大于等于 1。",
    )
    queue_if_offline = require_optional_bool(
        getattr(options, "queue_if_offline", None) if options is not None else None,
        "queue_if_offline",
    )
    auto_launch = require_optional_bool(
        getattr(options, "auto_launch", None) if options is not None else None,
        "auto_launch",
    )

    ttl_ms = 300000 if ttl_ms is None and is_request else ttl_ms
    ttl_ms = 60000 if ttl_ms is None else ttl_ms
    queue_if_offline = True if queue_if_offline is None else queue_if_offline
    auto_launch = (target_instance_id is None) if auto_launch is None else auto_launch

    if wait_timeout_ms is not None and wait_timeout_ms > ttl_ms:
        raise ValueError("wait_timeout_ms 不能大于 ttl_ms。")
    if target_instance_id is not None and auto_launch:
        raise ValueError("指定 target.instance_id 时不能启用 auto_launch。")
    if auto_launch and not queue_if_offline:
        raise ValueError("启用 auto_launch 时 queue_if_offline 必须为 true。")

    payload: dict[str, Any] = {
        "appId": app_id,
        "method": method,
        "options": {
            "ttlMs": ttl_ms,
            "queueIfOffline": queue_if_offline,
            "autoLaunch": auto_launch,
        },
    }
    if getattr(request, "_has_args", True):
        payload["args"] = ensure_json_value(request.args, "args")
    if is_request:
        payload["options"]["waitTimeoutMs"] = wait_timeout_ms
    payload["target"] = {
        "scope": target_scope,
        "instanceId": target_instance_id,
    }
    return payload


def _ensure_json_object(value: Any, name: str) -> dict[str, Any]:
    return ensure_json_object(value, name)


def _callee_error_to_dict(error: DevHubCalleeError | None) -> dict[str, Any]:
    if error is None:
        raise ValueError("error 不能为空。")
    code = getattr(error, "code", _MISSING)
    if not isinstance(code, int) or isinstance(code, bool):
        raise ValueError("error.code 必须为整数。")
    message = require_non_empty_string(getattr(error, "message", _MISSING), "error.message")
    payload: dict[str, Any] = {
        "code": code,
        "message": message,
    }
    if error.data is not None:
        payload["data"] = ensure_json_object(error.data, "error.data")
    return payload
