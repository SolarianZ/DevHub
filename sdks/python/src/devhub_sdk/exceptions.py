from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
from typing import Any

from ._validation import ensure_json_object
from .models import DevHubCalleeError


class DevHubRpcErrorCode(IntEnum):
    """DevHub 协议定义的已知 JSON-RPC 错误码。"""

    PARSE_ERROR = -32700
    INVALID_REQUEST = -32600
    METHOD_NOT_FOUND = -32601
    INVALID_PARAMS = -32602
    INTERNAL_ERROR = -32603
    UNAUTHORIZED = -32001
    FORBIDDEN = -32002
    INSTANCE_NOT_FOUND = -32010
    INVOCATION_EXPIRED = -32011
    INVOCATION_TIMEOUT = -32012
    APP_DEFINITION_NOT_FOUND = -32014
    LAUNCH_FAILED = -32020
    DELIVERY_CONFLICT = -32030
    RATE_LIMITED = -32040
    INVOCATION_FAILED = -32050
    NOT_SUPPORTED = -32099


_KNOWN_CODES = {code.value for code in DevHubRpcErrorCode}


@dataclass(slots=True)
class DevHubRpcException(Exception):
    """DevHub JSON-RPC 错误异常。"""

    code: int
    message: str
    data: Any
    request_id: str

    def __post_init__(self) -> None:
        super().__init__(self.message)

    @property
    def known_code(self) -> DevHubRpcErrorCode | None:
        """返回当前错误对应的已知错误码。"""

        if self.code not in _KNOWN_CODES:
            return None
        return DevHubRpcErrorCode(self.code)

    @property
    def reason(self) -> str | None:
        """返回 `error.data.reason`。"""

        if isinstance(self.data, dict):
            value = self.data.get("reason")
            return value if isinstance(value, str) else None
        return None

    @property
    def invocation_id(self) -> str | None:
        """返回 `error.data.invocationId`。"""

        if isinstance(self.data, dict):
            value = self.data.get("invocationId")
            return value if isinstance(value, str) else None
        return None

    @property
    def callee_error(self) -> DevHubCalleeError | None:
        """返回 `error.data.calleeError`。"""

        if not isinstance(self.data, dict):
            return None
        value = self.data.get("calleeError")
        if not isinstance(value, dict):
            return None
        code = value.get("code")
        message = value.get("message")
        if not isinstance(code, int) or isinstance(code, bool) or not isinstance(message, str) or not message:
            return None
        data = None
        if "data" in value:
            try:
                data = ensure_json_object(value["data"], "calleeError.data")
            except ValueError:
                return None
        return DevHubCalleeError(code=code, message=message, data=data)

    def is_code(self, error_code: DevHubRpcErrorCode | int) -> bool:
        """判断当前错误码是否与给定值一致。"""

        if isinstance(error_code, bool) or not isinstance(error_code, int):
            raise TypeError("error_code 必须为整数或 DevHubRpcErrorCode。")
        return self.code == int(error_code)

    def try_get_data_property(self, property_name: str) -> Any:
        """尝试读取 `error.data` 下的属性值。"""

        if not isinstance(property_name, str) or not property_name.strip():
            raise ValueError("property_name 不能为空。")
        if isinstance(self.data, dict) and property_name in self.data:
            return self.data[property_name]
        return None

    def try_get_data_string(self, property_name: str) -> str | None:
        """尝试读取 `error.data` 下的字符串属性。"""

        value = self.try_get_data_property(property_name)
        return value if isinstance(value, str) else None
