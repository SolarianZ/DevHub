from __future__ import annotations

import pytest

from devhub_sdk import create_instance_id
from devhub_sdk._validation import require_instance_id


def test_create_instance_id_should_generate_canonical_hub_global_value() -> None:
    first = create_instance_id("Sample.App", "Workspace-A.v2")
    second = create_instance_id("Sample.App", "Workspace-A.v2")

    assert first != second
    assert first.startswith("Sample.App.Workspace-A.v2.py-")
    assert require_instance_id(first, "instance_id") == first
    assert len(first) <= 256


def test_create_instance_id_should_encode_global_scope_without_empty_segment() -> None:
    instance_id = create_instance_id("sample.app", "")

    assert instance_id.startswith("sample.app.global.py-")
    assert require_instance_id(instance_id, "instance_id") == instance_id


def test_create_instance_id_with_long_identity_should_stay_within_instance_id_limit() -> None:
    instance_id = create_instance_id("a" * 120, "b" * 120)

    assert instance_id.startswith("py.")
    assert require_instance_id(instance_id, "instance_id") == instance_id
    assert len(instance_id) <= 256


@pytest.mark.parametrize(
    ("app_id", "scope"),
    [
        (".sample", ""),
        ("sample.app", ".workspace"),
    ],
)
def test_create_instance_id_should_reject_invalid_identity_inputs(app_id: str, scope: str) -> None:
    with pytest.raises(ValueError):
        create_instance_id(app_id, scope)
