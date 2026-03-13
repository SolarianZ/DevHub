from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Mapping
from urllib.parse import urlparse

from ._validation import (
    require_app_id as validate_app_id,
    require_instance_id as validate_instance_id,
    require_invocation_id as validate_invocation_id,
    require_optional_instance_id as validate_optional_instance_id,
    require_uuid_string as validate_uuid_string,
)
from .models import (
    AppCapabilities,
    AppDefinition,
    AppInstance,
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
)

_LAUNCH_STATUS_VALUES = {"started", "starting", "already_running"}


def parse_hub_runtime(value: Any, *, source: str) -> HubRuntime:
    """解析并校验 `hub.json`。"""

    root = require_mapping(value, source)
    protocol_version = require_int(root, "protocolVersion", source)
    if protocol_version != 1:
        raise RuntimeError(f"hub.json.protocolVersion 非法：{source}")

    pid = require_int(root, "pid", source)
    if pid < 1:
        raise RuntimeError(f"hub.json.pid 非法：{source}")

    http_base_url = require_str(root, "httpBaseUrl", source)
    _validate_loopback_url(http_base_url, {"http", "https"}, source, "httpBaseUrl")

    ws_url = require_str(root, "wsUrl", source)
    _validate_loopback_url(ws_url, {"ws", "wss"}, source, "wsUrl")

    token_file = require_str(root, "tokenFile", source)
    if not Path(token_file).is_absolute():
        raise RuntimeError(f"hub.json.tokenFile 非法：{source}")

    started_at_utc = require_datetime(root, "startedAtUtc", source)
    runtime_tuning = require_mapping(root.get("runtimeTuning"), f"{source}.runtimeTuning")
    lease_seconds = require_int(runtime_tuning, "leaseSeconds", f"{source}.runtimeTuning")
    online_threshold_seconds = require_int(runtime_tuning, "onlineThresholdSeconds", f"{source}.runtimeTuning")
    launch_dedupe_window_seconds = require_int(runtime_tuning, "launchDedupeWindowSeconds", f"{source}.runtimeTuning")
    if lease_seconds < 1 or online_threshold_seconds < 1 or launch_dedupe_window_seconds < 1:
        raise RuntimeError(f"hub.json.runtimeTuning 非法：{source}")

    hub_version = optional_str(root.get("hubVersion"), f"{source}.hubVersion")
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
    return PingResult(ok=ok, server_time_utc=server_time_utc, echo=root.get("echo"))


def parse_app_definition(value: Any, *, path: str) -> AppDefinition:
    """解析应用定义。"""

    root = require_mapping(value, path)
    capabilities_value = root.get("capabilities")
    capabilities = AppCapabilities(rpc=True)
    if capabilities_value is not None:
        capabilities_root = require_mapping(capabilities_value, f"{path}.capabilities")
        rpc = optional_bool(capabilities_root.get("rpc"), f"{path}.capabilities.rpc")
        capabilities = AppCapabilities(
            rpc=True if rpc is None else rpc,
            events=optional_bool(capabilities_root.get("events"), f"{path}.capabilities.events"),
        )

    launch_value = root.get("launch")
    launch = None
    if launch_value is not None:
        launch_root = require_mapping(launch_value, f"{path}.launch")
        launch = LaunchConfiguration(
            exe_path=require_string(launch_root, "exePath", f"{path}.launch"),
            args_template=optional_str(launch_root.get("argsTemplate"), f"{path}.launch.argsTemplate"),
            working_directory=optional_str(launch_root.get("workingDirectory"), f"{path}.launch.workingDirectory"),
            dedupe_key_template=optional_str(launch_root.get("dedupeKeyTemplate"), f"{path}.launch.dedupeKeyTemplate"),
        )

    return AppDefinition(
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        display_name=require_string(root, "displayName", path),
        description=optional_str(root.get("description"), f"{path}.description"),
        capabilities=capabilities,
        launch=launch,
    )


def parse_app_instance(value: Any, *, path: str) -> AppInstance:
    """解析应用实例。"""

    root = require_mapping(value, path)
    invoke_root = require_mapping(root.get("invoke"), f"{path}.invoke")
    meta_value = root.get("meta")
    if meta_value is not None and not isinstance(meta_value, dict):
        raise RuntimeError(f"{path}.meta 必须为对象。")
    return AppInstance(
        instance_id=require_validated_string(root, "instanceId", path, validate_instance_id),
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        scope=optional_str(root.get("scope"), f"{path}.scope"),
        pid=require_positive_int(root, "pid", path),
        registered_at_utc=require_datetime(root, "registeredAtUtc", path),
        last_seen_utc=require_datetime(root, "lastSeenUtc", path),
        invoke=InvokeCapability(
            poll=require_bool(invoke_root, "poll", f"{path}.invoke"),
            respond=require_bool(invoke_root, "respond", f"{path}.invoke"),
        ),
        meta=meta_value,
    )


