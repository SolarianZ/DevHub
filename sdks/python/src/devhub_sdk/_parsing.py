from __future__ import annotations

import re
from ipaddress import ip_address
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Mapping
from urllib.parse import urlparse

from .constants import ensure_supported_event_type
from ._validation import (
    ensure_json_object,
    ensure_json_value,
    require_app_id as validate_app_id,
    require_instance_id as validate_instance_id,
    require_invocation_id as validate_invocation_id,
    require_optional_instance_id as validate_optional_instance_id,
    require_scoped_string as validate_scope_string,
    require_uuid_string as validate_uuid_string,
)
from .models import (
    AppCapabilities,
    AppDefinition,
    AppInstance,
    DefinitionValidationResult,
    DevHubCalleeError,
    DevHubEvent,
    HubRuntime,
    HubRuntimeTuning,
    Invocation,
    InvocationCaller,
    InvocationDelivery,
    InvocationKind,
    InvocationOptions,
    InvocationTarget,
    InvokeCapability,
    LaunchConfiguration,
    LaunchResult,
    NotifyResult,
    PingResult,
    PollResult,
    RequestResult,
    ValidationIssue,
)

_LAUNCH_STATUS_VALUES = {"started", "starting", "already_running"}
_RFC3339_TIMESTAMP_PATTERN = re.compile(
    r"^\d{4}-\d{2}-\d{2}"
    r"T\d{2}:\d{2}:\d{2}"
    r"(?:\.\d+)?"
    r"(?:Z|[+-]\d{2}:\d{2})$"
)
_MISSING = object()


def parse_hub_runtime(value: Any, *, source: str) -> HubRuntime:
    """解析并校验 `hub.json`。"""

    root = require_mapping(value, source)
    protocol_version = require_int(root, "protocolVersion", source)
    if protocol_version != 1:
        raise RuntimeError(f"hub.json.protocolVersion 非法：{source}")

    pid = require_int(root, "pid", source)
    if pid < 1:
        raise RuntimeError(f"hub.json.pid 非法：{source}")

    http_base_url = require_non_empty_string(root, "httpBaseUrl", source)
    _validate_loopback_url(
        http_base_url,
        {"http", "https"},
        source,
        "httpBaseUrl",
        origin_only=True,
    )

    ws_url = require_non_empty_string(root, "wsUrl", source)
    _validate_websocket_url(ws_url, source)

    token_file = require_non_empty_string(root, "tokenFile", source)
    if not Path(token_file).is_absolute():
        raise RuntimeError(f"hub.json.tokenFile 非法：{source}")

    started_at_utc = require_datetime(root, "startedAtUtc", source)
    runtime_tuning = require_mapping(root.get("runtimeTuning"), f"{source}.runtimeTuning")
    lease_seconds = require_int(runtime_tuning, "leaseSeconds", f"{source}.runtimeTuning")
    online_threshold_seconds = require_int(runtime_tuning, "onlineThresholdSeconds", f"{source}.runtimeTuning")
    launch_dedupe_window_seconds = require_int(runtime_tuning, "launchDedupeWindowSeconds", f"{source}.runtimeTuning")
    launch_register_timeout_seconds = require_int(runtime_tuning, "launchRegisterTimeoutSeconds", f"{source}.runtimeTuning")
    if (
        lease_seconds < 1
        or online_threshold_seconds < 1
        or launch_dedupe_window_seconds < 1
        or launch_register_timeout_seconds < 1
    ):
        raise RuntimeError(f"hub.json.runtimeTuning 非法：{source}")

    hub_version = optional_property_string(root, "hubVersion", source)
    return HubRuntime(
        protocol_version=protocol_version,
        pid=pid,
        http_base_url=http_base_url,
        ws_url=ws_url,
        token_file=token_file,
        started_at_utc=started_at_utc,
        runtime_tuning=HubRuntimeTuning(
            lease_seconds=lease_seconds,
            online_threshold_seconds=online_threshold_seconds,
            launch_dedupe_window_seconds=launch_dedupe_window_seconds,
            launch_register_timeout_seconds=launch_register_timeout_seconds,
        ),
        hub_version=hub_version,
    )


