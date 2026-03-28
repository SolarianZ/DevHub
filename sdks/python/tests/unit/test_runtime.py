from __future__ import annotations

import json
from pathlib import Path

import pytest

from devhub_sdk import DevHubClientOptions, discover_runtime
import devhub_sdk.runtime as runtime_module


def test_M5_PY_UT_001_runtime_discovery_with_valid_hub_json_should_read_token_file(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1  \r\n", encoding="utf-8")
    _write_hub_json(runtime_dir, token_file=token_file)

    connection_info = discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))

    assert connection_info.runtime_directory == str(runtime_dir.resolve())
    assert connection_info.token == "token-1"
    assert connection_info.runtime.http_base_url == "http://127.0.0.1:47231"
    assert connection_info.runtime.ws_url == "ws://127.0.0.1:47231/ws"
    assert connection_info.runtime.token_file == str(token_file)


def test_M5_PY_UT_001_runtime_discovery_when_environment_override_provided_should_use_environment_data_dir(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-env", encoding="utf-8")
    _write_hub_json(runtime_dir, token_file=token_file)
    monkeypatch.setenv("DEVHUB_DATA_DIR", str(data_dir))

    connection_info = discover_runtime(DevHubClientOptions(client_id="unit-test-client"))

    assert connection_info.runtime_directory == str(runtime_dir.resolve())
    assert connection_info.token == "token-env"


def test_M5_PY_UT_001_resolve_data_directory_when_override_and_environment_both_present_should_prefer_override(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("DEVHUB_DATA_DIR", "C:/data-from-env")

    resolved = runtime_module.resolve_data_directory("C:/data-from-argument")

    assert resolved == Path("C:/data-from-argument").resolve()


def test_M5_PY_UT_001_resolve_data_directory_on_windows_should_use_local_app_data(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("DEVHUB_DATA_DIR", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", "C:/Users/tester/AppData/Local")
    monkeypatch.setattr(runtime_module.platform, "system", lambda: "Windows")

    resolved = runtime_module.resolve_data_directory()

    assert resolved == Path("C:/Users/tester/AppData/Local/DevHub").resolve()


def test_M5_PY_UT_001_resolve_data_directory_on_darwin_should_use_application_support(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("DEVHUB_DATA_DIR", raising=False)
    monkeypatch.delenv("LOCALAPPDATA", raising=False)
    monkeypatch.setattr(runtime_module.platform, "system", lambda: "Darwin")
    monkeypatch.setattr(runtime_module.Path, "home", classmethod(lambda cls: Path("C:/Users/tester")))

    resolved = runtime_module.resolve_data_directory()

    assert resolved == Path("C:/Users/tester/Library/Application Support/DevHub").resolve()


def test_M5_PY_UT_001_resolve_data_directory_on_linux_should_use_xdg_data_home_when_present(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("DEVHUB_DATA_DIR", raising=False)
    monkeypatch.setattr(runtime_module.platform, "system", lambda: "Linux")
    monkeypatch.setenv("XDG_DATA_HOME", "C:/xdg-data")

    resolved = runtime_module.resolve_data_directory()

    assert resolved == Path("C:/xdg-data/DevHub").resolve()


def test_M5_PY_UT_001_resolve_data_directory_on_linux_should_fallback_to_home_local_share(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("DEVHUB_DATA_DIR", raising=False)
    monkeypatch.delenv("XDG_DATA_HOME", raising=False)
    monkeypatch.setattr(runtime_module.platform, "system", lambda: "Linux")
    monkeypatch.setattr(runtime_module.Path, "home", classmethod(lambda cls: Path("C:/Users/tester")))

    resolved = runtime_module.resolve_data_directory()

    assert resolved == Path("C:/Users/tester/.local/share/DevHub").resolve()


def test_M5_PY_UT_002_runtime_discovery_when_data_dir_points_to_runtime_subdirectory_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    _write_hub_json(runtime_dir, token_file=token_file)

    with pytest.raises(RuntimeError, match="data_dir 必须指向数据根目录"):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(runtime_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_data_dir_contains_direct_hub_json_layout_should_raise(tmp_path: Path) -> None:
    data_dir = tmp_path / "devhub-data"
    data_dir.mkdir()
    token_file = data_dir / "token.txt"
    token_file.write_text("token-1", encoding="utf-8")
    _write_hub_json(data_dir, token_file=token_file)

    with pytest.raises(RuntimeError, match="data_dir 必须指向数据根目录"):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


@pytest.mark.parametrize("missing_property", ["httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc"])
def test_M5_PY_UT_002_runtime_discovery_when_hub_json_missing_required_field_should_raise(tmp_path: Path, missing_property: str) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload.pop(missing_property)
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_hub_json_contains_non_standard_json_constant_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    (runtime_dir / "hub.json").write_text(
        "\n".join(
            [
                "{",
                '  "protocolVersion": 1,',
                '  "pid": 12345,',
                '  "httpBaseUrl": "http://127.0.0.1:47231",',
                '  "wsUrl": "ws://127.0.0.1:47231/ws",',
                f'  "tokenFile": {json.dumps(str(token_file))},',
                '  "startedAtUtc": "2026-03-09T00:00:00Z",',
                '  "runtimeTuning": {',
                '    "leaseSeconds": 30,',
                '    "onlineThresholdSeconds": 30,',
                '    "launchDedupeWindowSeconds": 30',
                "  },",
                '  "extra": NaN',
                "}",
            ]
        ),
        encoding="utf-8",
    )

    with pytest.raises(RuntimeError, match="不是合法 JSON"):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_started_at_utc_missing_timezone_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload["startedAtUtc"] = "2026-03-09T00:00:00"
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_started_at_utc_is_not_utc_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload["startedAtUtc"] = "2026-03-09T08:00:00+08:00"
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_optional_hub_version_is_null_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload["hubVersion"] = None
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


@pytest.mark.parametrize(
    ("property_name", "value"),
    [
        ("httpBaseUrl", "http://127.0.0.1:47231/"),
        ("httpBaseUrl", "http://192.168.1.10:47231"),
        ("wsUrl", "ws://127.0.0.1:47231/ws/"),
        ("wsUrl", "ws://example.com:47231/ws"),
    ],
)
def test_M5_PY_UT_002_runtime_discovery_when_runtime_url_violates_spec_should_raise(
    tmp_path: Path,
    property_name: str,
    value: str,
) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload[property_name] = value
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


def test_M5_PY_UT_002_runtime_discovery_when_token_file_is_not_absolute_should_raise(tmp_path: Path) -> None:
    data_dir, runtime_dir, token_file = _create_data_directory(tmp_path)
    token_file.write_text("token-1", encoding="utf-8")
    payload = _hub_payload(token_file)
    payload["tokenFile"] = "token.txt"
    (runtime_dir / "hub.json").write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RuntimeError):
        discover_runtime(DevHubClientOptions(client_id="unit-test-client", data_dir=str(data_dir)))


@pytest.mark.parametrize("client_session_id", ["not-a-uuid", "11111111111111111111111111111111"])
def test_M5_PY_UT_002_client_options_when_client_session_id_invalid_should_raise(client_session_id: str) -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", client_session_id=client_session_id).validate()


def test_M5_PY_UT_002_client_options_when_data_dir_type_invalid_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", data_dir=123).validate()  # type: ignore[arg-type]


def test_M5_PY_UT_002_client_options_when_request_timeout_is_not_number_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", request_timeout="50").validate()  # type: ignore[arg-type]


def test_M5_PY_UT_002_client_options_when_protocol_version_is_bool_should_raise() -> None:
    with pytest.raises(ValueError):
        DevHubClientOptions(client_id="unit-test-client", protocol_version=True).validate()  # type: ignore[arg-type]


def _create_data_directory(tmp_path: Path) -> tuple[Path, Path, Path]:
    data_dir = tmp_path / "devhub-data"
    runtime_dir = data_dir / "runtime"
    runtime_dir.mkdir(parents=True)
    token_file = runtime_dir / "token.txt"
    return data_dir, runtime_dir, token_file


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
