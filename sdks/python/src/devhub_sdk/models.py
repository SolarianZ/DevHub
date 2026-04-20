from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from typing import Any
from uuid import uuid4

from .constants import DevHubEventType
from ._validation import (
    require_non_empty_string,
    require_positive_number,
    require_protocol_version,
    require_uuid_string,
)


@dataclass(slots=True)
class DevHubClientOptions:
    """DevHub 客户端选项。"""

    client_id: str
    client_session_id: str = field(default_factory=lambda: str(uuid4()))
    data_dir: str | None = None
    request_timeout: float | None = None
    protocol_version: int = 1

    def clone(self) -> "DevHubClientOptions":
        """复制当前选项。"""

        return DevHubClientOptions(
            client_id=self.client_id,
            client_session_id=self.client_session_id,
            data_dir=self.data_dir,
            request_timeout=self.request_timeout,
            protocol_version=self.protocol_version,
        )

    def validate(self) -> None:
        """校验选项合法性。"""

        require_non_empty_string(self.client_id, "client_id")
        require_uuid_string(self.client_session_id, "client_session_id")
        if self.data_dir is not None and not isinstance(self.data_dir, str):
            raise ValueError("data_dir 类型非法。")
        require_protocol_version(self.protocol_version)
        if self.request_timeout is not None:
            require_positive_number(self.request_timeout, "request_timeout")


@dataclass(slots=True)
class AppCapabilities:
    """应用能力声明。"""

    rpc: bool | None = None
    events: bool | None = None


@dataclass(slots=True)
class LaunchConfiguration:
    """应用启动配置。"""

    exe_path: str | None = None
    args_template: str | None = None
    working_directory: str | None = None
    dedupe_key_template: str | None = None


@dataclass(slots=True)
class AppDefinition:
    """应用定义。"""

    app_id: str
    display_name: str
    scope: str | None = None
    description: str | None = None
    capabilities: AppCapabilities | None = None
    launch: LaunchConfiguration | None = None


@dataclass(slots=True)
class ValidationIssue:
    """定义校验问题。"""

    path: str
    code: str
    message: str


@dataclass(slots=True)
class DefinitionValidationResult:
    """定义校验结果。"""

    ok: bool
    valid: bool
    errors: list[ValidationIssue]


@dataclass(slots=True)
class InvokeCapability:
    """实例调用能力。"""

    poll: bool
    respond: bool


@dataclass(slots=True)
class AppInstance:
    """应用实例。"""

    instance_id: str
    app_id: str
    scope: str | None
    pid: int
    registered_at_utc: datetime
    last_seen_utc: datetime
    invoke: InvokeCapability
    meta: dict[str, Any] | None = None


@dataclass(slots=True)
class AppInstanceRegistration:
    """实例注册载荷。"""

    instance_id: str
    app_id: str
    pid: int
    invoke: InvokeCapability
    scope: str | None = None
    meta: Any = None


@dataclass(slots=True)
class HubRuntimeTuning:
    """Hub 运行时调优参数。"""

    lease_seconds: int
    online_threshold_seconds: int
    launch_dedupe_window_seconds: int


@dataclass(slots=True)
class HubRuntime:
    """Hub 运行时发现文件模型。"""

    protocol_version: int
    pid: int
    http_base_url: str
    ws_url: str
    token_file: str
    started_at_utc: datetime
    runtime_tuning: HubRuntimeTuning
    hub_version: str | None = None


@dataclass(slots=True)
class RuntimeConnectionInfo:
    """运行时连接信息。"""

    runtime_directory: str
    token: str
    runtime: HubRuntime

    @property
    def rpc_endpoint(self) -> str:
        """返回 HTTP JSON-RPC 端点。"""

        return f"{self.runtime.http_base_url}/rpc"

    @property
    def websocket_endpoint(self) -> str:
        """返回 WebSocket 端点。"""

        return self.runtime.ws_url


@dataclass(slots=True)
class PingResult:
    """Ping 结果。"""

    ok: bool
    server_time_utc: datetime
    echo: Any = None


@dataclass(slots=True)
class LaunchRequest:
    """启动请求。"""

    app_id: str
    scope: str | None = None
    dedupe_key: str | None = None
    wait_for_register_ms: int | None = None