def parse_ping_result(value: Any, *, path: str) -> PingResult:
    """解析 ping 结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    server_time_utc = require_datetime(root, "serverTimeUtc", path)
    echo = _require_json_value(root["echo"], f"{path}.echo") if "echo" in root else None
    return PingResult(ok=ok, server_time_utc=server_time_utc, echo=echo)


def parse_host_version_result(value: Any, *, path: str) -> str:
    """解析 `hub.getVersion` 结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    return require_non_empty_string(root, "version", path)


def parse_definitions_result(value: Any, *, path: str) -> list[AppDefinition]:
    """解析应用定义列表结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    definitions = root.get("definitions")
    if not isinstance(definitions, list):
        raise RuntimeError(f"{path}.definitions 必须为数组。")
    return [
        parse_app_definition(item, path=f"{path}.definitions[{index}]")
        for index, item in enumerate(definitions)
    ]


def parse_definition_result(value: Any, *, path: str) -> AppDefinition:
    """解析单个应用定义结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    return parse_app_definition(root.get("definition"), path=f"{path}.definition")


def parse_definition_validation_result(value: Any, *, path: str) -> DefinitionValidationResult:
    """解析定义校验结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    valid = require_bool(root, "valid", path)
    errors_value = root.get("errors")
    if not isinstance(errors_value, list):
        raise RuntimeError(f"{path}.errors 必须为数组。")

    errors = [
        parse_validation_issue(item, path=f"{path}.errors[{index}]")
        for index, item in enumerate(errors_value)
    ]
    if valid and errors:
        raise RuntimeError(f"{path}.errors 必须为空数组。")
    if not valid and not errors:
        raise RuntimeError(f"{path}.errors 至少包含一项。")
    return DefinitionValidationResult(ok=ok, valid=valid, errors=errors)


def parse_app_definition(value: Any, *, path: str) -> AppDefinition:
    """解析应用定义。"""

    root = require_mapping(value, path)
    display_name = require_non_blank_string(root, "displayName", path)
    capabilities = AppCapabilities(rpc=True)
    if "capabilities" in root:
        capabilities_root = require_mapping(root["capabilities"], f"{path}.capabilities")
        rpc = optional_property_bool(capabilities_root, "rpc", f"{path}.capabilities")
        capabilities = AppCapabilities(
            rpc=True if rpc is None else rpc,
            events=optional_property_bool(capabilities_root, "events", f"{path}.capabilities"),
        )

    launch = None
    if "launch" in root:
        launch_root = require_mapping(root["launch"], f"{path}.launch")
        launch = LaunchConfiguration(
            exe_path=optional_property_string(launch_root, "exePath", f"{path}.launch"),
            args=optional_property_string_list(launch_root, "args", f"{path}.launch"),
            args_template=optional_property_string(launch_root, "argsTemplate", f"{path}.launch"),
            working_directory=optional_property_string(launch_root, "workingDirectory", f"{path}.launch"),
            dedupe_key_template=optional_property_string(launch_root, "dedupeKeyTemplate", f"{path}.launch"),
        )

    return AppDefinition(
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        display_name=display_name,
        scope=require_scope_string(root, "scope", path),
        description=optional_property_string(root, "description", path),
        capabilities=capabilities,
        launch=launch,
    )


def parse_validation_issue(value: Any, *, path: str) -> ValidationIssue:
    """解析定义校验问题。"""

    root = require_mapping(value, path)
    return ValidationIssue(
        path=require_string_allow_empty(root, "path", path),
        code=require_string_allow_empty(root, "code", path),
        message=require_string_allow_empty(root, "message", path),
    )


def parse_app_instance(value: Any, *, path: str) -> AppInstance:
    """解析应用实例。"""

    root = require_mapping(value, path)
    if "password" in root:
        raise RuntimeError(f"{path}.password 不得出现。")
    if "instanceSessionToken" in root:
        raise RuntimeError(f"{path}.instanceSessionToken 不得出现。")
    invoke_root = require_mapping(root.get("invoke"), f"{path}.invoke")
    meta = None
    if "meta" in root:
        meta = _require_json_object(root["meta"], f"{path}.meta")
    return AppInstance(
        instance_id=require_validated_string(root, "instanceId", path, validate_instance_id),
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        scope=require_scope_string(root, "scope", path),
        pid=require_positive_int(root, "pid", path),
        registered_at_utc=require_datetime(root, "registeredAtUtc", path),
        last_seen_utc=require_datetime(root, "lastSeenUtc", path),
        invoke=InvokeCapability(
            poll=require_bool(invoke_root, "poll", f"{path}.invoke"),
            respond=require_bool(invoke_root, "respond", f"{path}.invoke"),
        ),
        meta=meta,
    )


def parse_register_instance_result(value: Any, *, path: str) -> AppInstance:
    """解析实例注册结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    instance = parse_app_instance(root.get("instance"), path=f"{path}.instance")
    instance.instance_session_token = require_non_empty_string(root, "instanceSessionToken", path)
    return instance


