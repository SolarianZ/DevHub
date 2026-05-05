from __future__ import annotations

import os
import platform
from abc import ABC, abstractmethod
from pathlib import Path

from ._json import load_json_text
from ._parsing import parse_hub_runtime, validate_runtime_endpoints
from .models import DevHubClientOptions, RuntimeConnectionInfo


DATA_DIR_ENV = "DEVHUB_DATA_DIR"


class RuntimeResolver(ABC):
    """运行时发现抽象。"""

    @abstractmethod
    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        """根据数据根目录解析 Hub 连接信息。"""


class FileSystemRuntimeResolver(RuntimeResolver):
    """默认的文件系统运行时发现实现。"""

    def resolve(self, options: DevHubClientOptions) -> RuntimeConnectionInfo:
        cloned = options.clone()
        cloned.validate()

        data_directory = resolve_data_directory(cloned.data_dir)
        runtime_directory = data_directory / "runtime"
        hub_json_path = runtime_directory / "hub.json"
        if not hub_json_path.is_file():
            _raise_invalid_data_directory_error_if_needed(data_directory)
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
    """根据数据根目录发现 Hub 连接信息。"""

    return FileSystemRuntimeResolver().resolve(options)


def validate_runtime_connection_info(connection_info: RuntimeConnectionInfo, source: str = "runtime_resolver") -> None:
    """校验注入式运行时连接信息仍满足协议端点约束。"""

    validate_runtime_endpoints(connection_info.runtime.http_base_url, connection_info.runtime.ws_url, source)

    if connection_info.rpc_endpoint != f"{connection_info.runtime.http_base_url}/rpc":
        raise RuntimeError(f"{source}.rpc_endpoint 非法。")
    if connection_info.websocket_endpoint != connection_info.runtime.ws_url:
        raise RuntimeError(f"{source}.websocket_endpoint 非法。")


def resolve_data_directory(data_dir_override: str | None = None) -> Path:
    """解析运行时数据根目录。"""

    if data_dir_override and data_dir_override.strip():
        return _to_absolute_path(Path(data_dir_override).expanduser())

    env_data_dir = os.getenv(DATA_DIR_ENV)
    if env_data_dir and env_data_dir.strip():
        return _to_absolute_path(Path(env_data_dir).expanduser())

    system = platform.system()
    home = Path.home()
    if system == "Windows":
        local_app_data = os.getenv("LOCALAPPDATA")
        base = Path(local_app_data) if local_app_data else home / "AppData" / "Local"
        return _to_absolute_path(base / "DevHub")
    if system == "Darwin":
        return _to_absolute_path(home / "Library" / "Application Support" / "DevHub")

    xdg_data_home = os.getenv("XDG_DATA_HOME")
    base = Path(xdg_data_home).expanduser() if xdg_data_home else home / ".local" / "share"
    return _to_absolute_path(base / "DevHub")


def _to_absolute_path(path: Path) -> Path:
    return Path(os.path.abspath(path))


def _raise_invalid_data_directory_error_if_needed(data_directory: Path) -> None:
    direct_hub_json_path = data_directory / "hub.json"
    direct_token_path = data_directory / "token.txt"
    if direct_hub_json_path.is_file() or direct_token_path.is_file():
        raise RuntimeError(
            "data_dir 必须指向数据根目录，SDK 仅支持 <data_dir>/runtime/hub.json；"
            f"不再支持直接传入 runtime 子目录或旧版 hub.json 直放布局：{data_directory}"
        )
