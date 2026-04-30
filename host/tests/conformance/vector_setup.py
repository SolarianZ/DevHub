#!/usr/bin/env python3
"""
DevHub conformance 向量 setup / cleanup 支撑。
"""

from __future__ import annotations

import copy
import json
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from tests.blackbox.test_base import (  # type: ignore  # noqa: E402
    DEFAULT_INSTANCE_PASSWORD,
    PENDING_WAIT_STATUS,
    TEST_HUB_ENV_JSON_ENV_VAR,
    RpcClient,
    poll_until_deadline_with_long_wait_status,
    start_isolated_hub_process,
    temporary_env_var,
)


SUITE_HOST_ENV_OVERRIDES = {
    "DEVHUB_ONLINE_THRESHOLD_SECONDS": "2",
    "DEVHUB_PENDING_INVOCATIONS_LIMIT": "16",
    "DEVHUB_TEST_RPC_FORCE_INTERNAL_ERROR_REQUEST_IDS": "http-internal-error",
}


@dataclass
class HostRuntimeContext:
    """隔离 Host 运行时上下文。"""

    data_dir: Path
    runtime_dir: Path
    hub_json_path: Path
    token_file: Path
    token: str
    hub_info: dict[str, Any]

    @property
    def http_base_url(self) -> str:
        return str(self.hub_info["httpBaseUrl"])

    @property
    def ws_url(self) -> str:
        return str(self.hub_info["wsUrl"])

    @property
    def apps_dir(self) -> Path:
        return self.data_dir / "apps"

    @property
    def definitions_catalog_path(self) -> Path:
        return self.apps_dir / "definitions.json"

    def create_rpc_client(self) -> RpcClient:
        return RpcClient(self.http_base_url, self.token)


@dataclass
class CleanupLedger:
    """记录每条向量创建的临时资产，便于统一清理。"""

    vector_temp_dir: Path
    definitions_catalog_was_modified: bool = False
    definitions_catalog_original_exists: bool = False
    definitions_catalog_original_content: str | None = None
    registered_instances: list[tuple[str, str]] = field(default_factory=list)
    instance_session_tokens: dict[str, str] = field(default_factory=dict)

    def cleanup(self, host_context: HostRuntimeContext) -> None:
        client = host_context.create_rpc_client()

        for instance_id, instance_session_token in reversed(self.registered_instances):
            try:
                client.unregister_instance(instance_id, instance_session_token=instance_session_token)
            except Exception:
                pass

        if self.definitions_catalog_was_modified:
            catalog_path = host_context.definitions_catalog_path
            try:
                if self.definitions_catalog_original_exists:
                    catalog_path.parent.mkdir(parents=True, exist_ok=True)
                    catalog_path.write_text(self.definitions_catalog_original_content or "", encoding="utf-8")
                else:
                    catalog_path.unlink()
                refresh_definitions_snapshot(host_context, request_id="cleanup-refresh-definitions")
            except FileNotFoundError:
                pass
            except Exception:
                pass

        shutil.rmtree(self.vector_temp_dir, ignore_errors=True)


@dataclass
class VectorExecutionContext:
    """单条向量执行上下文。"""

    source_path: Path
    resolved_vector: dict[str, Any]
    data_dir: Path
    environment_data_dir: Path | None
    context_path: Path
    cleanup_ledger: CleanupLedger

    def cleanup(self, host_context: HostRuntimeContext) -> None:
        self.cleanup_ledger.cleanup(host_context)


def start_suite_host(temp_root: Path) -> tuple[HostRuntimeContext, subprocess.Popen[str], Any]:
    """启动 suite 级隔离 Host，并注入 runner 需要的调优参数。"""

    host_data_dir = temp_root / "isolated-hub-data"
    host_data_dir.mkdir(parents=True, exist_ok=True)
    log_path = temp_root / "isolated-hub.log"
    log_file = open(log_path, "w+", encoding="utf-8")

    with temporary_env_var(TEST_HUB_ENV_JSON_ENV_VAR, json.dumps(SUITE_HOST_ENV_OVERRIDES, ensure_ascii=False)):
        process = start_isolated_hub_process(str(host_data_dir), log_file)

    try:
        host_context = wait_for_host_runtime(host_data_dir, process, log_file)
        return host_context, process, log_file
    except Exception:
        stop_process(process)
        log_file.close()
        raise


