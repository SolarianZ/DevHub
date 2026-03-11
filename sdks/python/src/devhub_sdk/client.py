from __future__ import annotations

from typing import Any

from ._http_transport import JsonRpcHttpTransport, UrllibJsonRpcHttpTransport
from ._parsing import (
    parse_app_definition,
    parse_app_instance,
    parse_datetime,
    parse_launch_result,
    parse_notify_result,
    parse_ping_result,
    parse_poll_result,
    parse_request_result,
    require_bool,
    require_mapping,
)
from ._payloads import (
    build_get_definition_params,
    build_heartbeat_params,
    build_launch_params,
    build_list_instances_params,
    build_notify_params,
    build_poll_params,
    build_register_instance_params,
    build_request_params,
    build_respond_params,
    build_unregister_params,
)
from .models import (
    AppDefinition,
    AppInstance,
    AppInstanceRegistration,
    DevHubClientOptions,
    HubRuntime,
    InvokeRequest,
    LaunchRequest,
    LaunchResult,
    ListInstancesRequest,
    NotifyResult,
    PingResult,
    PollRequest,
    PollResult,
    RequestResult,
    RespondRequest,
)
from .runtime import FileSystemRuntimeResolver, RuntimeResolver


_ECHO_UNSET = object()
_DEFAULT_RUNTIME_RESOLVER = FileSystemRuntimeResolver()
_DEFAULT_HTTP_TRANSPORT = UrllibJsonRpcHttpTransport()


class DevHubClient:
    """DevHub HTTP JSON-RPC 客户端。"""

    def __init__(
        self,
        options: DevHubClientOptions,
        *,
        runtime_resolver: RuntimeResolver | None = None,
        transport: JsonRpcHttpTransport | None = None,
    ) -> None:
        """初始化客户端。"""

        self._options = options.clone()
        self._options.validate()
        self._runtime_resolver = runtime_resolver or _DEFAULT_RUNTIME_RESOLVER
        self._transport = transport or _DEFAULT_HTTP_TRANSPORT
        self._connection_info = self._runtime_resolver.resolve(self._options)

    @classmethod
    def from_runtime(cls, options: DevHubClientOptions) -> "DevHubClient":
        """根据运行时目录创建客户端。"""

        return cls(options)

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

        params = None if echo is _ECHO_UNSET else {"echo": echo}
        result = self._send("hub.ping", params)
        return parse_ping_result(result, path="hub.ping.result")

    def list_definitions(self) -> list[AppDefinition]:
        """调用 `hub.apps.listDefinitions`。"""

        result = self._send("hub.apps.listDefinitions", None)
        root = require_mapping(result, "hub.apps.listDefinitions.result")
        if not require_bool(root, "ok", "hub.apps.listDefinitions.result"):
            raise RuntimeError("hub.apps.listDefinitions.result 返回结果非法。")
        definitions = root.get("definitions")
        if not isinstance(definitions, list):
            raise RuntimeError("hub.apps.listDefinitions.result.definitions 必须为数组。")
        return [
            parse_app_definition(item, path=f"hub.apps.listDefinitions.result.definitions[{index}]")
            for index, item in enumerate(definitions)
        ]

    def get_definition(self, app_id: str) -> AppDefinition:
        """调用 `hub.apps.getDefinition`。"""

        result = self._send("hub.apps.getDefinition", build_get_definition_params(app_id))
        root = require_mapping(result, "hub.apps.getDefinition.result")
        if not require_bool(root, "ok", "hub.apps.getDefinition.result"):
            raise RuntimeError("hub.apps.getDefinition.result 返回结果非法。")
        return parse_app_definition(root.get("definition"), path="hub.apps.getDefinition.result.definition")

    def register_instance(self, instance: AppInstanceRegistration) -> AppInstance:
        """调用 `hub.apps.registerInstance`。"""

        result = self._send("hub.apps.registerInstance", build_register_instance_params(instance))
        root = require_mapping(result, "hub.apps.registerInstance.result")
        if not require_bool(root, "ok", "hub.apps.registerInstance.result"):
            raise RuntimeError("hub.apps.registerInstance.result 返回结果非法。")
        return parse_app_instance(root.get("instance"), path="hub.apps.registerInstance.result.instance")

    def heartbeat(self, instance_id: str):
        """调用 `hub.apps.heartbeat`。"""

        result = self._send("hub.apps.heartbeat", build_heartbeat_params(instance_id))
        root = require_mapping(result, "hub.apps.heartbeat.result")
        if not require_bool(root, "ok", "hub.apps.heartbeat.result"):
            raise RuntimeError("hub.apps.heartbeat.result 返回结果非法。")
        last_seen = root.get("lastSeenUtc")
        if not isinstance(last_seen, str):
            raise RuntimeError("hub.apps.heartbeat.result.lastSeenUtc 类型非法。")
        return parse_datetime(last_seen, "hub.apps.heartbeat.result.lastSeenUtc")

    def unregister_instance(self, instance_id: str) -> None:
        """调用 `hub.apps.unregisterInstance`。"""

        result = self._send("hub.apps.unregisterInstance", build_unregister_params(instance_id))
        root = require_mapping(result, "hub.apps.unregisterInstance.result")
        if not require_bool(root, "ok", "hub.apps.unregisterInstance.result"):
            raise RuntimeError("hub.apps.unregisterInstance.result 返回结果非法。")

    def list_instances(self, request: ListInstancesRequest | None = None) -> list[AppInstance]:
        """调用 `hub.apps.listInstances`。"""

        result = self._send("hub.apps.listInstances", build_list_instances_params(request))
        root = require_mapping(result, "hub.apps.listInstances.result")
        if not require_bool(root, "ok", "hub.apps.listInstances.result"):
            raise RuntimeError("hub.apps.listInstances.result 返回结果非法。")
        instances = root.get("instances")
        if not isinstance(instances, list):
            raise RuntimeError("hub.apps.listInstances.result.instances 必须为数组。")
        return [
            parse_app_instance(item, path=f"hub.apps.listInstances.result.instances[{index}]")
            for index, item in enumerate(instances)
        ]

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

    def __enter__(self) -> "DevHubClient":
        """上下文管理器入口。"""

        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        """上下文管理器出口。"""

        self.close()

    def _send(self, method: str, params: dict[str, Any] | None) -> dict[str, Any]:
        return self._transport.send(self._connection_info, self._options, method, params)
