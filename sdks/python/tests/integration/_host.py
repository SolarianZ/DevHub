from __future__ import annotations

import json
import os
import subprocess
import time
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any, Mapping
from uuid import uuid4

from devhub_sdk import DevHubClient, DevHubClientOptions, DevHubEventsClient


class DevHubHostFixture:
    """DevHub Host 进程测试夹具。"""

    def __init__(self, repo_root: Path, temp_root: TemporaryDirectory[str], runtime_directory: Path, definitions_directory: Path) -> None:
        self._repo_root = repo_root
        self._temp_root = temp_root
        self.runtime_directory = runtime_directory
        self.definitions_directory = definitions_directory
        self._process: subprocess.Popen[str] | None = None

    @classmethod
    def start(cls) -> "DevHubHostFixture":
        repo_root = _resolve_repo_root()
        temp_root = TemporaryDirectory(prefix="devhub-python-sdk-")
        temp_path = Path(temp_root.name)
        runtime_directory = temp_path / "runtime"
        definitions_directory = temp_path / "definitions"
        runtime_directory.mkdir(parents=True, exist_ok=True)
        definitions_directory.mkdir(parents=True, exist_ok=True)

        fixture = cls(repo_root, temp_root, runtime_directory, definitions_directory)
        fixture._start_process()
        return fixture

    def write_definition(self, definition: Mapping[str, Any]) -> None:
        path = self.definitions_directory / f"{definition['appId']}.json"
        path.write_text(json.dumps(definition), encoding="utf-8")

    def create_client(self, client_id: str) -> DevHubClient:
        return DevHubClient.from_runtime(
            DevHubClientOptions(client_id=client_id, runtime_dir=str(self.runtime_directory))
        )

    async def create_events_client(self, client_id: str) -> DevHubEventsClient:
        return await DevHubEventsClient.from_runtime(
            DevHubClientOptions(client_id=client_id, runtime_dir=str(self.runtime_directory))
        )

    def close(self) -> None:
        if self._process is not None:
            try:
                if self._process.poll() is None:
                    self._process.kill()
                    self._process.wait(timeout=10)
            finally:
                self._process = None
        self._temp_root.cleanup()

    def __enter__(self) -> "DevHubHostFixture":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def _start_process(self) -> None:
        host_assembly_path = self._repo_root / "src" / "DevHub.Host" / "bin" / "Release" / "net10.0" / "DevHub.Host.dll"
        if not host_assembly_path.is_file():
            raise RuntimeError(f"未找到 Host 程序：{host_assembly_path}")

        environment = os.environ.copy()
        environment["DEVHUB_RUNTIME_DIR"] = str(self.runtime_directory)
        environment["DEVHUB_APPDEFS_DIR"] = str(self.definitions_directory)
        environment["DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS"] = uuid4().hex

        self._process = subprocess.Popen(
            ["dotnet", str(host_assembly_path)],
            cwd=self._repo_root,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=environment,
        )

        hub_json_path = self.runtime_directory / "hub.json"
        deadline = time.time() + 30
        while time.time() < deadline:
            if hub_json_path.is_file():
                return
            if self._process.poll() is not None:
                stdout = self._process.stdout.read() if self._process.stdout is not None else ""
                stderr = self._process.stderr.read() if self._process.stderr is not None else ""
                raise RuntimeError(f"Host 进程提前退出。stdout={stdout} stderr={stderr}")
            time.sleep(0.25)

        raise RuntimeError("等待 hub.json 超时。")


def _resolve_repo_root() -> Path:
    current = Path(__file__).resolve()
    for directory in [current, *current.parents]:
        if (directory / "AGENTS.md").is_file() and (directory / "src" / "DevHub.Host" / "DevHub.Host.csproj").is_file():
            return directory
    raise RuntimeError("无法定位仓库根目录。")
