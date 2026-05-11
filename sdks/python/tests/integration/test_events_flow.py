from __future__ import annotations

import asyncio
import threading

import pytest

from devhub_sdk import (
    APP_DEFINITION_DELETED,
    APP_DEFINITION_UPSERTED,
    APP_INSTANCE_REGISTERED,
    AppDefinition,
    AppInstanceRegistration,
    DevHubCalleeError,
    DevHubEvent,
    DevHubRpcErrorCode,
    DevHubRpcException,
    INVOCATION_COMPLETED,
    INVOCATION_DELIVERED,
    INVOCATION_FAILED,
    INVOCATION_QUEUED,
    InvokeCapability,
    InvokeRequest,
    InvocationTarget,
    ListDefinitionsRequest,
    ListInstancesRequest,
    PollRequest,
    RespondRequest,
    SDK_VERSION,
    VersionCompatibilityStatus,
)

from ._host import DevHubHostFixture


@pytest.mark.asyncio
async def test_ws_authenticate_subscribe_unsubscribe_should_control_delivery() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.flow.app", "displayName": "events.flow.app"})

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([APP_INSTANCE_REGISTERED])
            reader = events_client.read_events()
            event_task = asyncio.create_task(anext(reader))

            client = host.create_client("events-http-client")
            client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-inst-1",
                    app_id="events.flow.app",
                    pid=99999,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-inst-1"),
            )

            event = await asyncio.wait_for(event_task, timeout=3)
            assert event.subscription_id == subscription_id
            assert event.type == APP_INSTANCE_REGISTERED

            await events_client.unsubscribe(subscription_id)
            client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-inst-2",
                    app_id="events.flow.app",
                    pid=99998,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-inst-2"),
            )

            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(anext(reader), timeout=0.6)
            await reader.aclose()
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_ws_read_events_should_reject_concurrent_reader_and_allow_new_reader_after_close() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.concurrent-reader.app", "displayName": "events.concurrent-reader.app"})

        events_client = await host.create_events_client("events-concurrent-reader-client")
        try:
            await events_client.authenticate()
            await events_client.subscribe([APP_INSTANCE_REGISTERED])

            first_reader = events_client.read_events()
            first_event_task = asyncio.create_task(anext(first_reader))
            await asyncio.sleep(0)

            with pytest.raises(RuntimeError, match="活动读取器"):
                await anext(events_client.read_events())

            http_client = host.create_client("events-concurrent-reader-http-client")
            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-concurrent-reader-inst-1",
                    app_id="events.concurrent-reader.app",
                    pid=99992,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-concurrent-reader-inst-1"),
            )

            first_event = await asyncio.wait_for(first_event_task, timeout=3)
            await first_reader.aclose()

            second_reader = events_client.read_events()
            second_event_task = asyncio.create_task(anext(second_reader))
            await asyncio.sleep(0)

            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-concurrent-reader-inst-2",
                    app_id="events.concurrent-reader.app",
                    pid=99991,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-concurrent-reader-inst-2"),
            )

            second_event = await asyncio.wait_for(second_event_task, timeout=3)
            await second_reader.aclose()
        finally:
            await events_client.close()

    assert first_event.type == APP_INSTANCE_REGISTERED
    assert first_event.payload["instanceId"] == "events-concurrent-reader-inst-1"
    assert second_event.type == APP_INSTANCE_REGISTERED
    assert second_event.payload["instanceId"] == "events-concurrent-reader-inst-2"


@pytest.mark.asyncio
async def test_ws_subscribe_unknown_type_should_raise_value_error_before_request() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-invalid-client")
        try:
            await events_client.authenticate()

            with pytest.raises(ValueError, match="受支持的 DevHub 事件类型"):
                await events_client.subscribe(["unknown.type"])
        finally:
            await events_client.close()