def wait_for_host_runtime(
    host_data_dir: Path,
    process: subprocess.Popen[str],
    log_file,
) -> HostRuntimeContext:
    """等待 Host 可用并返回运行时信息。"""

    runtime_dir = host_data_dir / "runtime"
    hub_json_path = runtime_dir / "hub.json"

    def wait_for_hub_json():
        if process.poll() is not None:
            raise RuntimeError(f"隔离 Hub 提前退出，日志片段：{read_log_tail(log_file)}")
        if hub_json_path.is_file():
            return None

        return PENDING_WAIT_STATUS

    hub_json_ready = poll_until_deadline_with_long_wait_status(
        label="等待 conformance 隔离 Host 生成 hub.json",
        timeout_seconds=45,
        poll_interval_seconds=0.2,
        poll_once=wait_for_hub_json,
        on_timeout=lambda: PENDING_WAIT_STATUS,
    )
    if hub_json_ready is PENDING_WAIT_STATUS:
        raise RuntimeError(f"等待隔离 Hub 生成 hub.json 超时，日志片段：{read_log_tail(log_file)}")

    hub_info = json.loads(hub_json_path.read_text(encoding="utf-8"))
    token_file = Path(hub_info["tokenFile"])
    if not token_file.is_file():
        raise FileNotFoundError(f"隔离 Hub 的 tokenFile 不存在：{token_file}")

    token = token_file.read_text(encoding="utf-8").strip()
    client = RpcClient(str(hub_info["httpBaseUrl"]), token)
    last_error = "unknown"

    def wait_for_ping():
        nonlocal last_error

        if process.poll() is not None:
            raise RuntimeError(f"隔离 Hub 在 ping 前退出，日志片段：{read_log_tail(log_file)}")
        try:
            response = client.call("hub.ping")
            if response.get("result", {}).get("ok") is True:
                return HostRuntimeContext(
                    data_dir=host_data_dir,
                    runtime_dir=runtime_dir,
                    hub_json_path=hub_json_path,
                    token_file=token_file,
                    token=token,
                    hub_info=hub_info,
                )
            last_error = json.dumps(response, ensure_ascii=False)
        except Exception as exc:  # noqa: BLE001
            last_error = str(exc)

        return PENDING_WAIT_STATUS

    host_context = poll_until_deadline_with_long_wait_status(
        label="等待 conformance 隔离 Host 的 hub.ping 可达",
        timeout_seconds=20,
        poll_interval_seconds=0.3,
        poll_once=wait_for_ping,
        on_timeout=lambda: PENDING_WAIT_STATUS,
    )
    if host_context is PENDING_WAIT_STATUS:
        raise RuntimeError(f"隔离 Hub 在超时时间内不可达：{last_error}；日志片段：{read_log_tail(log_file)}")

    return host_context


def read_log_tail(log_file, max_chars: int = 4000) -> str:
    """读取日志尾部，便于报错定位。"""

    try:
        log_file.flush()
        log_file.seek(0)
        content = log_file.read()
    except Exception:  # noqa: BLE001
        return ""

    content = content.strip()
    if len(content) > max_chars:
        return content[-max_chars:]
    return content


