from __future__ import annotations

import json
from typing import Any

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


def build_get_definition_params(app_id: str) -> dict[str, Any]:
    """构造 `hub.apps.getDefinition` 参数。"""

    if not app_id or not app_id.strip():
        raise ValueError("app_id 不能为空。")
    return {"appId": app_id}


def build_register_instance_params(instance: AppInstanceRegistration) -> dict[str, Any]:
    """构造 `hub.apps.registerInstance` 参数。"""

    if not instance.instance_id or not instance.instance_id.strip():
        raise ValueError("instance.instance_id 不能为空。")
    if not instance.app_id or not instance.app_id.strip():
        raise ValueError("instance.app_id 不能为空。")
    if instance.pid < 1:
        raise ValueError("instance.pid 必须大于等于 1。")

    payload: dict[str, Any] = {
        "instanceId": instance.instance_id,
        "appId": instance.app_id,
        "pid": instance.pid,
        "invoke": {
            "poll": instance.invoke.poll,
            "respond": instance.invoke.respond,
        },
    }
    if instance.scope is not None:
        payload["scope"] = instance.scope
    if instance.meta is not None:
        meta = _ensure_json_object(instance.meta, "meta")
        payload["meta"] = meta
    return {"instance": payload}


def build_heartbeat_params(instance_id: str) -> dict[str, Any]:
    """构造 `hub.apps.heartbeat` 参数。"""

    if not instance_id or not instance_id.strip():
        raise ValueError("instance_id 不能为空。")
    return {"instanceId": instance_id}


def build_unregister_params(instance_id: str) -> dict[str, Any]:
    """构造 `hub.apps.unregisterInstance` 参数。"""

    if not instance_id or not instance_id.strip():
        raise ValueError("instance_id 不能为空。")
    return {"instanceId": instance_id}


def build_list_instances_params(request: ListInstancesRequest | None) -> dict[str, Any] | None:
    """构造 `hub.apps.listInstances` 参数。"""

    if request is None:
        return None

    payload: dict[str, Any] = {}
    if request.app_id is not None:
        payload["appId"] = request.app_id
    if request.scope is not None:
        payload["scope"] = request.scope
    if request.include_all_scopes:
        payload["includeAllScopes"] = True
    if request.include_offline:
        payload["includeOffline"] = True
    return payload or None


def build_launch_params(request: LaunchRequest) -> dict[str, Any]:
    """构造 `hub.apps.launch` 参数。"""

    if not request.app_id or not request.app_id.strip():
        raise ValueError("request.app_id 不能为空。")
    if request.wait_for_register_ms is not None and request.wait_for_register_ms < 0:
        raise ValueError("wait_for_register_ms 不能小于 0。")

    payload: dict[str, Any] = {"appId": request.app_id}
    if request.scope is not None:
        payload["scope"] = request.scope
    if request.dedupe_key is not None:
        payload["dedupeKey"] = request.dedupe_key
    if request.wait_for_register_ms is not None:
        payload["waitForRegisterMs"] = request.wait_for_register_ms
    return payload


def build_notify_params(request: InvokeRequest) -> dict[str, Any]:
    """构造 `hub.invoke.notify` 参数。"""

    return _build_invoke_params(request, is_request=False)


def build_request_params(request: InvokeRequest) -> dict[str, Any]:
    """构造 `hub.invoke.request` 参数。"""

    return _build_invoke_params(request, is_request=True)


def build_poll_params(request: PollRequest) -> dict[str, Any]:
    """构造 `hub.invoke.poll` 参数。"""

    if not request.instance_id or not request.instance_id.strip():
        raise ValueError("request.instance_id 不能为空。")

    max_count = 10 if request.max_count is None else request.max_count
    wait_ms = 25000 if request.wait_ms is None else request.wait_ms
    if max_count < 1 or max_count > 100:
        raise ValueError("max_count 必须位于 1..100。")
    if wait_ms < 0:
        raise ValueError("wait_ms 不能小于 0。")

    return {
        "instanceId": request.instance_id,
        "maxCount": max_count,
        "waitMs": wait_ms,
    }


def build_respond_params(request: RespondRequest) -> dict[str, Any]:
    """构造 `hub.invoke.respond` 参数。"""

    if not request.instance_id or not request.instance_id.strip():
        raise ValueError("request.instance_id 不能为空。")
    if not request.invocation_id or not request.invocation_id.strip():
        raise ValueError("request.invocation_id 不能为空。")

    has_error = request.error is not None
    if has_error and request.value is not None:
        raise ValueError("RespondRequest 必须且只能包含 value 或 error 之一。")

    payload: dict[str, Any] = {
        "instanceId": request.instance_id,
        "invocationId": request.invocation_id,
    }
    if has_error:
        payload["error"] = _callee_error_to_dict(request.error)
    else:
        payload["value"] = request.value
    return payload


def _build_invoke_params(request: InvokeRequest, *, is_request: bool) -> dict[str, Any]:
    if not request.app_id or not request.app_id.strip():
        raise ValueError("request.app_id 不能为空。")
    if not request.method or not request.method.strip():
        raise ValueError("request.method 不能为空。")

    target = request.target or InvocationTarget()
    if target.instance_id is not None and not target.instance_id.strip():
        raise ValueError("target.instance_id 不能为空白字符串。")

    ttl_ms = request.options.ttl_ms if request.options and request.options.ttl_ms is not None else (300000 if is_request else 60000)
    wait_timeout_ms = request.options.wait_timeout_ms if request.options else None
    if is_request and wait_timeout_ms is None:
        wait_timeout_ms = 120000
    queue_if_offline = request.options.queue_if_offline if request.options and request.options.queue_if_offline is not None else True
    auto_launch = request.options.auto_launch if request.options and request.options.auto_launch is not None else target.instance_id is None

    if ttl_ms < 1000:
        raise ValueError("ttl_ms 必须大于等于 1000。")
    if wait_timeout_ms is not None and wait_timeout_ms < 1:
        raise ValueError("wait_timeout_ms 必须大于等于 1。")
    if wait_timeout_ms is not None and wait_timeout_ms > ttl_ms:
        raise ValueError("wait_timeout_ms 不能大于 ttl_ms。")
    if target.instance_id is not None and auto_launch:
        raise ValueError("指定 target.instance_id 时不能启用 auto_launch。")
    if auto_launch and not queue_if_offline:
        raise ValueError("启用 auto_launch 时 queue_if_offline 必须为 true。")

    payload: dict[str, Any] = {
        "appId": request.app_id,
        "method": request.method,
        "args": request.args,
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
            "scope": request.target.scope,
            "instanceId": request.target.instance_id,
        }
    return payload


def _ensure_json_object(value: Any, name: str) -> dict[str, Any]:
    try:
        parsed = json.loads(json.dumps(value))
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{name} 必须可序列化为 JSON 对象。") from exc
    if not isinstance(parsed, dict):
        raise ValueError(f"{name} 必须序列化为 JSON 对象。")
    return parsed


def _callee_error_to_dict(error: DevHubCalleeError | None) -> dict[str, Any]:
    if error is None:
        raise ValueError("error 不能为空。")
    if not error.message or not error.message.strip():
        raise ValueError("error.message 不能为空。")
    payload: dict[str, Any] = {
        "code": error.code,
        "message": error.message,
    }
    if error.data is not None:
        payload["data"] = error.data
    return payload