@pytest.mark.asyncio
async def test_ws_disconnect_cleanup_should_require_resubscribe_after_reconnect() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.reconnect.app", "displayName": "events.reconnect.app"})

        first_client = await host.create_events_client("events-client-1")
        try:
            await first_client.authenticate()
            await first_client.subscribe([APP_INSTANCE_REGISTERED])
        finally:
            await first_client.close()

        second_client = await host.create_events_client("events-client-2")
        try:
            await second_client.authenticate()

            http_client = host.create_client("events-reconnect-http-client")
            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-reconnect-inst-1",
                    app_id="events.reconnect.app",
                    pid=99997,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-reconnect-inst-1"),
            )

            await second_client.subscribe([APP_INSTANCE_REGISTERED])

            http_client.register_instance(
                AppInstanceRegistration(
                    instance_id="events-reconnect-inst-2",
                    app_id="events.reconnect.app",
                    pid=99996,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("events-reconnect-inst-2"),
            )

            event = await asyncio.wait_for(anext(second_client.read_events()), timeout=2)
        finally:
            await second_client.close()

    assert event.type == APP_INSTANCE_REGISTERED
    assert event.payload["instanceId"] == "events-reconnect-inst-2"


@pytest.mark.asyncio
async def test_ws_readable_methods_should_match_published_surface() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.ws.read.app", "displayName": "events.ws.read.app"})

        client = host.create_client("events-http-client")
        client.register_instance(
            AppInstanceRegistration(
                instance_id="events-ws-read-inst-1",
                app_id="events.ws.read.app",
                pid=99995,
                invoke=InvokeCapability(poll=True, respond=True),
                scope="",
            ),
            _instance_password("events-ws-read-inst-1"),
        )

        events_client = await host.create_events_client("events-client")
        try:
            await events_client.authenticate()
            ping = await events_client.ping({"source": "ws"})
            definitions = await events_client.list_definitions(ListDefinitionsRequest(scope=None))
            definition = await events_client.get_definition("events.ws.read.app", "")
            instance = await events_client.get_instance("events-ws-read-inst-1")
            instances = await events_client.list_instances(ListInstancesRequest(scope=None))
        finally:
            await events_client.close()

    assert ping.ok is True
    assert any(item.app_id == "events.ws.read.app" for item in definitions)
    assert definition.app_id == "events.ws.read.app"
    assert definition.scope == ""
    assert instance.instance_id == "events-ws-read-inst-1"
    assert instance.instance_session_token is None
    assert any(item.instance_id == "events-ws-read-inst-1" for item in instances)


@pytest.mark.asyncio
async def test_ws_version_methods_should_use_rpc_or_runtime_fallback() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-version-client")
        try:
            await events_client.authenticate()

            rpc_version: str | None = None
            try:
                rpc_version = await events_client.get_host_version()
            except DevHubRpcException as exc:
                assert exc.code == DevHubRpcErrorCode.METHOD_NOT_FOUND

            compatibility = await events_client.check_version_compatibility()
        finally:
            await events_client.close()

    expected_host_version = rpc_version if rpc_version is not None else events_client.runtime.hub_version
    assert compatibility.sdk_version == SDK_VERSION
    assert compatibility.host_version == expected_host_version
    assert compatibility.status == _expected_version_status(SDK_VERSION, expected_host_version)


@pytest.mark.asyncio
async def test_ws_get_instance_missing_should_surface_instance_not_found() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-get-instance-client")
        try:
            await events_client.authenticate()

            with pytest.raises(DevHubRpcException) as exc_info:
                await events_client.get_instance("missing-events-inst")
        finally:
            await events_client.close()

    assert exc_info.value.code == DevHubRpcErrorCode.INSTANCE_NOT_FOUND
    assert exc_info.value.message == "instance_not_found"
    assert exc_info.value.reason == "unknown_instance"
    assert exc_info.value.try_get_data_string("instanceId") == "missing-events-inst"


@pytest.mark.asyncio
async def test_ws_should_receive_definition_lifecycle_events() -> None:
    with DevHubHostFixture.start() as host:
        events_client = await host.create_events_client("events-definition-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([APP_DEFINITION_UPSERTED, APP_DEFINITION_DELETED])
            reader = events_client.read_events()

            http_client = host.create_client("events-definition-http-client")
            http_client.upsert_definition(
                AppDefinition(
                    app_id="events.definition.app",
                    display_name="Events Definition App",
                    scope="",
                )
            )
            upserted = await asyncio.wait_for(anext(reader), timeout=3)

            http_client.delete_definition("events.definition.app", "")
            deleted = await asyncio.wait_for(anext(reader), timeout=3)
            await reader.aclose()
        finally:
            await events_client.close()

    assert upserted.subscription_id == subscription_id
    assert upserted.type == APP_DEFINITION_UPSERTED
    assert upserted.payload["appId"] == "events.definition.app"
    assert upserted.payload["scope"] == ""
    assert upserted.payload["definition"]["scope"] == ""
    assert upserted.payload["definition"]["displayName"] == "Events Definition App"
    assert deleted.subscription_id == subscription_id
    assert deleted.type == APP_DEFINITION_DELETED
    assert deleted.payload["appId"] == "events.definition.app"
    assert deleted.payload["scope"] == ""


@pytest.mark.asyncio
async def test_ws_should_receive_invocation_lifecycle_events() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.invocation.app", "displayName": "events.invocation.app"})

        events_client = await host.create_events_client("events-invocation-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([
                INVOCATION_QUEUED,
                INVOCATION_DELIVERED,
                INVOCATION_COMPLETED,
            ])
            reader = events_client.read_events()

            http_client = host.create_client("events-invocation-http-client")
            instance_session_token = _register_instance(
                http_client,
                app_id="events.invocation.app",
                instance_id="events-invocation-inst-1",
            )
            request_future = _call_request(http_client, app_id="events.invocation.app")

            queued = await _read_event_of_type(reader, INVOCATION_QUEUED)
            invocation = _wait_for_single_invocation(
                http_client,
                "events-invocation-inst-1",
                instance_session_token,
            )
            delivered = await _read_event_of_type(reader, INVOCATION_DELIVERED)

            http_client.respond(
                RespondRequest(
                    instance_id="events-invocation-inst-1",
                    instance_session_token=instance_session_token,
                    invocation_id=invocation.invocation_id,
                    lease_token=_lease_token(invocation),
                    value={"ok": True},
                )
            )
            request_result = request_future()
            completed = await _read_event_of_type(reader, INVOCATION_COMPLETED)
            await reader.aclose()
        finally:
            await events_client.close()

    assert request_result.invocation_id == invocation.invocation_id
    assert queued.subscription_id == subscription_id
    assert queued.payload["invocationId"] == invocation.invocation_id
    assert queued.payload["appId"] == "events.invocation.app"
    assert queued.payload["target"]["scope"] == ""
    assert queued.payload["method"] == "test.request"
    assert queued.payload["kind"] == "request"

    assert delivered.subscription_id == subscription_id
    assert delivered.payload["invocationId"] == invocation.invocation_id
    assert delivered.payload["appId"] == "events.invocation.app"
    assert delivered.payload["target"]["scope"] == ""
    assert delivered.payload["instanceId"] == "events-invocation-inst-1"
    assert delivered.payload["delivery"]["attempt"] == invocation.delivery.attempt
    assert "leaseToken" not in delivered.payload["delivery"]

    assert completed.subscription_id == subscription_id
    assert completed.payload["invocationId"] == invocation.invocation_id
    assert completed.payload["appId"] == "events.invocation.app"
    assert completed.payload["target"]["scope"] == ""
    assert completed.payload["instanceId"] == "events-invocation-inst-1"


@pytest.mark.asyncio
async def test_ws_should_receive_invocation_failed_event() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "events.invocation.failed.app", "displayName": "events.invocation.failed.app"})

        events_client = await host.create_events_client("events-invocation-failed-client")
        try:
            await events_client.authenticate()
            subscription_id = await events_client.subscribe([INVOCATION_FAILED])
            reader = events_client.read_events()

            http_client = host.create_client("events-invocation-failed-http-client")
            instance_session_token = _register_instance(
                http_client,
                app_id="events.invocation.failed.app",
                instance_id="events-invocation-failed-inst-1",
            )
            request_future = _call_request(http_client, app_id="events.invocation.failed.app")
            invocation = _wait_for_single_invocation(
                http_client,
                "events-invocation-failed-inst-1",
                instance_session_token,
            )

            http_client.respond(
                RespondRequest(
                    instance_id="events-invocation-failed-inst-1",
                    instance_session_token=instance_session_token,
                    invocation_id=invocation.invocation_id,
                    lease_token=_lease_token(invocation),
                    error=DevHubCalleeError.create(1001, "app_error", {"reason": "boom"}),
                )
            )
            failed = await _read_event_of_type(reader, INVOCATION_FAILED)
            await reader.aclose()
        finally:
            await events_client.close()

    request_error = request_future.expect_error()
    assert request_error.code == DevHubRpcErrorCode.INVOCATION_FAILED
    assert request_error.invocation_id == invocation.invocation_id
    assert failed.subscription_id == subscription_id
    assert failed.payload["invocationId"] == invocation.invocation_id
    assert failed.payload["appId"] == "events.invocation.failed.app"
    assert failed.payload["target"]["scope"] == ""
    assert failed.payload["instanceId"] == "events-invocation-failed-inst-1"
    assert failed.payload["reason"]