def stop_process(process: subprocess.Popen[str] | None) -> None:
    """停止隔离 Host 进程。"""

    if process is None or process.poll() is not None:
        return

    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def materialize_vector(
    vector_path: Path,
    vector: dict[str, Any],
    host_context: HostRuntimeContext,
    temp_root: Path,
    execution_name: str | None = None,
) -> VectorExecutionContext:
    """物化单条向量的 setup、上下文文件与 cleanup ledger。"""

    setup = require_optional_mapping(vector.get("setup"), "setup")
    validate_setup_shape(setup)

    vector_dir = temp_root / sanitize_file_name(str(vector["id"]))
    vector_temp_dir = vector_dir / sanitize_file_name(execution_name) if execution_name else vector_dir
    ledger = CleanupLedger(vector_temp_dir=vector_temp_dir)
    vector_temp_dir.mkdir(parents=True, exist_ok=True)

    try:
        data_dir = vector_temp_dir / "data"
        runtime_dir = data_dir / "runtime"
        token_file = copy_host_runtime(host_context, runtime_dir)

        placeholders = build_host_placeholders(host_context)
        placeholders.update(
            {
                "VECTOR_DATA_DIR": str(data_dir),
                "VECTOR_RUNTIME_DIR": str(runtime_dir),
                "VECTOR_TOKEN_FILE": str(token_file),
                "VECTOR_PYTHON_EXE": sys.executable or "python3",
            }
        )

        resolved_vector = substitute_placeholders(copy.deepcopy(vector), placeholders)
        resolved_setup = require_optional_mapping(resolved_vector.get("setup"), "setup")

        apply_data_dir_setup(data_dir, require_optional_mapping(resolved_setup.get("dataDir"), "setup.dataDir"))
        apply_definitions_setup(
            host_context,
            require_optional_list(resolved_setup.get("definitions"), "setup.definitions"),
            ledger,
        )
        apply_instances_setup(
            host_context,
            require_optional_list(resolved_setup.get("instances"), "setup.instances"),
            ledger,
        )
        placeholders.update(build_instance_session_token_placeholders(ledger.instance_session_tokens))
        resolved_vector = substitute_placeholders(resolved_vector, placeholders)

        request = resolved_vector.get("request")
        request_mapping = require_mapping(request, "request") if isinstance(request, dict) else {}
        environment_data_dir = data_dir if request_mapping.get("useEnvironmentDataDir") is True else None
        context_payload = {
            "vector": resolved_vector,
            "dataDir": str(data_dir),
            "environmentDataDir": str(environment_data_dir) if environment_data_dir else None,
        }

        context_path = vector_temp_dir / "execution-context.json"
        context_path.write_text(json.dumps(context_payload, ensure_ascii=False, indent=2), encoding="utf-8")

        return VectorExecutionContext(
            source_path=vector_path,
            resolved_vector=resolved_vector,
            data_dir=data_dir,
            environment_data_dir=environment_data_dir,
            context_path=context_path,
            cleanup_ledger=ledger,
        )
    except Exception:
        ledger.cleanup(host_context)
        raise