def parse_instance_result(value: Any, *, path: str) -> AppInstance:
    """解析单个实例结果。"""

    root = require_mapping(value, path)
    if "password" in root:
        raise RuntimeError(f"{path}.password 不得出现。")
    if "instanceSessionToken" in root:
        raise RuntimeError(f"{path}.instanceSessionToken 不得出现。")
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    return parse_app_instance(root.get("instance"), path=f"{path}.instance")


def parse_instances_result(value: Any, *, path: str) -> list[AppInstance]:
    """解析实例列表结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    instances = root.get("instances")
    if not isinstance(instances, list):
        raise RuntimeError(f"{path}.instances 必须为数组。")
    return [
        parse_app_instance(item, path=f"{path}.instances[{index}]")
        for index, item in enumerate(instances)
    ]


def parse_launch_result(value: Any, *, path: str) -> LaunchResult:
    """解析启动结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    status = require_non_empty_string(root, "status", path)
    if status not in _LAUNCH_STATUS_VALUES:
        raise RuntimeError(f"{path}.status 取值非法。")
    pid = None
    if "pid" in root:
        pid = root["pid"]
        if not isinstance(pid, int) or isinstance(pid, bool) or pid < 1:
            raise RuntimeError(f"{path}.pid 类型非法。")
    launch_id = optional_property_string(root, "launchId", path)
    dedupe_key = optional_property_string(root, "dedupeKey", path)
    instance_id = optional_property_string(root, "instanceId", path)
    if status in {"started", "starting"}:
        if not launch_id or not dedupe_key:
            raise RuntimeError(f"{path}.launchId 与 {path}.dedupeKey 必须存在。")
    elif not instance_id and (not launch_id or not dedupe_key):
        raise RuntimeError(f"{path}.instanceId 或 {path}.launchId + {path}.dedupeKey 必须存在。")

    return LaunchResult(
        ok=ok,
        status=status,
        launch_id=launch_id,
        pid=pid,
        dedupe_key=dedupe_key,
        instance_id=instance_id,
    )


def parse_notify_result(value: Any, *, path: str) -> NotifyResult:
    """解析通知结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    return NotifyResult(
        ok=ok,
        invocation_id=require_validated_string(root, "invocationId", path, validate_invocation_id),
    )


def parse_request_result(value: Any, *, path: str) -> RequestResult:
    """解析请求结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    if "value" not in root:
        raise RuntimeError(f"{path}.value 必须存在。")
    return RequestResult(
        ok=ok,
        invocation_id=require_validated_string(root, "invocationId", path, validate_invocation_id),
        value=_require_json_value(root["value"], f"{path}.value"),
    )