@pytest.mark.asyncio
async def test_two_hosts_with_different_data_dirs_should_isolate_event_streams() -> None:
    with DevHubHostFixture.start() as host_a, DevHubHostFixture.start() as host_b:
        host_a.write_definition({"appId": "parallel.events.app", "displayName": "parallel.events.app.a"})
        host_b.write_definition({"appId": "parallel.events.app", "displayName": "parallel.events.app.b"})

        events_client_a = await host_a.create_events_client("parallel-events-client-a")
        events_client_b = await host_b.create_events_client("parallel-events-client-b")
        try:
            await events_client_a.authenticate()
            subscription_id_a = await events_client_a.subscribe([APP_INSTANCE_REGISTERED])
            reader_a = events_client_a.read_events()

            await events_client_b.authenticate()
            subscription_id_b = await events_client_b.subscribe([APP_INSTANCE_REGISTERED])
            reader_b = events_client_b.read_events()

            http_client_a = host_a.create_client("parallel-events-http-a")
            http_client_b = host_b.create_client("parallel-events-http-b")

            http_client_a.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-a",
                    app_id="parallel.events.app",
                    pid=99994,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("parallel-events-inst-a"),
            )

            event_a = await asyncio.wait_for(anext(reader_a), timeout=3)

            pending_event_b = asyncio.create_task(anext(reader_b))
            with pytest.raises(asyncio.TimeoutError):
                await asyncio.wait_for(asyncio.shield(pending_event_b), timeout=0.6)

            http_client_b.register_instance(
                AppInstanceRegistration(
                    instance_id="parallel-events-inst-b",
                    app_id="parallel.events.app",
                    pid=99993,
                    invoke=InvokeCapability(poll=True, respond=True),
                    scope="",
                ),
                _instance_password("parallel-events-inst-b"),
            )

            event_b = await asyncio.wait_for(pending_event_b, timeout=3)
            await reader_a.aclose()
            await reader_b.aclose()
        finally:
            await events_client_a.close()
            await events_client_b.close()

    assert event_a.subscription_id == subscription_id_a
    assert event_a.payload["instanceId"] == "parallel-events-inst-a"
    assert event_b.subscription_id == subscription_id_b
    assert event_b.payload["instanceId"] == "parallel-events-inst-b"