@dataclass(slots=True)
class LaunchResult:
    """启动结果。"""

    ok: bool
    status: str
    launch_id: str
    pid: int | None = None


@dataclass(slots=True)
class ListInstancesRequest:
    """实例列表请求。"""

    app_id: str | None = None
    scope: str | None = None
    include_offline: bool = False
    include_all_scopes: bool = False


@dataclass(slots=True)
class InvocationTarget:
    """调用目标。"""

    scope: str | None = None
    instance_id: str | None = None


@dataclass(slots=True)
class InvocationOptions:
    """调用选项。"""

    ttl_ms: int | None = None
    wait_timeout_ms: int | None = None
    queue_if_offline: bool | None = None
    auto_launch: bool | None = None


_INVOKE_ARGS_UNSET = object()


@dataclass(slots=True, init=False)
class InvokeRequest:
    """调用请求。"""

    app_id: str
    method: str
    target: InvocationTarget | None = None
    args: Any = None
    options: InvocationOptions | None = None
    _has_args: bool = field(init=False, repr=False, compare=False)

    def __init__(
        self,
        app_id: str,
        method: str,
        target: InvocationTarget | None = None,
        args: Any = _INVOKE_ARGS_UNSET,
        options: InvocationOptions | None = None,
    ) -> None:
        self.app_id = app_id
        self.method = method
        self.target = target
        self.args = None if args is _INVOKE_ARGS_UNSET else args
        self.options = options
        self._has_args = args is not _INVOKE_ARGS_UNSET


@dataclass(slots=True)
class NotifyResult:
    """通知结果。"""

    ok: bool
    invocation_id: str


@dataclass(slots=True)
class RequestResult:
    """请求结果。"""

    ok: bool
    invocation_id: str
    value: Any = None


@dataclass(slots=True)
class PollRequest:
    """轮询请求。"""

    instance_id: str
    max_count: int | None = None
    wait_ms: int | None = None


class InvocationKind(str, Enum):
    """调用类型。"""

    REQUEST = "request"
    NOTIFY = "notify"


@dataclass(slots=True)
class InvocationDelivery:
    """调用投递信息。"""

    lease_seconds: int
    attempt: int


@dataclass(slots=True)
class InvocationCaller:
    """调用方信息。"""

    client_id: str
    client_session_id: str


@dataclass(slots=True)
class Invocation:
    """调用对象。"""

    invocation_id: str
    app_id: str
    method: str
    kind: InvocationKind
    created_at_utc: datetime
    caller: InvocationCaller
    target: InvocationTarget | None = None
    args: Any = None
    options: InvocationOptions | None = None
    delivery: InvocationDelivery | None = None


@dataclass(slots=True)
class PollResult:
    """轮询结果。"""

    ok: bool
    server_time_utc: datetime
    items: list[Invocation]


@dataclass(slots=True)
class DevHubCalleeError:
    """被调用方错误对象。"""

    code: int
    message: str
    data: Any = None

    @staticmethod
    def create(code: int, message: str, data: Any = None) -> "DevHubCalleeError":
        """构造被调用方错误对象。"""

        return DevHubCalleeError(code=code, message=message, data=data)


_RESPOND_VALUE_UNSET = object()


@dataclass(slots=True, init=False)
class RespondRequest:
    """响应请求。"""

    instance_id: str
    invocation_id: str
    value: Any = None
    error: DevHubCalleeError | None = None
    _has_value: bool = field(init=False, repr=False, compare=False)

    def __init__(
        self,
        instance_id: str,
        invocation_id: str,
        value: Any = _RESPOND_VALUE_UNSET,
        error: DevHubCalleeError | None = None,
    ) -> None:
        self.instance_id = instance_id
        self.invocation_id = invocation_id
        self.value = None if value is _RESPOND_VALUE_UNSET else value
        self.error = error
        self._has_value = value is not _RESPOND_VALUE_UNSET


@dataclass(slots=True)
class DevHubEvent:
    """DevHub 事件对象。"""

    subscription_id: str
    type: DevHubEventType
    time_utc: datetime
    payload: Any = None
