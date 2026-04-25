from __future__ import annotations

import time

import pytest

from devhub_sdk import (
    AppInstanceRegistration,
    DevHubCalleeError,
    DevHubRpcErrorCode,
    DevHubRpcException,
    InvokeCapability,
    InvokeRequest,
    InvocationKind,
    InvocationOptions,
    InvocationTarget,
    PollRequest,
    RespondRequest,
)

from ._host import DevHubHostFixture


def test_notify_and_poll_should_round_trip() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.notify.app", "displayName": "invoke.notify.app"})
        client = host.create_client("invoke-notify-client")
        instance_session_token = _register_instance(client, "invoke.notify.app", "notify-inst-1", "")

        notify_result = client.notify(
            InvokeRequest(
                app_id="invoke.notify.app",
                method="test.notify",
                target=InvocationTarget(scope=""),
                args={"message": "hello"},
            )
        )

        invocation = _wait_for_single_invocation(client, "notify-inst-1", instance_session_token)
        assert notify_result.ok is True
        assert notify_result.invocation_id == invocation.invocation_id
        assert invocation.kind == InvocationKind.NOTIFY
        assert invocation.args["message"] == "hello"


def test_request_respond_value_should_return_result_and_second_respond_should_conflict() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.request.app", "displayName": "invoke.request.app"})
        client = host.create_client("invoke-request-client")
        instance_session_token = _register_instance(client, "invoke.request.app", "request-inst-1", "")

        request_future = _call_request(client)
        invocation = _wait_for_single_invocation(client, "request-inst-1", instance_session_token)

        client.respond(
            RespondRequest(
                instance_id="request-inst-1",
                instance_session_token=instance_session_token,
                invocation_id=invocation.invocation_id,
                value={"ok": True, "value": 2},
            )
        )

        request_result = request_future()
        assert request_result.ok is True
        assert request_result.value["value"] == 2

        with pytest.raises(DevHubRpcException) as exc_info:
            client.respond(
                RespondRequest(
                    instance_id="request-inst-1",
                    instance_session_token=instance_session_token,
                    invocation_id=invocation.invocation_id,
                    value={"ok": True},
                )
            )
        assert exc_info.value.code == -32030


def test_poll_and_respond_with_wrong_instance_session_token_should_surface_forbidden_reason() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.token.app", "displayName": "invoke.token.app"})
        client = host.create_client("invoke-token-client")
        instance_session_token = _register_instance(client, "invoke.token.app", "token-inst-1", "")

        client.notify(
            InvokeRequest(
                app_id="invoke.token.app",
                method="test.notify",
                target=InvocationTarget(scope=""),
            )
        )

        with pytest.raises(DevHubRpcException) as poll_error:
            client.poll(
                PollRequest(
                    instance_id="token-inst-1",
                    instance_session_token="wrong-token",
                    wait_ms=0,
                )
            )
        assert poll_error.value.code == DevHubRpcErrorCode.FORBIDDEN
        assert poll_error.value.reason == "instance_session_token_mismatch"

        first_invocation = _wait_for_single_invocation(client, "token-inst-1", instance_session_token)
        assert first_invocation.method == "test.notify"

        request_future = _call_request(client, app_id="invoke.token.app")
        request_invocation = _wait_for_single_invocation(client, "token-inst-1", instance_session_token)

        with pytest.raises(DevHubRpcException) as respond_error:
            client.respond(
                RespondRequest(
                    instance_id="token-inst-1",
                    instance_session_token="wrong-token",
                    invocation_id=request_invocation.invocation_id,
                    value={"ok": True},
                )
            )
        assert respond_error.value.code == DevHubRpcErrorCode.FORBIDDEN
        assert respond_error.value.reason == "instance_session_token_mismatch"

        client.respond(
            RespondRequest(
                instance_id="token-inst-1",
                instance_session_token=instance_session_token,
                invocation_id=request_invocation.invocation_id,
                value={"ok": True},
            )
        )
        assert request_future().value["ok"] is True


def test_request_respond_error_should_map_invocation_failed() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.error.app", "displayName": "invoke.error.app"})
        client = host.create_client("invoke-error-client")
        instance_session_token = _register_instance(client, "invoke.error.app", "error-inst-1", "")

        def send_request():
            return client.request(
                InvokeRequest(
                    app_id="invoke.error.app",
                    method="test.request",
                    target=InvocationTarget(scope=""),
                    options=None,
                )
            )

        invocation = None
        exception = None
        import threading

        result_holder = {}

        def run_request() -> None:
            try:
                result_holder["result"] = send_request()
            except Exception as exc:  # noqa: BLE001
                result_holder["error"] = exc

        thread = threading.Thread(target=run_request, daemon=True)
        thread.start()
        invocation = _wait_for_single_invocation(client, "error-inst-1", instance_session_token)
        client.respond(
            RespondRequest(
                instance_id="error-inst-1",
                instance_session_token=instance_session_token,
                invocation_id=invocation.invocation_id,
                error=DevHubCalleeError.create(1001, "app_error", {"reason": "boom"}),
            )
        )
        thread.join(timeout=10)
        exception = result_holder.get("error")

        assert isinstance(exception, DevHubRpcException)
        assert exception.code == -32050
        assert exception.invocation_id == invocation.invocation_id
        assert exception.callee_error is not None
        assert exception.callee_error.code == 1001


