from __future__ import annotations

import json
import os
import platform
from pathlib import Path

from ._parsing import parse_hub_runtime
from .models import DevHubClientOptions, RuntimeConnectionInfo


RUNTIME_DIR_ENV = "DEVHUB_RUNTIME_DIR"


def discover_runtime(options: DevHubClientOptions) -> RuntimeConnectionInfo:
    """根据运行时目录发现 Hub 连接信息。"""

    cloned = options.clone()
    cloned.validate()

    runtime_directory = resolve_runtime_directory(cloned.runtime_dir)
    hub_json_path = runtime_directory / "hub.json"
    if not hub_json_path.is_file():
        raise RuntimeError(f"未找到 hub.json：{hub_json_path}")

    runtime_payload = json.loads(hub_json_path.read_text(encoding="utf-8"))
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
