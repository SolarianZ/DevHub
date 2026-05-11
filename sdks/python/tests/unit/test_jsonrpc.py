from __future__ import annotations

import pytest

from devhub_sdk._jsonrpc import validate_response_envelope


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
