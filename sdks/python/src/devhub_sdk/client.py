from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from . import _versioning
from ._http_transport import JsonRpcHttpTransport, UrllibJsonRpcHttpTransport
from .exceptions import DevHubRpcErrorCode, DevHubRpcException
from ._parsing import (
    parse_datetime,
    parse_definition_validation_result,
    parse_definition_result,
    parse_definitions_result,
    parse_host_version_result,
    parse_instance_result,
    parse_instances_result,
    parse_launch_result,
    parse_notify_result,
    parse_ping_result,
    parse_poll_result,
    parse_register_instance_result,
    parse_request_result,
    require_bool,
    require_mapping,
)
from ._payloads import (
    build_delete_definition_params,
    build_get_definition_params,
    build_get_instance_params,
    build_heartbeat_params,
    build_launch_params,
    build_list_definitions_params,
    build_list_instances_params,
    build_ping_params,
    build_notify_params,
    build_poll_params,
    build_register_instance_params,
    build_request_params,
    build_respond_params,
    build_upsert_definition_params,
    build_unregister_params,
    build_validate_definition_params,
)
from .models import (
    AppDefinition,
    AppInstance,
    AppInstanceRegistration,
    DefinitionValidationResult,
    DevHubClientOptions,
    HubRuntime,
    InvokeRequest,
    ListDefinitionsRequest,
    LaunchRequest,
    LaunchResult,
    ListInstancesRequest,
    NotifyResult,
    PingResult,
    PollRequest,
    PollResult,
    RequestResult,
    RespondRequest,
    RuntimeConnectionInfo,
    VersionCompatibilityResult,
)
from .runtime import FileSystemRuntimeResolver, RuntimeResolver, validate_runtime_connection_info


_ECHO_UNSET = object()


def _create_default_http_transport(
    options: DevHubClientOptions,
    connection_info: RuntimeConnectionInfo,
) -> JsonRpcHttpTransport:
    return UrllibJsonRpcHttpTransport(connection_info, options)


@dataclass(slots=True)
class DevHubClientDependencies:
    """创建 HTTP 客户端时可注入的依赖项。"""

    runtime_resolver: RuntimeResolver = field(default_factory=FileSystemRuntimeResolver)
    transport_factory: Callable[[DevHubClientOptions, RuntimeConnectionInfo], JsonRpcHttpTransport] = (
        _create_default_http_transport
    )

    def __post_init__(self) -> None:
        if self.runtime_resolver is None:
            raise ValueError("runtime_resolver 不能为空。")
        if not callable(self.transport_factory):
            raise TypeError("transport_factory 必须为可调用对象。")