def _instance_password(instance_id: str) -> str:
    return f"python-sdk-{instance_id}"


def _register_instance(client, *, app_id: str, instance_id: str) -> str:
    registered = client.register_instance(
        AppInstanceRegistration(
            instance_id=instance_id,
            app_id=app_id,
            pid=99990,
            invoke=InvokeCapability(poll=True, respond=True),
            scope="",
        ),
        _instance_password(instance_id),
    )
    if registered.instance_session_token is None:
        raise AssertionError(f"实例 {instance_id} 缺少 instance_session_token。")
    return registered.instance_session_token


class _RequestFuture:
    def __init__(self, client, app_id: str) -> None:
        self._holder = {}
        self._thread = threading.Thread(target=self._run, args=(client, app_id), daemon=True)
        self._thread.start()

    def _run(self, client, app_id: str) -> None:
        try:
            self._holder["result"] = client.request(
                InvokeRequest(
                    app_id=app_id,
                    method="test.request",
                    target=InvocationTarget(scope=""),
                )
            )
        except Exception as exc:  # noqa: BLE001
            self._holder["error"] = exc

    def __call__(self):
        self._thread.join(timeout=10)
        if self._thread.is_alive():
            raise TimeoutError("request 调用未在 10 秒内结束。")
        if "error" in self._holder:
            raise self._holder["error"]
        return self._holder["result"]

    def expect_error(self) -> DevHubRpcException:
        self._thread.join(timeout=10)
        if self._thread.is_alive():
            raise TimeoutError("request 调用未在 10 秒内结束。")
        error = self._holder.get("error")
        if not isinstance(error, DevHubRpcException):
            raise AssertionError(f"request 应返回 DevHubRpcException，实际为 {error!r}。")
        return error


