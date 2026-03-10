from __future__ import annotations

import json
from pathlib import Path

import pytest

from devhub_sdk import DevHubClientOptions, discover_runtime


def test_runtime_discovery_with_valid_hub_json_should_read_token_file(tmp_path: Path) -> None:
    runtime_dir = tmp_path / "runtime"
    runtime_dir.mkdir()
    token_file = runtime_dir / "token.txt"
    token_file.write_text("token-1  \r\n", encoding="utf-8")
    _write_hub_json(runtime_dir, token_file=token_file)

    connection_info = discover_runtime(DevHubClientOptions(client_id="unit-test-client", runtime_dir=str(runtime_dir)))

    assert connection_info.token == "token-1"
    assert connection_info.runtime.http_base_url == "http://127.0.0.1:47231"
    assert connection_info.runtime.ws_url == "ws://127.0.0.1:47231/ws"
    assert connection_info.runtime.token_file == str(token_file)


@pytest.mark.parametrize("missing_property", ["httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc"])
def test_runtime_discovery_when_hub_json_missing_required_field_should_raise(tmp_path: Path, missing_property: str) -> None:
    runtime_dir = tmp_path / "runtime"
    runtime_dir.mkdir()
    token_file = runtime_dir / "token.txt"
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload.pop(missing_property)
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", runtime_dir=str(runtime_dir)))


def test_runtime_discovery_when_environment_override_provided_should_use_environment_runtime_dir(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    runtime_dir = tmp_path / "runtime"
    runtime_dir.mkdir()
    token_file = runtime_dir / "token.txt"
    token_file.write_text("token-env", encoding="utf-8")
    _write_hub_json(runtime_dir, token_file=token_file)
    monkeypatch.setenv("DEVHUB_RUNTIME_DIR", str(runtime_dir))

    connection_info = discover_runtime(DevHubClientOptions(client_id="unit-test-client"))

    assert connection_info.runtime_directory == str(runtime_dir.resolve())
    assert connection_info.token == "token-env"


def test_client_options_when_client_session_id_invalid_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", client_session_id="not-a-uuid").validate()


def test_client_options_when_request_timeout_is_not_number_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", request_timeout="50").validate()  # type: ignore[arg-type]


def test_client_options_when_protocol_version_is_bool_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", protocol_version=True).validate()  # type: ignore[arg-type]


def _write_hub_json(runtime_dir: Path, *, token_file: Path) -> None:
    (runtime_dir / "hub.json").write_text(json.dumps(_hub_payload(token_file)), encoding="utf-8")


def _hub_payload(token_file: Path) -> dict[str, object]:
    return {
        "protocolVersion": 1,
        "pid": 12345,
        "httpBaseUrl": "http://127.0.0.1:47231",
        "wsUrl": "ws://127.0.0.1:47231/ws",
        "tokenFile": str(token_file),
        "startedAtUtc": "2026-03-09T00:00:00Z",
        "runtimeTuning": {
            "leaseSeconds": 30,
            "onlineThresholdSeconds": 30,
            "launchDedupeWindowSeconds": 30,
        },
    }