def parse_launch_result(value: Any, *, path: str) -> LaunchResult:
    """解析启动结果。"""

    root = require_mapping(value, path)
    ok = require_bool(root, "ok", path)
    if not ok:
        raise RuntimeError(f"{path} 返回结果非法。")
    status = require_str(root, "status", path)
    if status not in _LAUNCH_STATUS_VALUES:
        raise RuntimeError(f"{path}.status 取值非法。")
    pid = root.get("pid")
    if pid is not None and (not isinstance(pid, int) or isinstance(pid, bool)):
        raise RuntimeError(f"{path}.pid 类型非法。")
    return LaunchResult(
        ok=ok,
        status=status,
        launch_id=require_str(root, "launchId", path),
        pid=pid,
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
        value=root.get("value"),
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
        scope=optional_str(target_root.get("scope"), f"{path}.target.scope"),
        instance_id=optional_validated_string(
            target_root.get("instanceId"),
            f"{path}.target.instanceId",
            validate_optional_instance_id,
        ),
    )

    options_value = root.get("options")
    options = None
    if options_value is not None:
        options_root = require_mapping(options_value, f"{path}.options")
        options = InvocationOptions(
            ttl_ms=optional_int_at_least(options_root.get("ttlMs"), f"{path}.options.ttlMs", 1000),
            wait_timeout_ms=optional_int_at_least(options_root.get("waitTimeoutMs"), f"{path}.options.waitTimeoutMs", 1),
            queue_if_offline=optional_bool(options_root.get("queueIfOffline"), f"{path}.options.queueIfOffline"),
            auto_launch=optional_bool(options_root.get("autoLaunch"), f"{path}.options.autoLaunch"),
        )

    delivery_value = root.get("delivery")
    delivery = None
    if delivery_value is not None:
        delivery_root = require_mapping(delivery_value, f"{path}.delivery")
        delivery = InvocationDelivery(
            lease_seconds=require_positive_int(delivery_root, "leaseSeconds", f"{path}.delivery"),
            attempt=require_positive_int(delivery_root, "attempt", f"{path}.delivery"),
        )

    caller_root = require_mapping(root.get("caller"), f"{path}.caller")
    kind_value = require_str(root, "kind", path)
    try:
        kind = InvocationKind(kind_value)
    except ValueError as exc:
        raise RuntimeError(f"{path}.kind 非法。") from exc

    return Invocation(
        invocation_id=require_validated_string(root, "invocationId", path, validate_invocation_id),
        app_id=require_validated_string(root, "appId", path, validate_app_id),
        method=require_str(root, "method", path),
        kind=kind,
        created_at_utc=require_datetime(root, "createdAtUtc", path),
        caller=InvocationCaller(
            client_id=require_str(caller_root, "clientId", f"{path}.caller"),
            client_session_id=require_validated_string(
                caller_root,
                "clientSessionId",
                f"{path}.caller",
                validate_uuid_string,
            ),
        ),
        target=target,
        args=root.get("args"),
        options=options,
        delivery=delivery,
    )


def parse_event(value: Any, *, path: str) -> DevHubEvent:
    """解析事件通知。"""

    root = require_mapping(value, path)
    return DevHubEvent(
        subscription_id=require_str(root, "subscriptionId", path),
        type=require_str(root, "type", path),
        time_utc=require_datetime(root, "timeUtc", path),
        payload=root.get("payload"),
    )


def parse_callee_error(value: Any, *, path: str) -> DevHubCalleeError:
    """解析被调用方错误对象。"""

    root = require_mapping(value, path)
    return DevHubCalleeError(
        code=require_int(root, "code", path),
        message=require_str(root, "message", path),
        data=root.get("data"),
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


def require_mapping(value: Any, path: str) -> dict[str, Any]:
    """要求值必须为对象。"""

    if not isinstance(value, dict):
        raise RuntimeError(f"{path} 必须为对象。")
    return value


def require_str(root: Mapping[str, Any], name: str, path: str) -> str:
    """读取必填字符串属性。"""

    value = root.get(name)
    if not isinstance(value, str) or not value:
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def require_string(root: Mapping[str, Any], name: str, path: str) -> str:
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


def require_int(root: Mapping[str, Any], name: str, path: str) -> int:
    """读取必填整数属性。"""

    value = root.get(name)
    if not isinstance(value, int) or isinstance(value, bool):
        raise RuntimeError(f"{path}.{name} 类型非法。")
    return value


def require_positive_int(root: Mapping[str, Any], name: str, path: str) -> int:
    """璇诲彇蹇呭～姝ｆ暣鏁板睘鎬с€?"""

    value = require_int(root, name, path)
    if value < 1:
        raise RuntimeError(f"{path}.{name} 蹇呴』涓烘鏁般€?")
    return value


def optional_int(value: Any, path: str) -> int | None:
    """读取可选整数属性。"""

    if value is None:
        return None
    if not isinstance(value, int) or isinstance(value, bool):
        raise RuntimeError(f"{path} 类型非法。")
    return value


def optional_int_at_least(value: Any, path: str, minimum_value: int) -> int | None:
    """璇诲彇鍙€夋暣鏁板睘鎬э紝骞惰姹傚叾涓嶅皬浜庢寚瀹氫笅闄愩€?"""

    parsed = optional_int(value, path)
    if parsed is not None and parsed < minimum_value:
        raise RuntimeError(f"{path} 蹇呴』澶т簬绛変簬 {minimum_value}銆?")
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

    normalized = value.replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as exc:
        raise RuntimeError(f"{path} 类型非法。") from exc
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _validate_loopback_url(url: str, schemes: set[str], source: str, field_name: str) -> None:
    parsed = urlparse(url)
    if not url or url.endswith("/"):
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
    if parsed.scheme not in schemes or not parsed.hostname:
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
    if parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
        raise RuntimeError(f"hub.json.{field_name} 非法：{source}")
