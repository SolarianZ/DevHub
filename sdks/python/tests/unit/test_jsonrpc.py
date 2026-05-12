from __future__ import annotations

import pytest

from devhub_sdk._jsonrpc import validate_response_envelope
from devhub_sdk.exceptions import DevHubRpcException


@pytest.mark.parametrize("data", [None, {"callback": lambda: "ignored"}])
def test_validate_response_envelope_when_error_data_is_not_valid_json_object_should_raise(data) -> None:
    with pytest.raises(RuntimeError):
        validate_response_envelope(
            {
                "jsonrpc": "2.0",
                "id": "req-1",
                "error": {
                    "code": -32001,
                    "message": "unauthorized",
                    "data": data,
                },
            },
            "req-1",
        )


def test_validate_response_envelope_should_accept_int64_numeric_response_id() -> None:
    result = validate_response_envelope(
        {
            "jsonrpc": "2.0",
            "id": 9223372036854775807,
            "result": {
                "ok": True,
            },
        },
        "9223372036854775807",
    )

    assert result["ok"] is True


def test_validate_response_envelope_when_error_response_id_is_null_should_raise_devhub_rpc_exception() -> None:
    with pytest.raises(DevHubRpcException) as exc_info:
        validate_response_envelope(
            {
                "jsonrpc": "2.0",
                "id": None,
                "error": {
                    "code": -32001,
                    "message": "unauthorized",
                    "data": {
                        "reason": "invalid_token",
                    },
                },
            },
            "req-1",
        )

    assert exc_info.value.code == -32001
    assert exc_info.value.reason == "invalid_token"
    assert exc_info.value.request_id == "req-1"


@pytest.mark.parametrize(
    ("response_id", "message"),
    [
        (1.5, "Int64 范围内整数"),
        (9223372036854775808, "Int64 范围"),
    ],
)
def test_validate_response_envelope_when_numeric_response_id_violates_protocol_should_raise(
    response_id,
    message: str,
) -> None:
    with pytest.raises(RuntimeError, match=message):
        validate_response_envelope(
            {
                "jsonrpc": "2.0",
                "id": response_id,
                "result": {
                    "ok": True,
                },
            },
            str(response_id),
        )
