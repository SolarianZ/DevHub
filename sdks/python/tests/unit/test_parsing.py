from __future__ import annotations

import pytest

from devhub_sdk._parsing import parse_app_definition


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