def test_request_timeout_and_expired_should_map_expected_error_codes() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.timeout.app", "displayName": "invoke.timeout.app"})
        client = host.create_client("invoke-timeout-client")
        _register_instance(client, "invoke.timeout.app", "timeout-inst-1", "")

        with pytest.raises(DevHubRpcException) as timeout_exc_info:
            client.request(
                InvokeRequest(
                    app_id="invoke.timeout.app",
                    method="test.timeout",
                    target=InvocationTarget(scope=""),
                    options=InvocationOptions(ttl_ms=1500, wait_timeout_ms=1000),
                )
            )
        assert timeout_exc_info.value.code == DevHubRpcErrorCode.INVOCATION_TIMEOUT

        with pytest.raises(DevHubRpcException) as expired_exc_info:
            client.request(
                InvokeRequest(
                    app_id="invoke.timeout.app",
                    method="test.expired",
                    target=InvocationTarget(scope=""),
                    options=InvocationOptions(ttl_ms=1000, wait_timeout_ms=1000),
                )
            )
        assert expired_exc_info.value.code == DevHubRpcErrorCode.INVOCATION_EXPIRED


def test_scope_routing_should_hit_expected_instance() -> None:
    with DevHubHostFixture.start() as host:
        host.write_definition({"appId": "invoke.scope.app", "displayName": "invoke.scope.app"})
        host.write_definition({"appId": "invoke.scope.app", "scope": "scope-a", "displayName": "invoke.scope.app.scope-a"})
        host.write_definition({"appId": "invoke.scope.app", "scope": "global", "displayName": "invoke.scope.app.literal-global"})
        client = host.create_client("invoke-scope-client")
        scope_global_token = _register_instance(client, "invoke.scope.app", "scope-global-inst", "")
        scope_a_token = _register_instance(client, "invoke.scope.app", "scope-a-inst", "scope-a")
        scope_literal_global_token = _register_instance(client, "invoke.scope.app", "scope-literal-global-inst", "global")

        client.notify(
            InvokeRequest(
                app_id="invoke.scope.app",
                method="test.default-global",
                target=InvocationTarget(scope=""),
            )
        )
        assert _wait_for_single_invocation(client, "scope-global-inst", scope_global_token).method == "test.default-global"
        assert client.poll(
            PollRequest(
                instance_id="scope-a-inst",
                instance_session_token=scope_a_token,
                wait_ms=0,
            )
        ).items == []

        client.notify(
            InvokeRequest(
                app_id="invoke.scope.app",
                method="test.scope-a",
                target=InvocationTarget(scope="scope-a"),
            )
        )
        assert _wait_for_single_invocation(client, "scope-a-inst", scope_a_token).method == "test.scope-a"

        client.notify(
            InvokeRequest(
                app_id="invoke.scope.app",
                method="test.empty-scope",
                target=InvocationTarget(scope=""),
            )
        )
        assert _wait_for_single_invocation(client, "scope-global-inst", scope_global_token).method == "test.empty-scope"

        client.notify(
            InvokeRequest(
                app_id="invoke.scope.app",
                method="test.literal-global",
                target=InvocationTarget(scope="global"),
            )
        )
        assert _wait_for_single_invocation(
            client,
            "scope-literal-global-inst",
            scope_literal_global_token,
        ).method == "test.literal-global"


def _create_instance(app_id: str, instance_id: str, scope: str) -> AppInstanceRegistration:
    return AppInstanceRegistration(
        instance_id=instance_id,
        app_id=app_id,
        scope=scope,
        pid=99999,
        invoke=InvokeCapability(poll=True, respond=True),
    )


def _register_instance(client, app_id: str, instance_id: str, scope: str) -> str:
    registered = client.register_instance(_create_instance(app_id, instance_id, scope), _instance_password(instance_id))
    if registered.instance_session_token is None:
        raise AssertionError(f"实例 {instance_id} 缺少 instance_session_token。")
    return registered.instance_session_token


def _instance_password(instance_id: str) -> str:
    return f"python-sdk-{instance_id}"


def _call_request(client, app_id: str = "invoke.request.app"):
    result_holder = {}
    import threading

    def worker() -> None:
        result_holder["value"] = client.request(
            InvokeRequest(
                app_id=app_id,
                method="test.request",
                target=InvocationTarget(scope=""),
                args={"input": 1},
                options=None,
            )
        )

    thread = threading.Thread(target=worker, daemon=True)
    thread.start()

    def wait_result():
        thread.join(timeout=10)
        return result_holder["value"]

    return wait_result


def _wait_for_single_invocation(client, instance_id: str, instance_session_token: str, timeout_ms: int = 3000):
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
