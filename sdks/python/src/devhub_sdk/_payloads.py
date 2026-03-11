from __future__ import annotations

from typing import Any

from ._validation import (
    ensure_json_object,
    ensure_json_value,
    require_bool,
    require_non_empty_string,
    require_optional_bool,
    require_optional_int_at_least,
    require_optional_int_in_range,
    require_optional_string,
)
from .models import (
    AppInstanceRegistration,
    DevHubCalleeError,
    InvokeRequest,
    InvocationTarget,
    LaunchRequest,
    ListInstancesRequest,
    PollRequest,
    RespondRequest,
)


_MISSING = object()


def build_get_definition_params(app_id: str) -> dict[str, Any]:
    """构造 `hub.apps.getDefinition` 参数。"""

    return {"appId": require_non_empty_string(app_id, "app_id")}


def build_register_instance_params(instance: AppInstanceRegistration) -> dict[str, Any]:
    """构造 `hub.apps.registerInstance` 参数。"""

    if instance is None:
        raise ValueError("instance 不能为空。")

    instance_id = require_non_empty_string(instance.instance_id, "instance.instance_id")
    app_id = require_non_empty_string(instance.app_id, "instance.app_id")
    scope = require_optional_string(instance.scope, "instance.scope")
    if not isinstance(instance.pid, int) or isinstance(instance.pid, bool) or instance.pid < 1:
        raise ValueError("instance.pid 必须大于等于 1。")

    invoke = instance.invoke
    poll = require_bool(getattr(invoke, "poll", _MISSING), "invoke.poll")
    respond = require_bool(getattr(invoke, "respond", _MISSING), "invoke.respond")

    payload: dict[str, Any] = {
        "instanceId": instance_id,
        "appId": app_id,
        "pid": instance.pid,
        "invoke": {
            "poll": poll,
            "respond": respond,
        },
    }
    if scope is not None:
        payload["scope"] = scope
    if instance.meta is not None:
        meta = _ensure_json_object(instance.meta, "meta")
        payload["meta"] = meta
    return {"instance": payload}


def build_heartbeat_params(instance_id: str) -> dict[str, Any]:
    """构造 `hub.apps.heartbeat` 参数。"""

    return {"instanceId": require_non_empty_string(instance_id, "instance_id")}


def build_unregister_params(instance_id: str) -> dict[str, Any]:
    """构造 `hub.apps.unregisterInstance` 参数。"""

    return {"instanceId": require_non_empty_string(instance_id, "instance_id")}


def build_list_instances_params(request: ListInstancesRequest | None) -> dict[str, Any] | None:
    """构造 `hub.apps.listInstances` 参数。"""

    if request is None:
        return None

    payload: dict[str, Any] = {}
    app_id = require_optional_string(request.app_id, "app_id", allow_empty=False)
    scope = require_optional_string(request.scope, "scope")
    include_all_scopes = require_optional_bool(request.include_all_scopes, "include_all_scopes")
    include_offline = require_optional_bool(request.include_offline, "include_offline")

    if app_id is not None:
        payload["appId"] = app_id
    if scope is not None:
        payload["scope"] = scope
    if include_all_scopes:
        payload["includeAllScopes"] = True
    if include_offline:
        payload["includeOffline"] = True
    return payload or None


def build_launch_params(request: LaunchRequest) -> dict[str, Any]:
    """构造 `hub.apps.launch` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    app_id = require_non_empty_string(request.app_id, "request.app_id")
    scope = require_optional_string(request.scope, "request.scope")
    dedupe_key = require_optional_string(request.dedupe_key, "request.dedupe_key")
    wait_for_register_ms = require_optional_int_at_least(
        request.wait_for_register_ms,
        0,
        "wait_for_register_ms 必须为大于等于 0 的整数。",
    )

    payload: dict[str, Any] = {"appId": app_id}
    if scope is not None:
        payload["scope"] = scope
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

    instance_id = require_non_empty_string(request.instance_id, "request.instance_id")
    max_count = require_optional_int_in_range(request.max_count, 1, 100, "max_count 必须位于 1..100。")
    wait_ms = require_optional_int_at_least(request.wait_ms, 0, "wait_ms 必须为大于等于 0 的整数。")

    return {
        "instanceId": instance_id,
        "maxCount": 10 if max_count is None else max_count,
        "waitMs": 25000 if wait_ms is None else wait_ms,
    }


def build_respond_params(request: RespondRequest) -> dict[str, Any]:
    """构造 `hub.invoke.respond` 参数。"""

    if request is None:
        raise ValueError("request 不能为空。")

    instance_id = require_non_empty_string(request.instance_id, "request.instance_id")
    invocation_id = require_non_empty_string(request.invocation_id, "request.invocation_id")

    has_value = getattr(request, "_has_value", True)
    has_error = request.error is not None
    if has_value == has_error:
        raise ValueError("RespondRequest 必须且只能包含 value 或 error 之一。")

    payload: dict[str, Any] = {
        "instanceId": instance_id,
        "invocationId": invocation_id,
    }
    if has_error:
        payload["error"] = _callee_error_to_dict(request.error)
    else:
        payload["value"] = ensure_json_value(request.value, "value")
    return payload


def _build_invoke_params(request: InvokeRequest, *, is_request: bool) -> dict[str, Any]:
    if request is None:
        raise ValueError("request 不能为空。")

    app_id = require_non_empty_string(request.app_id, "request.app_id")
    method = require_non_empty_string(request.method, "request.method")

    target_scope = None
    target_instance_id = None
    if request.target is not None:
        target_scope = require_optional_string(getattr(request.target, "scope", _MISSING), "target.scope")
        target_instance_id = require_optional_string(
            getattr(request.target, "instance_id", _MISSING),
            "target.instance_id",
            allow_empty=False,
            error_message="target.instance_id 不能为空白字符串。",
        )

    options = request.options
    ttl_ms = require_optional_int_at_least(
        getattr(options, "ttl_ms", None) if options is not None else None,
        1000,
        "ttl_ms 必须大于等于 1000。",
    )
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
        "args": ensure_json_value(request.args, "args"),
        "options": {
            "ttlMs": ttl_ms,
            "queueIfOffline": queue_if_offline,
            "autoLaunch": auto_launch,
        },
    }
    if is_request:
        payload["options"]["waitTimeoutMs"] = wait_timeout_ms
    if request.target is not None:
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
