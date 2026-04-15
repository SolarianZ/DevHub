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
