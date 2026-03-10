from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .models import DevHubCalleeError


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
        if not isinstance(code, int) or not isinstance(message, str) or not message:
            return None
        return DevHubCalleeError(code=code, message=message, data=value.get("data"))
