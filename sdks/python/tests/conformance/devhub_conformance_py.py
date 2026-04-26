#!/usr/bin/env python3
"""
Python conformance 薄适配器。
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path
from typing import Any

import requests
import websockets


SDK_ROOT = Path(__file__).resolve().parents[2]
PYTHON_SDK_ROOT = SDK_ROOT / "src"
if str(PYTHON_SDK_ROOT) not in sys.path:
    sys.path.insert(0, str(PYTHON_SDK_ROOT))

from devhub_sdk import (  # type: ignore  # noqa: E402
    AppCapabilities,
    AppDefinition,
    AppInstance,
    AppInstanceRegistration,
    DevHubClient,
    DevHubClientOptions,
    DevHubEventsClient,
    DevHubRpcException,
    InvocationOptions,
    InvocationTarget,
    InvokeCapability,
    InvokeRequest,
    LaunchConfiguration,
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
        kind = request.get("kind") if isinstance(request, dict) else None
        if "expectedDiscovery" in vector:
            result = run_discovery(context)
        elif kind in {"sdk.notify", "sdk.request"}:
            result = run_invocation(context)
        elif kind == "sdk.events":
            result = asyncio.run(run_events(context))
        elif kind == "raw.ws" or vector.get("transport") == "ws":
            result = asyncio.run(run_ws(context))
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
    body = normalize_raw_request_body(vector["request"])
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


async def run_events(context: dict[str, Any]) -> dict[str, Any]:
    vector = context["vector"]
    request = require_mapping(vector["request"], "request")
    steps = require_list(request.get("steps"), "request.steps")
    data_dir = str(context["dataDir"])
    raw_rpc_connection = discover_runtime(
        DevHubClientOptions(
            client_id="ConformanceRawDefinitionRpc",
            data_dir=data_dir,
        )
    )

    event_clients: dict[str, DevHubEventsClient] = {}
    event_iterators: dict[str, Any] = {}
    http_clients: dict[str, DevHubClient] = {}
    captures: dict[str, Any] = {}
    registered_instances: dict[str, tuple[str, str]] = {}

    try:
        for index, raw_step in enumerate(steps):
            step = require_mapping(raw_step, f"request.steps[{index}]")
            action = require_string(step.get("action"), f"request.steps[{index}].action")

            if action == "create_events_client":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                client_id = step.get("clientId") or request.get("clientId") or f"ConformanceEvents-{client_name}"
                event_clients[client_name] = await DevHubEventsClient.from_runtime(
                    DevHubClientOptions(client_id=str(client_id), data_dir=data_dir)
                )
                continue

            if action == "authenticate":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                await require_events_client(event_clients, client_name, index).authenticate()
                continue

            if action == "subscribe":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                types = step.get("types")
                subscription_id = await require_events_client(event_clients, client_name, index).subscribe(types)
                captures[capture_as] = subscription_id
                continue

            if action == "unsubscribe":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                subscription_id = resolve_capture_value(step, captures, index, "subscriptionId")
                await require_events_client(event_clients, client_name, index).unsubscribe(str(subscription_id))
                capture_as = step.get("captureAs")
                if capture_as is not None:
                    captures[require_string(capture_as, f"request.steps[{index}].captureAs")] = {"ok": True}
                continue

            if action == "create_http_client":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                client_id = step.get("clientId") or request.get("clientId") or f"ConformanceHttp-{client_name}"
                http_clients[client_name] = DevHubClient.from_runtime(
                    DevHubClientOptions(client_id=str(client_id), data_dir=data_dir)
                )
                continue

            if action == "register_instance":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                instance_payload = require_mapping(step.get("instance"), f"request.steps[{index}].instance")
                instance = build_app_instance_registration(instance_payload)
                password = resolve_password(
                    step,
                    captures,
                    index,
                    default=_default_instance_password(instance.instance_id),
                )
                registered = require_http_client(http_clients, client_name, index).register_instance(instance, password)
                instance_session_token = require_string(
                    registered.instance_session_token,
                    f"request.steps[{index}].registerInstance.instanceSessionToken",
                )
                registered_instances[instance.instance_id] = (client_name, instance_session_token)
                continue

            if action == "unregister_instance":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                instance_id = resolve_capture_value(step, captures, index, "instanceId")
                registered_token = registered_instances.get(str(instance_id), (client_name, ""))[1]
                instance_session_token = resolve_instance_session_token(
                    step,
                    captures,
                    index,
                    default=registered_token,
                )
                require_http_client(http_clients, client_name, index).unregister_instance(
                    str(instance_id),
                    instance_session_token,
                )
                registered_instances.pop(str(instance_id), None)
                continue

            if action == "validate_definition":
                definition_payload = require_mapping(step.get("definition"), f"request.steps[{index}].definition")
                result = read_raw_result(
                    send_raw_rpc(
                        raw_rpc_connection,
                        request_id=f"sdk-events-validate-definition-{index}",
                        method="hub.apps.validateDefinition",
                        params={"definition": definition_payload},
                    ),
                    path=f"request.steps[{index}]",
                )
                capture_as = step.get("captureAs")
                if capture_as is not None:
                    captures[require_string(capture_as, f"request.steps[{index}].captureAs")] = result
                continue

            if action == "upsert_definition":
                definition_payload = require_mapping(step.get("definition"), f"request.steps[{index}].definition")
                result = read_raw_result(
                    send_raw_rpc(
                        raw_rpc_connection,
                        request_id=f"sdk-events-upsert-definition-{index}",
                        method="hub.apps.upsertDefinition",
                        params={"definition": definition_payload},
                    ),
                    path=f"request.steps[{index}]",
                )
                capture_as = step.get("captureAs")
                if capture_as is not None:
                    captures[require_string(capture_as, f"request.steps[{index}].captureAs")] = require_mapping(
                        result.get("definition"),
                        f"request.steps[{index}].captureAs",
                    )
                continue

            if action == "delete_definition":
                read_raw_result(
                    send_raw_rpc(
                        raw_rpc_connection,
                        request_id=f"sdk-events-delete-definition-{index}",
                        method="hub.apps.deleteDefinition",
                        params=build_definition_identity_params(step, captures, index),
                    ),
                    path=f"request.steps[{index}]",
                )
                capture_as = step.get("captureAs")
                if capture_as is not None:
                    captures[require_string(capture_as, f"request.steps[{index}].captureAs")] = {"ok": True}
                continue

            if action == "get_definition":
                result = read_raw_result(
                    send_raw_rpc(
                        raw_rpc_connection,
                        request_id=f"sdk-events-get-definition-{index}",
                        method="hub.apps.getDefinition",
                        params=build_definition_identity_params(step, captures, index),
                    ),
                    path=f"request.steps[{index}]",
                )
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                captures[capture_as] = require_mapping(result.get("definition"), f"request.steps[{index}].captureAs")
                continue

            if action == "get_instance":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                instance_id = resolve_capture_value(step, captures, index, "instanceId")
                events_client = event_clients.get(client_name)
                if events_client is not None:
                    instance = await events_client.get_instance(str(instance_id))
                else:
                    instance = require_http_client(http_clients, client_name, index).get_instance(str(instance_id))
                captures[capture_as] = normalize_app_instance(instance)
                continue

            if action == "list_definitions":
                params: dict[str, Any] = {"scope": resolve_capture_value(step, captures, index, "scope")}
                app_id = resolve_capture_value(step, captures, index, "appId")
                if app_id is not None:
                    params["appId"] = require_string(app_id, f"request.steps[{index}].appId")
                result = read_raw_result(
                    send_raw_rpc(
                        raw_rpc_connection,
                        request_id=f"sdk-events-list-definitions-{index}",
                        method="hub.apps.listDefinitions",
                        params=params,
                    ),
                    path=f"request.steps[{index}]",
                )
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                captures[capture_as] = require_list(result.get("definitions"), f"request.steps[{index}].captureAs")
                continue

            if action == "read_event":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                timeout_ms = read_timeout_ms(step, index)
                iterator = event_iterators.get(client_name)
                if iterator is None:
                    iterator = require_events_client(event_clients, client_name, index).read_events().__aiter__()
                    event_iterators[client_name] = iterator
                event = await asyncio.wait_for(anext(iterator), timeout=timeout_ms / 1000)
                captures[capture_as] = normalize_event(event)
                continue

            if action == "expect_no_event":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                timeout_ms = read_timeout_ms(step, index)
                iterator = event_iterators.get(client_name)
                if iterator is None:
                    iterator = require_events_client(event_clients, client_name, index).read_events().__aiter__()
                    event_iterators[client_name] = iterator
                try:
                    event = await asyncio.wait_for(anext(iterator), timeout=timeout_ms / 1000)
                    captures[capture_as] = {
                        "status": "received",
                        "event": normalize_event(event),
                    }
                except asyncio.TimeoutError:
                    captures[capture_as] = {"status": "timeout"}
                continue

            if action == "close_events_client":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                client = event_clients.pop(client_name, None)
                event_iterators.pop(client_name, None)
                if client is not None:
                    await client.close()
                continue

            if action == "dispose_http_client":
                client_name = require_string(step.get("client"), f"request.steps[{index}].client")
                client = http_clients.pop(client_name, None)
                if client is not None:
                    client.close()
                continue

            if action == "sleep":
                timeout_ms = read_timeout_ms(step, index)
                await asyncio.sleep(timeout_ms / 1000)
                continue

            raise ValueError(f"request.steps[{index}].action 不支持：{action}")

        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "sdk-events",
            "outcome": "success",
            "actual": captures,
            "error": None,
        }
    finally:
        for instance_id, (client_name, instance_session_token) in reversed(list(registered_instances.items())):
            try:
                require_http_client(http_clients, client_name, -1).unregister_instance(instance_id, instance_session_token)
            except Exception:
                pass
        for client in http_clients.values():
            try:
                client.close()
            except Exception:
                pass
        for client in event_clients.values():
            try:
                await client.close()
            except Exception:
                pass


async def run_ws(context: dict[str, Any]) -> dict[str, Any]:
    vector = context["vector"]
    request = require_mapping(vector["request"], "request")
    steps = require_list(request.get("steps"), "request.steps")
    connection = discover_runtime(
        DevHubClientOptions(
            client_id=request.get("clientId") or "ConformanceWsAdapter",
            data_dir=context["dataDir"],
        )
    )

    captures: dict[str, Any] = {}
    websocket = await websockets.connect(connection.runtime.ws_url)
    try:
        for index, raw_step in enumerate(steps):
            step = require_mapping(raw_step, f"request.steps[{index}]")
            action = require_string(step.get("action"), f"request.steps[{index}].action")
            if action == "send":
                message = step.get("message")
                await websocket.send(normalize_raw_request_body(message))
                continue

            if action == "receive":
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                timeout_ms = read_timeout_ms(step, index)
                payload = await asyncio.wait_for(websocket.recv(), timeout=timeout_ms / 1000)
                captures[capture_as] = parse_ws_payload(payload)
                continue

            if action == "wait_closed":
                capture_as = require_string(step.get("captureAs"), f"request.steps[{index}].captureAs")
                timeout_ms = read_timeout_ms(step, index)
                try:
                    await asyncio.wait_for(websocket.wait_closed(), timeout=timeout_ms / 1000)
                    captures[capture_as] = {"closed": True}
                except asyncio.TimeoutError:
                    captures[capture_as] = {"closed": False}
                continue

            if action == "sleep":
                timeout_ms = read_timeout_ms(step, index)
                await asyncio.sleep(timeout_ms / 1000)
                continue

            raise ValueError(f"request.steps[{index}].action 不支持：{action}")

        return {
            "sdk": "python",
            "vectorId": vector["id"],
            "phase": "ws",
            "outcome": "success",
            "actual": captures,
            "error": None,
        }
    finally:
        try:
            await websocket.close()
        except Exception:
            pass


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


def build_app_instance_registration(payload: dict[str, Any]) -> AppInstanceRegistration:
    invoke = require_mapping(payload.get("invoke"), "instance.invoke")
    return AppInstanceRegistration(
        instance_id=require_string(payload.get("instanceId"), "instance.instanceId"),
        app_id=require_string(payload.get("appId"), "instance.appId"),
        scope=payload.get("scope"),
        pid=require_int(payload.get("pid"), "instance.pid"),
        invoke=InvokeCapability(
            poll=require_bool(invoke.get("poll"), "instance.invoke.poll"),
            respond=require_bool(invoke.get("respond"), "instance.invoke.respond"),
        ),
        meta=payload.get("meta"),
    )


def build_app_definition(payload: dict[str, Any]) -> AppDefinition:
    capabilities_payload = payload.get("capabilities")
    capabilities = None
    if capabilities_payload is not None:
        capabilities_root = require_mapping(capabilities_payload, "definition.capabilities")
        capabilities = AppCapabilities(
            rpc=capabilities_root.get("rpc"),
            events=capabilities_root.get("events"),
        )

    launch_payload = payload.get("launch")
    launch = None
    if launch_payload is not None:
        launch_root = require_mapping(launch_payload, "definition.launch")
        launch = LaunchConfiguration(
            exe_path=launch_root.get("exePath"),
            args_template=launch_root.get("argsTemplate"),
            working_directory=launch_root.get("workingDirectory"),
            dedupe_key_template=launch_root.get("dedupeKeyTemplate"),
        )

    return AppDefinition(
        app_id=require_string(payload.get("appId"), "definition.appId"),
        display_name=require_string(payload.get("displayName"), "definition.displayName"),
        scope=payload.get("scope"),
        description=payload.get("description"),
        capabilities=capabilities,
        launch=launch,
    )


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


def normalize_definition_validation_result(result) -> dict[str, Any]:
    return {
        "ok": result.ok,
        "valid": result.valid,
        "errors": [
            {
                "path": issue.path,
                "code": issue.code,
                "message": issue.message,
            }
            for issue in result.errors
        ],
    }


def normalize_app_definition(definition: AppDefinition) -> dict[str, Any]:
    actual: dict[str, Any] = {
        "appId": definition.app_id,
        "scope": definition.scope,
        "displayName": definition.display_name,
    }
    if definition.description is not None:
        actual["description"] = definition.description
    if definition.capabilities is not None:
        capabilities: dict[str, Any] = {}
        if definition.capabilities.rpc is not None:
            capabilities["rpc"] = definition.capabilities.rpc
        if definition.capabilities.events is not None:
            capabilities["events"] = definition.capabilities.events
        actual["capabilities"] = capabilities
    if definition.launch is not None:
        launch: dict[str, Any] = {"exePath": definition.launch.exe_path}
        if definition.launch.args_template is not None:
            launch["argsTemplate"] = definition.launch.args_template
        if definition.launch.working_directory is not None:
            launch["workingDirectory"] = definition.launch.working_directory
        if definition.launch.dedupe_key_template is not None:
            launch["dedupeKeyTemplate"] = definition.launch.dedupe_key_template
        actual["launch"] = launch
    return actual


def normalize_app_instance(instance: AppInstance) -> dict[str, Any]:
    actual: dict[str, Any] = {
        "instanceId": instance.instance_id,
        "appId": instance.app_id,
        "scope": instance.scope,
        "pid": instance.pid,
        "registeredAtUtc": instance.registered_at_utc.isoformat().replace("+00:00", "Z"),
        "lastSeenUtc": instance.last_seen_utc.isoformat().replace("+00:00", "Z"),
        "invoke": {
            "poll": instance.invoke.poll,
            "respond": instance.invoke.respond,
        },
    }
    if instance.meta is not None:
        actual["meta"] = instance.meta
    return actual


def send_raw_rpc(
    connection,
    *,
    request_id: str,
    method: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    response = requests.post(
        f"{connection.runtime.http_base_url}/rpc",
        data=json.dumps(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": method,
                "params": params,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {connection.token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "ConformanceRawDefinitionRpc",
            "X-DevHub-ClientSessionId": "00000000-0000-0000-0000-000000000099",
        },
        timeout=30,
    )
    return json.loads(response.text)


def read_raw_result(response: dict[str, Any], *, path: str) -> dict[str, Any]:
    if not isinstance(response, dict):
        raise ValueError(f"{path} 定义 RPC 响应非法。")

    if isinstance(response.get("error"), dict):
        raise ValueError(f"{path} 定义 RPC 返回错误：{json.dumps(response['error'], ensure_ascii=False)}")

    result = require_mapping(response.get("result"), f"{path}.result")
    if result.get("ok") is not True:
        raise ValueError(f"{path} 定义 RPC 缺少 result.ok=true。")

    return result


def normalize_event(event: Any) -> dict[str, Any]:
    return {
        "subscriptionId": event.subscription_id,
        "type": event.type,
        "payload": event.payload,
    }


def normalize_raw_request_body(request: Any) -> str:
    if isinstance(request, str):
        return request
    return json.dumps(request, ensure_ascii=False, separators=(",", ":"))


def parse_ws_payload(payload: Any) -> Any:
    if isinstance(payload, bytes):
        payload = payload.decode("utf-8")
    if not isinstance(payload, str):
        return payload
    try:
        return json.loads(payload)
    except json.JSONDecodeError:
        return payload


def resolve_capture_value(step: dict[str, Any], captures: dict[str, Any], index: int, field_name: str) -> Any:
    reference_field = f"{field_name}Ref"
    if reference_field in step:
        capture_key = require_string(step.get(reference_field), f"request.steps[{index}].{reference_field}")
        if capture_key not in captures:
            raise ValueError(f"request.steps[{index}].{reference_field} 引用不存在：{capture_key}")
        return captures[capture_key]

    return step.get(field_name)


def build_definition_identity_params(step: dict[str, Any], captures: dict[str, Any], index: int) -> dict[str, Any]:
    app_id = require_string(resolve_capture_value(step, captures, index, "appId"), f"request.steps[{index}].appId")
    return {
        "appId": app_id,
        "scope": resolve_capture_value(step, captures, index, "scope"),
    }


def resolve_password(
    step: dict[str, Any],
    captures: dict[str, Any],
    index: int,
    *,
    default: str,
) -> str:
    value = resolve_capture_value(step, captures, index, "password")
    if value is None:
        return default
    return require_string(value, f"request.steps[{index}].password")


def resolve_instance_session_token(
    step: dict[str, Any],
    captures: dict[str, Any],
    index: int,
    *,
    default: str,
) -> str:
    value = resolve_capture_value(step, captures, index, "instanceSessionToken")
    if value is None:
        if not default:
            raise ValueError(f"request.steps[{index}].instanceSessionToken 不能为空。")
        return default
    return require_string(value, f"request.steps[{index}].instanceSessionToken")


def _default_instance_password(instance_id: str) -> str:
    return f"conformance-{instance_id}"


def read_timeout_ms(step: dict[str, Any], index: int) -> int:
    value = step.get("timeoutMs", step.get("waitMs", 1000))
    return require_non_negative_int(value, f"request.steps[{index}].timeoutMs")


def require_events_client(clients: dict[str, DevHubEventsClient], client_name: str, index: int) -> DevHubEventsClient:
    client = clients.get(client_name)
    if client is None:
        raise ValueError(f"request.steps[{index}].client 引用的 events client 不存在：{client_name}")
    return client


def require_http_client(clients: dict[str, DevHubClient], client_name: str, index: int) -> DevHubClient:
    client = clients.get(client_name)
    if client is None:
        raise ValueError(f"request.steps[{index}].client 引用的 http client 不存在：{client_name}")
    return client


def require_mapping(value: Any, path: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{path} 必须为对象。")
    return value


def require_list(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        raise ValueError(f"{path} 必须为数组。")
    return value


def require_string(value: Any, path: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{path} 必须为非空字符串。")
    return value


def require_int(value: Any, path: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError(f"{path} 必须为整数。")
    return value


def require_bool(value: Any, path: str) -> bool:
    if not isinstance(value, bool):
        raise ValueError(f"{path} 必须为布尔值。")
    return value


def require_non_negative_int(value: Any, path: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValueError(f"{path} 必须为大于等于 0 的整数。")
    return value


def emit(payload: dict[str, Any]) -> None:
    encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    stdout_buffer = getattr(sys.stdout, "buffer", None)
    if stdout_buffer is not None:
        stdout_buffer.write(encoded)
        stdout_buffer.write(b"\n")
    else:
        sys.stdout.write(encoded.decode("utf-8"))
        sys.stdout.write("\n")
    sys.stdout.flush()


if __name__ == "__main__":
    raise SystemExit(main())