def copy_host_runtime(host_context: HostRuntimeContext, runtime_dir: Path) -> Path:
    """复制 Host runtime 发现文件到向量私有数据根目录。"""

    runtime_dir.mkdir(parents=True, exist_ok=True)
    token_file = runtime_dir / "token.txt"
    token_file.write_text(host_context.token, encoding="utf-8")

    hub_info = copy.deepcopy(host_context.hub_info)
    hub_info["tokenFile"] = str(token_file)
    (runtime_dir / "hub.json").write_text(
        json.dumps(hub_info, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return token_file


def build_host_placeholders(host_context: HostRuntimeContext) -> dict[str, str]:
    """构造 Host 运行时占位符。"""

    return {
        "HOST_DATA_DIR": str(host_context.data_dir),
        "HOST_APPS_DIR": str(host_context.apps_dir),
        "HOST_DEFINITIONS_CATALOG": str(host_context.definitions_catalog_path),
        "HOST_RUNTIME_DIR": str(host_context.runtime_dir),
        "HOST_HUB_JSON": str(host_context.hub_json_path),
        "HOST_TOKEN_FILE": str(host_context.token_file),
        "HOST_TOKEN": host_context.token,
        "HOST_HTTP_BASE_URL": host_context.http_base_url,
        "HOST_WS_URL": host_context.ws_url,
    }


def build_instance_session_token_placeholders(instance_session_tokens: dict[str, str]) -> dict[str, str]:
    """根据 setup 注册结果生成 instanceSessionToken 占位符。"""
    placeholders: dict[str, str] = {}

    if len(instance_session_tokens) == 1:
        placeholders["REGISTERED_INSTANCE_SESSION_TOKEN"] = next(iter(instance_session_tokens.values()))

    for instance_id, instance_session_token in instance_session_tokens.items():
        normalized_instance_id = re.sub(r"[^A-Z0-9]+", "_", instance_id.upper()).strip("_")
        placeholders[f"INSTANCE_SESSION_TOKEN_{normalized_instance_id}"] = instance_session_token

    return placeholders


def substitute_placeholders(value: Any, placeholders: dict[str, str]) -> Any:
    """递归替换向量中的 ${PLACEHOLDER}。"""

    if isinstance(value, str):
        return re.compile(r"\$\{([A-Z0-9_]+)\}").sub(
            lambda match: placeholders.get(match.group(1), match.group(0)),
            value,
        )
    if isinstance(value, list):
        return [substitute_placeholders(item, placeholders) for item in value]
    if isinstance(value, dict):
        return {key: substitute_placeholders(item, placeholders) for key, item in value.items()}
    return value


def validate_setup_shape(setup: dict[str, Any]) -> None:
    """校验 setup 扩展字段集合。"""

    allowed_keys = {"runtime", "definitions", "instances", "dataDir"}
    unknown_keys = sorted(key for key in setup if key not in allowed_keys)
    if unknown_keys:
        raise ValueError(f"setup 包含未支持字段：{unknown_keys}")

    runtime = require_optional_mapping(setup.get("runtime"), "setup.runtime")
    if runtime:
        unknown_runtime_keys = sorted(key for key in runtime if key != "mode")
        if unknown_runtime_keys:
            raise ValueError(f"setup.runtime 包含未支持字段：{unknown_runtime_keys}")

        mode = require_string(runtime.get("mode"), "setup.runtime.mode")
        if mode != "copy_host_runtime":
            raise ValueError(f"setup.runtime.mode 不支持：{mode}")

    data_dir = require_optional_mapping(setup.get("dataDir"), "setup.dataDir")
    if data_dir:
        unknown_data_dir_keys = sorted(key for key in data_dir if key != "files")
        if unknown_data_dir_keys:
            raise ValueError(f"setup.dataDir 包含未支持字段：{unknown_data_dir_keys}")

    definitions = require_optional_list(setup.get("definitions"), "setup.definitions")
    validate_definitions_setup(definitions)


def validate_definitions_setup(definitions: list[Any]) -> None:
    """校验 setup.definitions 的 catalog 条目模型。"""

    allowed_keys = {"appId", "scopeEntry", "rawScopeEntry", "appEntry", "rawAppEntry"}
    for index, item in enumerate(definitions):
        definition_setup = require_mapping(item, f"setup.definitions[{index}]")
        unknown_keys = sorted(key for key in definition_setup if key not in allowed_keys)
        if unknown_keys:
            raise ValueError(f"setup.definitions[{index}] 包含未支持字段：{unknown_keys}")

        has_app_id = "appId" in definition_setup
        has_scope_entry = "scopeEntry" in definition_setup
        has_raw_scope_entry = "rawScopeEntry" in definition_setup
        has_app_entry = "appEntry" in definition_setup
        has_raw_app_entry = "rawAppEntry" in definition_setup

        if has_app_id or has_scope_entry or has_raw_scope_entry:
            if not has_app_id:
                raise ValueError(f"setup.definitions[{index}].appId 缺失。")
            require_string(definition_setup.get("appId"), f"setup.definitions[{index}].appId")
            if has_scope_entry == has_raw_scope_entry:
                raise ValueError(
                    f"setup.definitions[{index}] 必须且只能提供 scopeEntry 或 rawScopeEntry 其中之一。"
                )
            if has_app_entry or has_raw_app_entry:
                raise ValueError(
                    f"setup.definitions[{index}] 使用 appId 预置 scope 条目时，不能同时提供 appEntry / rawAppEntry。"
                )
            read_catalog_setup_value(
                definition_setup.get("scopeEntry") if has_scope_entry else definition_setup.get("rawScopeEntry"),
                f"setup.definitions[{index}].{'scopeEntry' if has_scope_entry else 'rawScopeEntry'}",
                require_object=has_scope_entry,
            )
            continue

        if has_app_entry == has_raw_app_entry:
            raise ValueError(
                f"setup.definitions[{index}] 必须提供 appId + (scopeEntry/rawScopeEntry)，"
                "或提供 appEntry / rawAppEntry。"
            )
        read_catalog_setup_value(
            definition_setup.get("appEntry") if has_app_entry else definition_setup.get("rawAppEntry"),
            f"setup.definitions[{index}].{'appEntry' if has_app_entry else 'rawAppEntry'}",
            require_object=has_app_entry,
        )


def apply_data_dir_setup(data_dir: Path, data_dir_setup: dict[str, Any]) -> None:
    """将 setup.dataDir.files 物化到向量数据根目录。"""

    files = require_optional_list(data_dir_setup.get("files"), "setup.dataDir.files")
    for index, item in enumerate(files):
        file_setup = require_mapping(item, f"setup.dataDir.files[{index}]")
        relative_path = require_string(file_setup.get("path"), f"setup.dataDir.files[{index}].path")
        target_path = resolve_relative_path(data_dir, relative_path, f"setup.dataDir.files[{index}].path")
        content = build_file_content(file_setup, f"setup.dataDir.files[{index}]")
        target_path.parent.mkdir(parents=True, exist_ok=True)
        target_path.write_text(content, encoding="utf-8")


def apply_definitions_setup(
    host_context: HostRuntimeContext,
    definitions: list[Any],
    ledger: CleanupLedger,
) -> None:
    """将 setup.definitions 物化为 suite Host 的 definitions.json catalog。"""

    if not definitions:
        return

    capture_original_definitions_catalog(host_context, ledger)
    catalog = {
        "version": 1,
        "definitions": [],
    }
    app_entries_by_app_id: dict[str, dict[str, Any]] = {}

    for index, item in enumerate(definitions):
        definition_setup = require_mapping(item, f"setup.definitions[{index}]")
        if "appId" in definition_setup:
            app_id = require_string(definition_setup.get("appId"), f"setup.definitions[{index}].appId")
            scope_entry_field = "scopeEntry" if "scopeEntry" in definition_setup else "rawScopeEntry"
            scope_entry = read_catalog_setup_value(
                definition_setup.get(scope_entry_field),
                f"setup.definitions[{index}].{scope_entry_field}",
                require_object=scope_entry_field == "scopeEntry",
            )

            app_entry = app_entries_by_app_id.get(app_id)
            if app_entry is None:
                app_entry = {
                    "appId": app_id,
                    "scopes": [],
                }
                app_entries_by_app_id[app_id] = app_entry
                catalog["definitions"].append(app_entry)

            app_entry["scopes"].append(scope_entry)
            continue

        app_entry_field = "appEntry" if "appEntry" in definition_setup else "rawAppEntry"
        catalog["definitions"].append(
            read_catalog_setup_value(
                definition_setup.get(app_entry_field),
                f"setup.definitions[{index}].{app_entry_field}",
                require_object=app_entry_field == "appEntry",
            )
        )

    catalog_path = host_context.definitions_catalog_path
    catalog_path.parent.mkdir(parents=True, exist_ok=True)
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding="utf-8")
    refresh_definitions_snapshot(host_context, request_id="setup-refresh-definitions")


def capture_original_definitions_catalog(host_context: HostRuntimeContext, ledger: CleanupLedger) -> None:
    """记录 suite Host 当前 definitions.json，便于向量完成后恢复。"""

    if ledger.definitions_catalog_was_modified:
        return

    catalog_path = host_context.definitions_catalog_path
    if catalog_path.is_file():
        ledger.definitions_catalog_original_exists = True
        ledger.definitions_catalog_original_content = catalog_path.read_text(encoding="utf-8")
    else:
        ledger.definitions_catalog_original_exists = False
        ledger.definitions_catalog_original_content = None

    ledger.definitions_catalog_was_modified = True


def read_catalog_setup_value(value: Any, path: str, *, require_object: bool) -> Any:
    """读取 setup.definitions 中的 catalog 片段。"""

    parsed = value
    if isinstance(value, str):
        try:
            parsed = json.loads(value)
        except json.JSONDecodeError as exc:
            raise ValueError(f"{path} 必须是合法 JSON 文本。") from exc
    else:
        parsed = copy.deepcopy(value)

    if require_object and not isinstance(parsed, dict):
        raise ValueError(f"{path} 必须解析为对象。")

    return parsed


def refresh_definitions_snapshot(host_context: HostRuntimeContext, request_id: str) -> None:
    """通过 listDefinitions 触发 Host 刷新内存中的 Definition 快照。"""

    client = host_context.create_rpc_client()
    response = client.call(
        "hub.apps.listDefinitions",
        {
            "scope": None,
        },
        request_id=request_id,
    )
    error = response.get("error")
    if isinstance(error, dict):
        raise RuntimeError(
            "刷新 Definition 快照失败："
            f"{json.dumps(error, ensure_ascii=False, sort_keys=True)}"
        )


def apply_instances_setup(
    host_context: HostRuntimeContext,
    instances: list[Any],
    ledger: CleanupLedger,
) -> None:
    """按顺序执行 setup.instances。"""

    for index, item in enumerate(instances):
        instance_setup = require_mapping(item, f"setup.instances[{index}]")
        state = require_string(instance_setup.get("state"), f"setup.instances[{index}].state")
        instance = require_mapping(instance_setup.get("instance"), f"setup.instances[{index}].instance")
        password = require_optional_string(instance_setup.get("password"), f"setup.instances[{index}].password")

        if state not in {"registered", "offline"}:
            raise ValueError(f"setup.instances[{index}].state 不支持：{state}")

        register_instance(
            host_context,
            ledger,
            instance,
            password=password,
            request_id=f"setup-instance-{index}",
            error_path=f"setup.instances[{index}]",
        )
        if state == "offline":
            wait_seconds = require_positive_number(instance_setup.get("waitSeconds"), f"setup.instances[{index}].waitSeconds")
            time.sleep(wait_seconds)


def register_instance(
    host_context: HostRuntimeContext,
    ledger: CleanupLedger,
    instance: dict[str, Any],
    *,
    password: str | None = None,
    request_id: str,
    error_path: str,
) -> str:
    """注册实例并记录到清理账本。"""

    instance_id = require_string(instance.get("instanceId"), f"{error_path}.instance.instanceId")
    resolved_password = password or DEFAULT_INSTANCE_PASSWORD
    client = host_context.create_rpc_client()
    response = client.call(
        "hub.apps.registerInstance",
        {
            "password": resolved_password,
            "instance": instance,
        },
        request_id=request_id,
    )
    error = response.get("error")
    if isinstance(error, dict):
        raise RuntimeError(
            f"{error_path} 预置失败：{json.dumps(error, ensure_ascii=False, sort_keys=True)}"
        )

    result = response.get("result")
    if not isinstance(result, dict) or result.get("ok") is not True:
        raise RuntimeError(f"{error_path} 预置失败：响应缺少 result.ok=true。")

    instance_session_token = require_string(result.get("instanceSessionToken"), f"{error_path}.result.instanceSessionToken")
    ledger.registered_instances.append((instance_id, instance_session_token))
    ledger.instance_session_tokens[instance_id] = instance_session_token
    return instance_session_token


def build_file_content(
    file_setup: dict[str, Any],
    path: str,
    *,
    text_key: str = "text",
    json_key: str = "json",
) -> str:
    """从 text/json 或 rawText/definition 生成文件内容。"""

    has_text = text_key in file_setup
    has_json = json_key in file_setup
    if has_text == has_json:
        raise ValueError(f"{path} 必须且只能提供 {text_key} 或 {json_key} 其中之一。")

    if has_text:
        content = file_setup[text_key]
        if not isinstance(content, str):
            raise ValueError(f"{path}.{text_key} 必须为字符串。")
        return content

    return json.dumps(file_setup[json_key], ensure_ascii=False, indent=2)


def resolve_relative_path(root: Path, relative_path: str, field_path: str) -> Path:
    """解析并校验相对路径，防止越出 dataDir。"""

    candidate = Path(relative_path)
    if candidate.is_absolute():
        raise ValueError(f"{field_path} 必须为相对路径。")

    resolved_root = root.resolve()
    resolved_candidate = (root / candidate).resolve()
    if resolved_candidate != resolved_root and resolved_root not in resolved_candidate.parents:
        raise ValueError(f"{field_path} 不得越出 dataDir。")

    return resolved_candidate


def require_mapping(value: Any, path: str) -> dict[str, Any]:
    """断言值为对象。"""

    if not isinstance(value, dict):
        raise ValueError(f"{path} 必须为对象。")
    return value


def require_optional_mapping(value: Any, path: str) -> dict[str, Any]:
    """断言值为可选对象。"""

    if value is None:
        return {}
    return require_mapping(value, path)


def require_optional_list(value: Any, path: str) -> list[Any]:
    """断言值为可选数组。"""

    if value is None:
        return []
    if not isinstance(value, list):
        raise ValueError(f"{path} 必须为数组。")
    return value


def require_string(value: Any, path: str) -> str:
    """断言值为非空字符串。"""

    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{path} 必须为非空字符串。")
    return value


def require_optional_string(value: Any, path: str) -> str | None:
    """断言值为可选非空字符串。"""

    if value is None:
        return None
    return require_string(value, path)


def require_positive_number(value: Any, path: str) -> float:
    """断言值为正数。"""

    if not isinstance(value, (int, float)) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"{path} 必须为正数。")
    return float(value)


def sanitize_file_name(value: str) -> str:
    """生成安全的向量临时目录名。"""

    return re.sub(r"[^A-Za-z0-9._-]+", "_", value)
