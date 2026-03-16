from __future__ import annotations

import os
import platform
from abc import ABC, abstractmethod
from pathlib import Path

from ._json import load_json_text
from ._parsing import parse_hub_runtime
from .models import DevHubClientOptions, RuntimeConnectionInfo


RUNTIME_DIR_ENV = "DEVHUB_RUNTIME_DIR"


class RuntimeResolver(ABC):
    """运行时发现抽象。"""

    @abstractmethod
    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        """根据运行时目录解析 Hub 连接信息。"""


class FileSystemRuntimeResolver(RuntimeResolver):
    """默认的文件系统运行时发现实现。"""

    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        cloned = options.clone()
        cloned.validate()

        runtime_root_or_directory = resolve_runtime_directory(cloned.runtime_dir)
        runtime_directory, hub_json_path = _resolve_runtime_paths(runtime_root_or_directory)
        if not hub_json_path.is_file():
            raise RuntimeError(f"未找到 hub.json：{hub_json_path}")

        runtime_payload = load_json_text(hub_json_path.read_text(encoding="utf-8"), source=str(hub_json_path))
        runtime = parse_hub_runtime(runtime_payload, source=str(hub_json_path))

        token_path = Path(runtime.token_file)
        if not token_path.is_file():
            raise RuntimeError(f"未找到 token 文件：{token_path}")

        token = token_path.read_text(encoding="utf-8").strip()
        if not token:
            raise RuntimeError(f"token 文件为空：{token_path}")

        return RuntimeConnectionInfo(
            runtime_directory=str(runtime_directory),
            token=token,
            runtime=runtime,
        )


def discover_runtime(options: DevHubClientOptions) -> RuntimeConnectionInfo:
    """根据运行时目录发现 Hub 连接信息。"""

    return FileSystemRuntimeResolver().resolve(options)


def resolve_runtime_directory(runtime_dir_override: str | None = None) -> Path:
    """解析运行时目录。"""

    if runtime_dir_override and runtime_dir_override.strip():
        return Path(runtime_dir_override).expanduser().resolve()

    env_runtime_dir = os.getenv(RUNTIME_DIR_ENV)
    if env_runtime_dir and env_runtime_dir.strip():
        return Path(env_runtime_dir).expanduser().resolve()

    system = platform.system()
    home = Path.home()
    if system == "Windows":
        local_app_data = os.getenv("LOCALAPPDATA")
        base = Path(local_app_data) if local_app_data else home / "AppData" / "Local"
        return (base / "DevHub" / "runtime").resolve()
    if system == "Darwin":
        return (home / "Library" / "Application Support" / "DevHub" / "runtime").resolve()

    xdg_data_home = os.getenv("XDG_DATA_HOME")
    base = Path(xdg_data_home).expanduser() if xdg_data_home else home / ".local" / "share"
    return (base / "DevHub" / "runtime").resolve()


def _resolve_runtime_paths(runtime_root_or_directory: Path) -> tuple[Path, Path]:
    standard_runtime_directory = runtime_root_or_directory / "runtime"
    standard_hub_json_path = standard_runtime_directory / "hub.json"

    if standard_hub_json_path.is_file():
        return standard_runtime_directory, standard_hub_json_path

    direct_hub_json_path = runtime_root_or_directory / "hub.json"
    return runtime_root_or_directory, direct_hub_json_path
