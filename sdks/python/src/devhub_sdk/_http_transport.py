from __future__ import annotations

import json
from abc import ABC, abstractmethod
from typing import Any
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4

from ._json import load_json_text
from ._jsonrpc import validate_response_envelope
from .models import DevHubClientOptions, RuntimeConnectionInfo


class JsonRpcHttpTransport(ABC):
    """HTTP JSON-RPC 传输抽象。"""

    @abstractmethod
    def send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        """发送 JSON-RPC 请求并返回结果载荷。"""


class UrllibJsonRpcHttpTransport(JsonRpcHttpTransport):
    """基于 urllib 的默认 HTTP JSON-RPC 传输。"""

    def __init__(self, connection_info: RuntimeConnectionInfo, options: DevHubClientOptions) -> None:
        self._connection_info = connection_info
        self._options = options.clone()

    def send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        request_id = f"req-{uuid4().hex}"
        payload = {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
        }
        if params is not None:
            payload["params"] = params

        request = Request(
            self._connection_info.rpc_endpoint,
            data=json.dumps(payload, allow_nan=False).encode("utf-8"),
            headers={
                "Authorization": f"Bearer {self._connection_info.token}",
                "Content-Type": "application/json",
                "X-DevHub-Protocol": str(self._options.protocol_version),
                "X-DevHub-ClientId": self._options.client_id,
                "X-DevHub-ClientSessionId": self._options.client_session_id,
            },
            method="POST",
        )

        try:
            with urlopen(request, timeout=self._options.request_timeout) as response:
                body = response.read().decode("utf-8")
        except HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"HTTP 请求失败：{exc.code} {exc.reason}，响应体：{body}") from exc

        root = load_json_text(body, source="HTTP 响应体")
        return validate_response_envelope(root, request_id)