def _call_request(client, *, app_id: str) -> _RequestFuture:
    return _RequestFuture(client, app_id)


def _wait_for_single_invocation(client, instance_id: str, instance_session_token: str, timeout_ms: int = 3000):
    import time

    deadline = time.time() + timeout_ms / 1000
    last_count = 0
    while time.time() < deadline:
        remaining_ms = max(0, int((deadline - time.time()) * 1000))
        poll_result = client.poll(
            PollRequest(
                instance_id=instance_id,
                instance_session_token=instance_session_token,
                wait_ms=min(remaining_ms, 250),
            )
        )
        last_count = len(poll_result.items)
        if last_count == 1:
            return poll_result.items[0]
        if last_count > 1:
            raise AssertionError("轮询结果返回了多条调用。")
    raise TimeoutError(f"在 {timeout_ms}ms 内未等到实例 {instance_id} 的单条调用，最后一轮返回 {last_count} 项。")


def _lease_token(invocation) -> str:
    if invocation.delivery is None:
        raise AssertionError("poll item 缺少 delivery。")
    return invocation.delivery.lease_token


async def _read_event_of_type(reader, event_type, timeout: float = 3) -> DevHubEvent:
    deadline = asyncio.get_running_loop().time() + timeout
    while True:
        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0:
            raise TimeoutError(f"未在 {timeout}s 内读取到 {event_type} 事件。")
        event = await asyncio.wait_for(anext(reader), timeout=remaining)
        if event.type == event_type:
            return event


def _expected_version_status(
    sdk_version: str,
    host_version: str | None,
) -> VersionCompatibilityStatus:
    sdk_parts = _parse_major_minor(sdk_version)
    host_parts = _parse_major_minor(host_version)
    if sdk_parts is None or host_parts is None:
        return VersionCompatibilityStatus.UNKNOWN
    if sdk_parts[0] != host_parts[0]:
        return VersionCompatibilityStatus.INCOMPATIBLE
    if sdk_parts[1] != host_parts[1]:
        return VersionCompatibilityStatus.UPDATE_RECOMMENDED
    return VersionCompatibilityStatus.COMPATIBLE


def _parse_major_minor(version: str | None) -> tuple[int, int] | None:
    if not isinstance(version, str):
        return None

    parts = version.split(".", 2)
    if len(parts) < 3 or not parts[0].isdigit() or not parts[1].isdigit():
        return None
    return int(parts[0]), int(parts[1])