def parse_poll_result(value: Any, *, path: str) -> PollResult:
    """解析轮询结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    items_value = root.get("items")
    if not isinstance(items_value, list):
        raise RuntimeError(f"{path}.items 必须为数组。")
    items = [parse_invocation(item, path=f"{path}.items[{index}]") for index, item in enumerate(items_value)]
    return PollResult(ok=ok, server_time_utc=require_datetime(root, "serverTimeUtc", path), items=items)


def parse_invocation(value: Any, *, path: str) -> Invocation:
    """解析调用对象。"""

    root = require_mapping(value, path)
    target_root = require_mapping(root.get("target"), f"{path}.target")
    target = InvocationTarget(
        scope=require_scope_string(target_root, "scope", f"{path}.target"),
        instance_id=optional_validated_string(
            target_root.get("instanceId"),
            f"{path}.target.instanceId",
            validate_optional_instance_id,
        ),
    )

    options = None
    if "options" in root:
        options_root = require_mapping(root["options"], f"{path}.options")
        ttl_ms = optional_property_int_at_least(options_root, "ttlMs", f"{path}.options", 1000)
        wait_timeout_ms = optional_property_int_at_least(options_root, "waitTimeoutMs", f"{path}.options", 1)
        if wait_timeout_ms is not None and ttl_ms is not None and wait_timeout_ms > ttl_ms:
            raise RuntimeError(f"{path}.options.waitTimeoutMs 必须小于等于 {path}.options.ttlMs。")
        options = InvocationOptions(
            ttl_ms=ttl_ms,
            wait_timeout_ms=wait_timeout_ms,
            queue_if_offline=optional_property_bool(options_root, "queueIfOffline", f"{path}.options"),
            auto_launch=optional_property_bool(options_root, "autoLaunch", f"{path}.options"),
        )

    delivery = None
    if "delivery" in root:
        delivery_root = require_mapping(root["delivery"], f"{path}.delivery")
        delivery = InvocationDelivery(
            lease_seconds=require_positive_int(delivery_root, "leaseSeconds", f"{path}.delivery"),
            attempt=require_positive_int(delivery_root, "attempt", f"{path}.delivery"),
            lease_token=require_non_empty_string(delivery_root, "leaseToken", f"{path}.delivery"),
        )

    caller_root = require_mapping(root.get("caller"), f"{path}.caller")
    kind_value = require_non_empty_string(root, "kind", path)
    try:
        kind = InvocationKind(kind_value)
    except ValueError as exc:
        raise RuntimeError(f"{path}.kind 非法。") from exc

    return Invocation(
        invocation_id=require_validated_string(root, "invocationId", path, validate_invocation_id),
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        method=require_non_empty_string(root, "method", path),
        kind=kind,
        created_at_utc=require_datetime(root, "createdAtUtc", path),
        caller=InvocationCaller(
            client_id=require_non_empty_string(caller_root, "clientId", f"{path}.caller"),
            client_session_id=require_validated_string(
                caller_root,
                "clientSessionId",
                f"{path}.caller",
                validate_uuid_string,
            ),
        ),
        target=target,
        args=_require_json_value(root["args"], f"{path}.args") if "args" in root else None,
        options=options,
        delivery=delivery,
    )


def parse_event(value: Any, *, path: str) -> DevHubEvent:
    """解析事件通知。"""

    root = require_mapping(value, path)
    try:
        event_type = ensure_supported_event_type(require_non_empty_string(root, "type", path), f"{path}.type")
    except ValueError as exc:
        raise RuntimeError(str(exc)) from exc
    payload = _require_json_value(root["payload"], f"{path}.payload") if "payload" in root else None
    _validate_known_event_payload(event_type, payload, path=f"{path}.payload")
    return DevHubEvent(
        subscription_id=require_non_empty_string(root, "subscriptionId", path),
        type=event_type,
        time_utc=require_datetime(root, "timeUtc", path),
        payload=payload,
    )


def parse_callee_error(value: Any, *, path: str) -> DevHubCalleeError:
    """解析被调用方错误对象。"""

    root = require_mapping(value, path)
    data = _require_json_value(root["data"], f"{path}.data") if "data" in root else None
    return DevHubCalleeError(
        code=require_int(root, "code", path),
        message=require_non_empty_string(root, "message", path),
        data=data,
    )


def require_validated_string(
    root: Mapping[str, Any],
    name: str,
    path: str,
    validator: Callable[[Any, str], str],
) -> str:
    """读取并校验必须字段中的字符串格式。"""

    return _validate_string(root.get(name), f"{path}.{name}", validator)


def optional_validated_string(
    value: Any,
    path: str,
    validator: Callable[[Any, str], str | None],
) -> str | None:
    """读取并校验可选字符串格式。"""

    if value is None:
        return None
    return _validate_string(value, path, validator)


def _validate_string(
    value: Any,
    path: str,
    validator: Callable[[Any, str], str | None],
) -> str | None:
    try:
        return validator(value, path)
    except ValueError as exc:
        raise RuntimeError(str(exc)) from exc


def _validate_known_event_payload(event_type: str, payload: Any, *, path: str) -> None:
    if event_type in {
        "app.definition.upserted",
        "app.definition.deleted",
        "app.instance.registered",
        "app.instance.unregistered",
    }:
        if payload is None:
            raise RuntimeError(f"{path} 必须存在。")

        payload_root = require_mapping(payload, path)
        if event_type == "app.definition.upserted":
            require_validated_string(payload_root, "appId", path, validate_app_id)
            payload_scope = require_scope_string(payload_root, "scope", path)
            definition = parse_app_definition(payload_root.get("definition"), path=f"{path}.definition")
            if definition.scope != payload_scope:
                raise RuntimeError(f"{path}.definition.scope 必须与 {path}.scope 一致。")
            return

        if event_type == "app.definition.deleted":
            require_validated_string(payload_root, "appId", path, validate_app_id)
            require_scope_string(payload_root, "scope", path)
            return

        require_validated_string(payload_root, "appId", path, validate_app_id)
        require_validated_string(payload_root, "instanceId", path, validate_instance_id)
        require_scope_string(payload_root, "scope", path)
        if "password" in payload_root:
            raise RuntimeError(f"{path}.password 不得出现。")
        if "instanceSessionToken" in payload_root:
            raise RuntimeError(f"{path}.instanceSessionToken 不得出现。")


def optional_property_string(root: Mapping[str, Any], name: str, path: str) -> str | None:
    """读取“可省略但不可为 null”的字符串属性。"""

    if name not in root:
        return None
    return require_string_allow_empty(root, name, path)


def optional_property_bool(root: Mapping[str, Any], name: str, path: str) -> bool | None:
    """读取“可省略但不可为 null”的布尔属性。"""

    if name not in root:
        return None
    return require_bool(root, name, path)


def optional_property_string_list(root: Mapping[str, Any], name: str, path: str) -> list[str] | None:
    """读取“可省略但不可为 null”的字符串数组属性。"""

    if name not in root:
        return None
    value = root.get(name)
    if not isinstance(value, list):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    parsed: list[str] = []
    for index, item in enumerate(value):
        if not isinstance(item, str):
            raise RuntimeError(f"{path}.{name}[{index}] 类型非法。")
        parsed.append(item)
    return parsed


def optional_property_int_at_least(
    root: Mapping[str, Any],
    name: str,
    path: str,
    minimum_value: int,
) -> int | None:
    """读取“可省略但不可为 null”的整数属性，并要求其满足最小值。"""

    if name not in root:
        return None
    parsed = require_int(root, name, path)
    if parsed < minimum_value:
        raise RuntimeError(f"{path}.{name} 必须大于等于 {minimum_value}。")
    return parsed


def require_scope_string(root: Mapping[str, Any], name: str, path: str) -> str:
    """读取必须存在的显式字符串 scope 属性。"""

    if name not in root:
        raise RuntimeError(f"{path}.{name} 必须存在。")
    return parse_scope_string(root.get(name), f"{path}.{name}")


def _require_json_value(value: Any, path: str) -> Any:
    try:
        return ensure_json_value(value, path)
    except ValueError as exc:
        raise RuntimeError(str(exc)) from exc


def _require_json_object(value: Any, path: str) -> dict[str, Any]:
    try:
        return ensure_json_object(value, path)
    except ValueError as exc:
        raise RuntimeError(str(exc)) from exc


def require_mapping(value: Any, path: str) -> dict[str, Any]:
    """要求值必须为对象。"""

    if not isinstance(value, dict):
        raise RuntimeError(f"{path} 必须为对象。")
    return value


def require_non_empty_string(root: Mapping[str, Any], name: str, path: str) -> str:
    """读取必填字符串属性。"""

    value = root.get(name)
    if not isinstance(value, str) or not value.strip():
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def require_non_blank_string(root: Mapping[str, Any], name: str, path: str) -> str:
    """读取必填非空白字符串属性。"""

    value = root.get(name)
    if not isinstance(value, str) or not value.strip():
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def require_string_allow_empty(root: Mapping[str, Any], name: str, path: str) -> str:
    value = root.get(name)
    if not isinstance(value, str):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def optional_str(value: Any, path: str) -> str | None:
    """读取可选字符串属性。"""

    if value is None:
        return None
    if not isinstance(value, str):
        raise RuntimeError(f"{path} 类型非法。")
    return value


def parse_scope_string(value: Any, path: str) -> str:
    """读取显式字符串 scope。"""

    try:
        return validate_scope_string(value, path)
    except ValueError as exc:
        raise RuntimeError(str(exc)) from exc


def require_int(root: Mapping[str, Any], name: str, path: str) -> int:
    """读取必填整数属性。"""

    value = root.get(name)
    if not isinstance(value, int) or isinstance(value, bool):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def require_positive_int(root: Mapping[str, Any], name: str, path: str) -> int:
    """读取必填正整数属性。"""

    value = require_int(root, name, path)
    if value < 1:
        raise RuntimeError(f"{path}.{name} 必须为正整数。")
    return value


def optional_int(value: Any, path: str) -> int | None:
    """读取可选整数属性。"""

    if value is None:
        return None
    if not isinstance(value, int) or isinstance(value, bool):
        raise RuntimeError(f"{path} 类型非法。")
    return value


def optional_int_at_least(value: Any, path: str, minimum_value: int) -> int | None:
    """读取可选整数属性，并要求其不小于指定下限。"""

    parsed = optional_int(value, path)
    if parsed is not None and parsed < minimum_value:
        raise RuntimeError(f"{path} 必须大于等于 {minimum_value}。")
    return parsed


def require_bool(root: Mapping[str, Any], name: str, path: str) -> bool:
    """读取必填布尔属性。"""

    value = root.get(name)
    if not isinstance(value, bool):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def optional_bool(value: Any, path: str) -> bool | None:
    """读取可选布尔属性。"""

    if value is None:
        return None
    if not isinstance(value, bool):
        raise RuntimeError(f"{path} 类型非法。")
    return value


def require_datetime(root: Mapping[str, Any], name: str, path: str) -> datetime:
    """读取必填时间属性。"""

    value = root.get(name)
    if not isinstance(value, str):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return parse_datetime(value, f"{path}.{name}")


def parse_datetime(value: str, path: str) -> datetime:
    """解析 ISO 8601 UTC 时间。"""

    if _RFC3339_TIMESTAMP_PATTERN.fullmatch(value) is None:
        raise RuntimeError(f"{path} 类型非法。")

    normalized = value.replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as exc:
        raise RuntimeError(f"{path} 类型非法。") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise RuntimeError(f"{path} 类型非法。")
    if parsed.utcoffset() != timedelta(0):
        raise RuntimeError(f"{path} 类型非法。")
    return parsed.astimezone(timezone.utc)


def _validate_loopback_url(
    url: str,
    schemes: set[str],
    source: str,
    field_name: str,
    *,
    origin_only: bool = False,
) -> None:
    parsed = urlparse(url)
    if not url or url.strip() != url or url.endswith("/") or "?" in url or "#" in url:
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
    if parsed.scheme not in schemes or not parsed.hostname:
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
    if not _is_loopback_host(parsed.hostname):
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
    if origin_only and (
        parsed.username
        or parsed.password
        or parsed.path
        or parsed.query
        or parsed.fragment
        or parsed.params
    ):
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")


def _validate_websocket_url(url: str, source: str) -> None:
    parsed = urlparse(url)
    if (
        not url
        or url.strip() != url
        or url.endswith("/")
        or parsed.scheme not in {"ws", "wss"}
        or not parsed.hostname
        or not _is_loopback_host(parsed.hostname)
        or "@" in parsed.netloc
        or parsed.username
        or parsed.password
        or parsed.path != "/ws"
        or "?" in url
        or "#" in url
        or parsed.query
        or parsed.fragment
        or parsed.params
    ):
        raise RuntimeError(f"hub.json.wsUrl 非法：{source}")


def validate_runtime_endpoints(http_base_url: str, ws_url: str, source: str) -> None:
    """校验运行时 HTTP 与 WebSocket 端点形状。"""

    _validate_loopback_url(
        http_base_url,
        {"http", "https"},
        source,
        "httpBaseUrl",
        origin_only=True,
    )
    _validate_websocket_url(ws_url, source)


def _is_loopback_host(host: str) -> bool:
    if host == "localhost":
        return True
    try:
        return ip_address(host).is_loopback
    except ValueError:
        return False