class DevHubClient:
    """DevHub HTTP JSON-RPC 客户端。"""

    def __init__(
        self,
        options: DevHubClientOptions,
        connection_info: RuntimeConnectionInfo,
        transport: JsonRpcHttpTransport,
    ) -> None:
        """初始化客户端。"""

        self._options = options.clone()
        self._options.validate()
        self._connection_info = connection_info
        self._transport = transport
        self._closed = False

    @classmethod
    def from_runtime(
        cls,
        options: DevHubClientOptions,
        dependencies: DevHubClientDependencies | None = None,
    ) -> "DevHubClient":
        """根据数据根目录创建客户端。"""

        cloned_options = options.clone()
        cloned_options.validate()
        resolved_dependencies = dependencies or DevHubClientDependencies()
        connection_info = resolved_dependencies.runtime_resolver.resolve(cloned_options)
        validate_runtime_connection_info(connection_info)
        transport = resolved_dependencies.transport_factory(cloned_options, connection_info)
        return cls(cloned_options, connection_info, transport)

    @property
    def options(self) -> DevHubClientOptions:
        """返回当前客户端选项。"""

        return self._options.clone()

    @property
    def runtime(self) -> HubRuntime:
        """返回发现到的运行时信息。"""

        return self._connection_info.runtime

    def ping(self, echo: Any = _ECHO_UNSET) -> PingResult:
        """调用 `hub.ping`。"""

        params = build_ping_params() if echo is _ECHO_UNSET else build_ping_params(echo)
        result = self._send("hub.ping", params)
        return parse_ping_result(result, path="hub.ping.result")

    def get_host_version(self) -> str:
        """调用 `hub.getVersion` 并返回当前 Host 版本。"""

        result = self._send("hub.getVersion", None)
        return parse_host_version_result(result, path="hub.getVersion.result")

    def check_version_compatibility(self) -> VersionCompatibilityResult:
        """检查当前 SDK 与已连接 Host 的版本兼容性。"""

        try:
            host_version = self.get_host_version()
        except DevHubRpcException as exc:
            if not exc.is_code(DevHubRpcErrorCode.METHOD_NOT_FOUND):
                raise
            host_version = self.runtime.hub_version

        return _versioning.evaluate_version_compatibility(
            _versioning.get_sdk_version(),
            host_version,
        )

    def list_definitions(self, request: ListDefinitionsRequest) -> list[AppDefinition]:
        """调用 `hub.apps.listDefinitions`。"""

        result = self._send("hub.apps.listDefinitions", build_list_definitions_params(request))
        return parse_definitions_result(result, path="hub.apps.listDefinitions.result")

    def get_definition(self, app_id: str, scope: str) -> AppDefinition:
        """调用 `hub.apps.getDefinition`，按 `appId + scope` 精确读取 Definition。"""

        result = self._send("hub.apps.getDefinition", build_get_definition_params(app_id, scope))
        return parse_definition_result(result, path="hub.apps.getDefinition.result")

    def get_instance(self, instance_id: str) -> AppInstance:
        """调用 `hub.apps.getInstance`，按 `instanceId` 精确读取实例快照。"""

        result = self._send("hub.apps.getInstance", build_get_instance_params(instance_id))
        return parse_instance_result(result, path="hub.apps.getInstance.result")

    def validate_definition(self, definition: AppDefinition) -> DefinitionValidationResult:
        """调用 `hub.apps.validateDefinition`。"""

        result = self._send("hub.apps.validateDefinition", build_validate_definition_params(definition))
        return parse_definition_validation_result(result, path="hub.apps.validateDefinition.result")

    def upsert_definition(self, definition: AppDefinition) -> AppDefinition:
        """调用 `hub.apps.upsertDefinition`。"""

        result = self._send("hub.apps.upsertDefinition", build_upsert_definition_params(definition))
        return parse_definition_result(result, path="hub.apps.upsertDefinition.result")

    def delete_definition(self, app_id: str, scope: str) -> None:
        """调用 `hub.apps.deleteDefinition`，按 `appId + scope` 精确删除 Definition。"""

        result = self._send("hub.apps.deleteDefinition", build_delete_definition_params(app_id, scope))
        root = require_mapping(result, "hub.apps.deleteDefinition.result")
        if not require_bool(root, "ok", "hub.apps.deleteDefinition.result"):
            raise RuntimeError("hub.apps.deleteDefinition.result 返回结果非法。")

    def register_instance(
        self,
        instance: AppInstanceRegistration,
        password: str,
        launch_id: str | None = None,
    ) -> AppInstance:
        """调用 `hub.apps.registerInstance`。"""

        result = self._send("hub.apps.registerInstance", build_register_instance_params(instance, password, launch_id))
        return parse_register_instance_result(result, path="hub.apps.registerInstance.result")

    def heartbeat(self, instance_id: str, instance_session_token: str) -> datetime:
        """调用 `hub.apps.heartbeat`。"""

        result = self._send("hub.apps.heartbeat", build_heartbeat_params(instance_id, instance_session_token))
        root = require_mapping(result, "hub.apps.heartbeat.result")
        if not require_bool(root, "ok", "hub.apps.heartbeat.result"):
            raise RuntimeError("hub.apps.heartbeat.result 返回结果非法。")
        last_seen = root.get("lastSeenUtc")
        if not isinstance(last_seen, str):
            raise RuntimeError("hub.apps.heartbeat.result.lastSeenUtc 类型非法。")
        return parse_datetime(last_seen, "hub.apps.heartbeat.result.lastSeenUtc")

    def unregister_instance(self, instance_id: str, instance_session_token: str) -> None:
        """调用 `hub.apps.unregisterInstance`。"""

        result = self._send(
            "hub.apps.unregisterInstance",
            build_unregister_params(instance_id, instance_session_token),
        )
        root = require_mapping(result, "hub.apps.unregisterInstance.result")
        if not require_bool(root, "ok", "hub.apps.unregisterInstance.result"):
            raise RuntimeError("hub.apps.unregisterInstance.result 返回结果非法。")

    def list_instances(self, request: ListInstancesRequest) -> list[AppInstance]:
        """调用 `hub.apps.listInstances`。"""

        result = self._send("hub.apps.listInstances", build_list_instances_params(request))
        return parse_instances_result(result, path="hub.apps.listInstances.result")

    def launch(self, request: LaunchRequest) -> LaunchResult:
        """调用 `hub.apps.launch`。"""

        result = self._send("hub.apps.launch", build_launch_params(request))
        return parse_launch_result(result, path="hub.apps.launch.result")

    def notify(self, request: InvokeRequest) -> NotifyResult:
        """调用 `hub.invoke.notify`。"""

        result = self._send("hub.invoke.notify", build_notify_params(request))
        return parse_notify_result(result, path="hub.invoke.notify.result")

    def request(self, request: InvokeRequest) -> RequestResult:
        """调用 `hub.invoke.request`。"""

        result = self._send("hub.invoke.request", build_request_params(request))
        return parse_request_result(result, path="hub.invoke.request.result")

    def poll(self, request: PollRequest) -> PollResult:
        """调用 `hub.invoke.poll`。"""

        result = self._send("hub.invoke.poll", build_poll_params(request))
        return parse_poll_result(result, path="hub.invoke.poll.result")

    def respond(self, request: RespondRequest) -> None:
        """调用 `hub.invoke.respond`。"""

        result = self._send("hub.invoke.respond", build_respond_params(request))
        root = require_mapping(result, "hub.invoke.respond.result")
        if not require_bool(root, "ok", "hub.invoke.respond.result"):
            raise RuntimeError("hub.invoke.respond.result 返回结果非法。")

    def close(self) -> None:
        """关闭客户端。"""

        if self._closed:
            return

        self._closed = True
        self._transport.close()

    def __enter__(self) -> "DevHubClient":
        """上下文管理器入口。"""

        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        """上下文管理器出口。"""

        self.close()

    def _send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        self._ensure_not_closed()
        return self._transport.send(method, params)

    def _ensure_not_closed(self) -> None:
        if self._closed:
            raise RuntimeError("当前 HTTP 客户端已关闭。")
