from __future__ import annotations

import re
from dataclasses import dataclass
from importlib import metadata
from pathlib import Path

from .models import VersionCompatibilityResult, VersionCompatibilityStatus


_PACKAGE_NAME = "devhub-sdk-python"
_PYPROJECT_VERSION_PATTERN = re.compile(r'^version\s*=\s*"([^"]+)"\s*$', re.MULTILINE)
_SEMVER_PATTERN = re.compile(
    r"^(0|[1-9]\d*)\."
    r"(0|[1-9]\d*)\."
    r"(0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)


@dataclass(slots=True, frozen=True)
class _SemVerCore:
    major: int
    minor: int


def _load_sdk_version() -> str:
    try:
        return metadata.version(_PACKAGE_NAME)
    except metadata.PackageNotFoundError:
        return _load_sdk_version_from_pyproject()


def _load_sdk_version_from_pyproject() -> str:
    pyproject_path = Path(__file__).resolve().parents[2] / "pyproject.toml"
    if not pyproject_path.is_file():
        raise RuntimeError(f"未找到 Python SDK 版本元数据文件：{pyproject_path}")

    match = _PYPROJECT_VERSION_PATTERN.search(pyproject_path.read_text(encoding="utf-8"))
    if match is None:
        raise RuntimeError(f"无法从 {pyproject_path} 解析 Python SDK 版本。")
    return match.group(1)


def get_sdk_version() -> str:
    """返回当前 Python SDK 的公开版本字符串。"""

    return SDK_VERSION


def evaluate_version_compatibility(
    sdk_version: str,
    host_version: str | None,
) -> VersionCompatibilityResult:
    """按 DevHub 兼容规则比较 SDK 与 Host 版本。"""

    sdk_core = _parse_semver_core(sdk_version)
    host_core = _parse_semver_core(host_version)
    if sdk_core is None or host_core is None:
        status = VersionCompatibilityStatus.UNKNOWN
    elif sdk_core.major != host_core.major:
        status = VersionCompatibilityStatus.INCOMPATIBLE
    elif sdk_core.minor != host_core.minor:
        status = VersionCompatibilityStatus.UPDATE_RECOMMENDED
    else:
        status = VersionCompatibilityStatus.COMPATIBLE

    return VersionCompatibilityResult(
        sdk_version=sdk_version,
        host_version=host_version,
        status=status,
    )


def _parse_semver_core(version: str | None) -> _SemVerCore | None:
    if not isinstance(version, str) or not version:
        return None

    match = _SEMVER_PATTERN.fullmatch(version)
    if match is None:
        return None

    return _SemVerCore(
        major=int(match.group(1)),
        minor=int(match.group(2)),
    )


SDK_VERSION = _load_sdk_version()
