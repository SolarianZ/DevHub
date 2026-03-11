from __future__ import annotations

import pytest

from devhub_sdk._parsing import parse_app_definition, parse_launch_result


def test_parse_app_definition_when_launch_missing_exe_path_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_app_definition(
            {
                "appId": "test.app",
                "displayName": "Test App",
                "launch": {},
            },
            path="app.definition",
        )


def test_parse_launch_result_when_pid_is_bool_should_raise() -> None:
    with pytest.raises(RuntimeError):
        parse_launch_result(
            {
                "ok": True,
                "status": "started",
                "launchId": "launch-1",
                "pid": True,
            },
            path="hub.apps.launch.result",
        )
