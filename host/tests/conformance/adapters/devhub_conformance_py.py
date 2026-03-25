#!/usr/bin/env python3
"""
Python conformance 薄适配器。
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

import requests


REPO_ROOT = Path(__file__).resolve().parents[4]
PYTHON_SDK_ROOT = REPO_ROOT / "sdks" / "python" / "src"
if str(PYTHON_SDK_ROOT) not in sys.path:
    sys.path.insert(0, str(PYTHON_SDK_ROOT))

from devhub_sdk import (  # type: ignore  # noqa: E402
    DevHubClient,
    DevHubClientOptions,
    DevHubRpcException,
    InvocationOptions,
    InvocationTarget,
    InvokeRequest,
    discover_runtime,
)


def main() -> int:
    if len(sys.argv) != 2:
        emit(
            {
                "sdk": "python",
                "vectorId": None,
                "phase": None,
                "outcome": "error",
                "actual": None,
                "error": {"message": "用法错误：需要 execution-context.json 路径。"},
            }
        )
        return 0

    context_path = Path(sys.argv[1])
    try:
        context = json.loads(context_path.read_text(encoding="utf-8"))
        vector = context["vector"]
        request = vector["request"]
        kind = request.get("kind")
        if "expectedDiscovery" in vector:
            result = run_discovery(context)
        elif kind in {"sdk.notify", "sdk.request"}:
            result = run_invocation(context)
        else:
            result = run_rpc(context)
    except Exception as exc:  # noqa: BLE001
        result = {
            "sdk": "python",
            "vectorId": None,
            "phase": None,
            "outcome": "error",
            "actual": None,
            "error": {"message": str(exc)},
        }

    emit(result)
    return 0


def run_discovery(context: dict[str, Any]) -> dict[str, Any]:
    vector = context["vector"]
    request = vector["request"]
    explicit_data_dir = request.get("dataDir")
    environment_data_dir = context.get("environmentDataDir")
    original_env = os.environ.get("DEVHUB_DATA_DIR")

    try:
        if environment_data_dir:
            os.environ["DEVHUB_DATA_DIR"] = str(environment_data_dir)

        options = DevHubClientOptions(
            client_id=request.get("clientId") or "ConformanceDiscovery",
            data_dir=explicit_data_dir if explicit_data_dir else None,
        )
        connection = discover_runtime(options)
        actual = {
            "runtimeDirectory": connection.runtime_directory,
            "token": connection.token,
            "runtime": {
                "protocolVersion": connection.runtime.protocol_version,
                "httpBaseUrl": connection.runtime.http_base_url,
                "wsUrl": connection.runtime.ws_url,
                "tokenFile": connection.runtime.token_file,
            },
        }
        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "discovery",
            "outcome": "success",
            "actual": actual,
            "error": None,
        }
    except Exception as exc:  # noqa: BLE001
        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "discovery",
            "outcome": "error",
            "actual": normalize_discovery_error(explicit_data_dir, exc),
            "error": None,
        }
    finally:
        if original_env is None:
            os.environ.pop("DEVHUB_DATA_DIR", None)
        else:
            os.environ["DEVHUB_DATA_DIR"] = original_env


def run_invocation(context: dict[str, Any]) -> dict[str, Any]:
    vector = context["vector"]
    request = vector["request"]
    kind = request["kind"]
    operation = "notify" if kind == "sdk.notify" else "request"

    client = DevHubClient.from_runtime(
        DevHubClientOptions(
            client_id=request.get("clientId") or "ConformanceInvocation",
            data_dir=context["dataDir"],
        )
    )

    invoke_request = build_invoke_request(request.get("invokeRequest"))
    try:
        if operation == "notify":
            result = client.notify(invoke_request)
            actual = {
                "ok": result.ok,
                "invocationId": result.invocation_id,
            }
        else:
            result = client.request(invoke_request)
            actual = {
                "ok": result.ok,
                "invocationId": result.invocation_id,
                "value": result.value,
            }

        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "sdk-invocation",
            "operation": operation,
            "outcome": "success",
            "actual": actual,
            "error": None,
        }
    except DevHubRpcException as exc:
        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "sdk-invocation",
            "operation": operation,
            "outcome": "error",
            "actual": normalize_invocation_error(exc),
            "error": None,
        }


def run_rpc(context: dict[str, Any]) -> dict[str, Any]:
    vector = context["vector"]
    connection = discover_runtime(
        DevHubClientOptions(
            client_id="ConformanceHttpAdapter",
            data_dir=context["dataDir"],
        )
    )

    headers = dict(vector.get("http", {}).get("headers", {}))
    body = json.dumps(vector["request"], ensure_ascii=False, separators=(",", ":"))
    response = requests.post(
        f"{connection.runtime.http_base_url}/rpc",
        data=body.encode("utf-8"),
        headers=headers,
        timeout=30,
    )
    actual = json.loads(response.text)
    return {
        "sdk": "python",
        "vectorId": vector["id"],
        "phase": "rpc",
        "outcome": "success",
        "actual": actual,
        "error": None,
    }


def build_invoke_request(payload: Any) -> InvokeRequest:
    if not isinstance(payload, dict):
        raise ValueError("request.invokeRequest 必须为对象。")

    kwargs: dict[str, Any] = {
        "app_id": payload["appId"],
        "method": payload["method"],
    }

    if "target" in payload:
        target = payload["target"]
        if target is not None and not isinstance(target, dict):
            raise ValueError("request.invokeRequest.target 必须为对象或 null。")
        kwargs["target"] = None if target is None else InvocationTarget(
            scope=target.get("scope"),
            instance_id=target.get("instanceId"),
        )

    if "args" in payload:
        kwargs["args"] = payload["args"]

    if "options" in payload:
        options = payload["options"]
        if options is not None and not isinstance(options, dict):
            raise ValueError("request.invokeRequest.options 必须为对象或 null。")
        kwargs["options"] = None if options is None else InvocationOptions(
            ttl_ms=options.get("ttlMs"),
            wait_timeout_ms=options.get("waitTimeoutMs"),
            queue_if_offline=options.get("queueIfOffline"),
            auto_launch=options.get("autoLaunch"),
        )

    return InvokeRequest(**kwargs)


def normalize_invocation_error(exc: DevHubRpcException) -> dict[str, Any]:
    actual: dict[str, Any] = {
        "code": exc.code,
        "message": exc.message,
    }
    if exc.reason is not None:
        actual["reason"] = exc.reason
    if exc.invocation_id is not None:
        actual["invocationId"] = exc.invocation_id
    if exc.callee_error is not None:
        actual["calleeError"] = {
            "code": exc.callee_error.code,
            "message": exc.callee_error.message,
        }
        if exc.callee_error.data is not None:
            actual["calleeError"]["data"] = exc.callee_error.data
    return actual


def normalize_discovery_error(explicit_data_dir: str | None, exc: Exception) -> dict[str, Any]:
    reason = "discovery_failed"
    if explicit_data_dir and Path(explicit_data_dir).name.lower() == "runtime":
        reason = "runtime_subdirectory_rejected"

    return {
        "reason": reason,
        "message": str(exc),
    }


def emit(payload: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
    sys.stdout.write("\n")


if __name__ == "__main__":
    raise SystemExit(main())
