from __future__ import annotations

import shutil
import tempfile
from pathlib import Path

from . import _host


def test_resolve_configured_host_assembly_path_from_environment_should_use_shared_override(monkeypatch) -> None:
    repo_root = _create_fake_repository_root()
    _host._cleanup_shared_host_build_root()

    try:
        configured_host_assembly_path = _create_configured_host_assembly(
            repo_root,
            Path("shared") / "DevHub.Host.dll",
        )
        monkeypatch.setenv("DEVHUB_SDK_HOST_ASSEMBLY", str(Path("shared") / "DevHub.Host.dll"))

        resolved_path = _host._resolve_configured_host_assembly_path_from_environment(repo_root)

        assert resolved_path == configured_host_assembly_path.resolve()
    finally:
        _host._cleanup_shared_host_build_root()
        shutil.rmtree(repo_root, ignore_errors=True)


def test_resolve_configured_host_assembly_path_from_environment_should_prefer_python_specific_override(monkeypatch) -> None:
    repo_root = _create_fake_repository_root()
    _host._cleanup_shared_host_build_root()

    try:
        shared_host_assembly_path = _create_configured_host_assembly(
            repo_root,
            Path("shared") / "DevHub.Host.dll",
        )
        python_host_assembly_path = _create_configured_host_assembly(
            repo_root,
            Path("python") / "DevHub.Host.dll",
        )
        monkeypatch.setenv("DEVHUB_SDK_HOST_ASSEMBLY", str(shared_host_assembly_path))
        monkeypatch.setenv("DEVHUB_PYTHON_SDK_HOST_ASSEMBLY", str(python_host_assembly_path))

        resolved_path = _host._resolve_configured_host_assembly_path_from_environment(repo_root)

        assert resolved_path == python_host_assembly_path
    finally:
        _host._cleanup_shared_host_build_root()
        shutil.rmtree(repo_root, ignore_errors=True)


def test_resolve_host_assembly_path_should_skip_local_build_when_prebuilt_host_provided(monkeypatch) -> None:
    repo_root = _create_fake_repository_root()
    _host._cleanup_shared_host_build_root()

    try:
        configured_host_assembly_path = _create_configured_host_assembly(
            repo_root,
            Path("prebuilt") / "DevHub.Host.dll",
        )
        monkeypatch.setenv("DEVHUB_SDK_HOST_ASSEMBLY", str(configured_host_assembly_path))

        resolved_path = _host._resolve_host_assembly_path(repo_root)

        assert resolved_path == configured_host_assembly_path
    finally:
        _host._cleanup_shared_host_build_root()
        shutil.rmtree(repo_root, ignore_errors=True)


def _create_fake_repository_root() -> Path:
    return Path(tempfile.mkdtemp(prefix="devhub-python-host-fixture-"))


def _create_configured_host_assembly(repo_root: Path, relative_path: Path) -> Path:
    host_assembly_path = repo_root / relative_path
    host_assembly_path.parent.mkdir(parents=True, exist_ok=True)
    host_assembly_path.write_text("", encoding="utf-8")
    return host_assembly_path
