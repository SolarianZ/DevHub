from __future__ import annotations

from typing import Any, Mapping

from ._validation import ensure_json_object
from .exceptions import DevHubRpcException


def validate_response_envelope(root: Any, request_id: str) -> dict[str, Any]:
    """校验 JSON-RPC 响应并返回 `result` 对象。"""

    if not isinstance(root, dict):
        raise RuntimeError("JSON-RPC 响应根必须为对象。")
    if root.get("jsonrpc") != "2.0":
        raise RuntimeError("JSON-RPC 响应的 jsonrpc 版本非法。")

    response_id = read_response_id(root)
    if response_id != request_id:
        raise RuntimeError("JSON-RPC 响应的 id 与请求不匹配。")

    has_result = "result" in root
    has_error = "error" in root and root.get("error") is not None
    if has_result == has_error:
        raise RuntimeError("JSON-RPC 响应必须且只能包含 result 或 error。")

    if has_error:
        error = root.get("error")
        if not isinstance(error, Mapping):
            raise RuntimeError("JSON-RPC error 对象非法。")
        code = error.get("code")
        message = error.get("message")
        if not isinstance(code, int) or isinstance(code, bool):
            raise RuntimeError("JSON-RPC error.code 非法。")
        if not isinstance(message, str) or not message:
            raise RuntimeError("JSON-RPC error.message 非法。")
        data = None
        if "data" in error:
            try:
                data = ensure_json_object(error["data"], "JSON-RPC error.data")
            except ValueError as exc:
                raise RuntimeError(str(exc)) from exc
        raise DevHubRpcException(
            code=code,
            message=message,
            data=data,
            request_id=request_id,
        )

    result = root.get("result")
    if not isinstance(result, dict):
        raise RuntimeError("JSON-RPC result 必须为对象。")

    return result


def read_response_id(root: Mapping[str, Any]) -> str:
    """读取 JSON-RPC 响应 id。"""

    if "id" not in root:
        raise RuntimeError("JSON-RPC 响应缺少 id 字段。")
    response_id = root["id"]
    if isinstance(response_id, str):
        return response_id
    if isinstance(response_id, int | float) and not isinstance(response_id, bool):
        return str(response_id)
    raise RuntimeError("JSON-RPC 响应的 id 类型非法。")
