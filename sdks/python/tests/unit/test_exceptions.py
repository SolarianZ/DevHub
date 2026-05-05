from __future__ import annotations

import pytest

from devhub_sdk import DevHubCalleeError, DevHubRpcErrorCode, DevHubRpcException


def test_devhub_rpc_exception_should_expose_known_code_and_helpers() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "detail": "boom",
            "calleeError": {
                "code": 1001,
                "message": "app_error",
                "data": {"reason": "boom"},
            },
        },
        request_id="req-1",
    )

    assert exception.known_code == DevHubRpcErrorCode.INVOCATION_FAILED
    assert exception.is_code(DevHubRpcErrorCode.INVOCATION_FAILED) is True
    assert exception.is_code(-32050) is True
    assert exception.is_code(DevHubRpcErrorCode.UNAUTHORIZED) is False
    assert exception.reason is None
    assert exception.invocation_id == "invk-1"
    assert exception.callee_error == DevHubCalleeError(
        code=1001,
        message="app_error",
        data={"reason": "boom"},
    )
    assert exception.try_get_data_property("calleeError") == {
        "code": 1001,
        "message": "app_error",
        "data": {"reason": "boom"},
    }
    assert exception.try_get_data_string("detail") == "boom"
    assert exception.try_get_data_string("calleeError") is None
    assert exception.try_get_data_property("missing") is None


def test_devhub_rpc_exception_should_preserve_scalar_callee_error_data() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "calleeError": {
                "code": 1001,
                "message": "app_error",
                "data": "invalid-name",
            },
        },
        request_id="req-1",
    )

    assert exception.callee_error == DevHubCalleeError(
        code=1001,
        message="app_error",
        data="invalid-name",
    )


def test_devhub_rpc_exception_when_code_is_unknown_should_return_none() -> None:
    exception = DevHubRpcException(
        code=-32088,
        message="custom_error",
        data={"reason": "custom"},
        request_id="req-2",
    )

    assert exception.known_code is None
    assert exception.reason == "custom"


def test_devhub_rpc_exception_should_expose_unknown_invocation_reason_and_id() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_EXPIRED,
        message="invocation_expired",
        data={"reason": "unknown_invocation", "invocationId": "invk-unknown"},
        request_id="req-unknown-invocation",
    )

    assert exception.known_code == DevHubRpcErrorCode.INVOCATION_EXPIRED
    assert exception.reason == "unknown_invocation"
    assert exception.invocation_id == "invk-unknown"
    assert exception.try_get_data_string("reason") == "unknown_invocation"


def test_devhub_rpc_exception_when_callee_error_data_is_scalar_should_preserve_helper() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "calleeError": {
                "code": 1001,
                "message": "app_error",
                "data": "boom",
            },
        },
        request_id="req-invalid-callee-error",
    )

    assert exception.callee_error == DevHubCalleeError(
        code=1001,
        message="app_error",
        data="boom",
    )


def test_devhub_rpc_exception_when_callee_error_data_is_null_should_preserve_helper() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "calleeError": {
                "code": 1001,
                "message": "app_error",
                "data": None,
            },
        },
        request_id="req-invalid-callee-error-data",
    )

    assert exception.callee_error == DevHubCalleeError(
        code=1001,
        message="app_error",
        data=None,
    )


def test_devhub_rpc_exception_when_callee_error_data_is_not_json_value_should_ignore_helper() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "calleeError": {
                "code": 1001,
                "message": "app_error",
                "data": {"callback": lambda: "ignored"},
            },
        },
        request_id="req-invalid-callee-error-data",
    )

    assert exception.callee_error is None


def test_devhub_rpc_exception_when_callee_error_code_is_bool_should_ignore_helper() -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.INVOCATION_FAILED,
        message="invocation_failed",
        data={
            "invocationId": "invk-1",
            "calleeError": {
                "code": True,
                "message": "app_error",
            },
        },
        request_id="req-invalid-callee-error-code",
    )

    assert exception.callee_error is None


@pytest.mark.parametrize("property_name", ["", "   ", None])
def test_devhub_rpc_exception_when_property_name_is_blank_should_raise(property_name) -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.UNAUTHORIZED,
        message="unauthorized",
        data={"reason": "invalid_token"},
        request_id="req-3",
    )

    with pytest.raises(ValueError):
        exception.try_get_data_property(property_name)


@pytest.mark.parametrize("error_code", [None, "-32001", True])
def test_devhub_rpc_exception_when_is_code_input_is_invalid_should_raise(error_code) -> None:
    exception = DevHubRpcException(
        code=DevHubRpcErrorCode.UNAUTHORIZED,
        message="unauthorized",
        data={"reason": "invalid_token"},
        request_id="req-4",
    )

    with pytest.raises(TypeError):
        exception.is_code(error_code)
