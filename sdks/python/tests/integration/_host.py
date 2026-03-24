from __future__ import annotations

import json
import os
import signal
import subprocess
import time
from collections import deque
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Thread
from typing import Any, Mapping
from uuid import uuid4

from devhub_sdk import DevHubClient, DevHubClientOptions, DevHubEventsClient


class DevHubHostFixture:
    """DevHub Host 进程测试夹具。"""

    def __init__(
        self,
        repo_root: Path,
        temp_root: TemporaryDirectory[str],
        data_directory: Path,
        runtime_directory: Path,
        definitions_directory: Path,
        instances_directory: Path,
        logs_directory: Path,
    ) -> None:
        self._repo_root = repo_root
        self._temp_root = temp_root
        self.data_directory = data_directory
        self.runtime_directory = runtime_directory
        self.definitions_directory = definitions_directory
        self.instances_directory = instances_directory
        self.logs_directory = logs_directory
        self._process: subprocess.Popen[str] | None = None
        self._stdout_buffer: deque[str] = deque(maxlen=200)
        self._stderr_buffer: deque[str] = deque(maxlen=200)
        self._stdout_thread: Thread | None = None
        self._stderr_thread: Thread | None = None

    @classmethod
    def start(cls) -> "DevHubHostFixture":
        repo_root = _resolve_repo_root()
        temp_root = TemporaryDirectory(prefix="devhub-python-sdk-")
        data_directory = Path(temp_root.name)
        runtime_directory = data_directory / "runtime"
        definitions_directory = data_directory / "apps" / "definitions"
        instances_directory = data_directory / "apps" / "instances"
        logs_directory = data_directory / "logs"
        runtime_directory.mkdir(parents=True, exist_ok=True)
        definitions_directory.mkdir(parents=True, exist_ok=True)
        instances_directory.mkdir(parents=True, exist_ok=True)
        logs_directory.mkdir(parents=True, exist_ok=True)

        fixture = cls(
            repo_root,
            temp_root,
            data_directory,
            runtime_directory,
            definitions_directory,
            instances_directory,
            logs_directory,
        )
        fixture._start_process()
        return fixture

    def write_definition(self, definition: Mapping[str, Any]) -> None:
        path = self.definitions_directory / f"{definition['appId']}.json"
        path.write_text(json.dumps(definition), encoding="utf-8")

    def create_client(self, client_id: str) -> DevHubClient:
        return DevHubClient.from_runtime(
            DevHubClientOptions(client_id=client_id, data_dir=str(self.data_directory))
        )

    async def create_events_client(self, client_id: str) -> DevHubEventsClient:
        return await DevHubEventsClient.from_runtime(
            DevHubClientOptions(client_id=client_id, data_dir=str(self.data_directory))
        )

    def close(self) -> None:
        if self._process is not None:
            try:
                if self._process.poll() is None:
                    _terminate_process_tree(self._process)
                    try:
                        self._process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        self._process.kill()
                        self._process.wait(timeout=5)
            finally:
                self._process = None
        self._stop_output_drainers()
        self._temp_root.cleanup()

    def __enter__(self) -> "DevHubHostFixture":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def _start_process(self) -> None:
        host_assembly_path = (
            self._repo_root / "host" / "src" / "DevHub.Host" / "bin" / "Release" / "net10.0" / "DevHub.Host.dll"
        )
        if not host_assembly_path.is_file():
            raise RuntimeError(f"未找到 Host 程序：{host_assembly_path}")

        environment = os.environ.copy()
        environment["DEVHUB_DATA_DIR"] = str(self.data_directory)
        environment["DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS"] = uuid4().hex

        self._process = subprocess.Popen(
            ["dotnet", str(host_assembly_path)],
            cwd=self._repo_root,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            **_create_isolated_process_kwargs(),
        )
        # 重要：必须持续消费 stdout/stderr，避免管道被写满后阻塞 Host 线程，导致 HTTP 请求卡死。
        self._start_output_drainers()

        hub_json_path = self.runtime_directory / "hub.json"
        deadline = time.time() + 30
        while time.time() < deadline:
            if hub_json_path.is_file():
                return
            if self._process.poll() is not None:
                stdout = self._collect_output(self._stdout_buffer)
                stderr = self._collect_output(self._stderr_buffer)
                raise RuntimeError(f"Host 进程提前退出。stdout={stdout} stderr={stderr}")
            time.sleep(0.25)

        raise RuntimeError("等待 hub.json 超时。")

    def _start_output_drainers(self) -> None:
        if self._process is None:
            return

        if self._process.stdout is not None:
            # 说明：Host 默认会写 Console 日志；若不 drain，Windows 管道缓冲区写满后会阻塞。
            self._stdout_thread = Thread(
                target=self._drain_stream,
                args=(self._process.stdout, self._stdout_buffer),
                daemon=True,
            )
            self._stdout_thread.start()

        if self._process.stderr is not None:
            # 说明：stderr 同样需要 drain，避免日志输出造成死锁。
            self._stderr_thread = Thread(
                target=self._drain_stream,
                args=(self._process.stderr, self._stderr_buffer),
                daemon=True,
            )
            self._stderr_thread.start()

    def _stop_output_drainers(self) -> None:
        threads = [self._stdout_thread, self._stderr_thread]
        for thread in threads:
            if thread is not None:
                thread.join(timeout=1)
        self._stdout_thread = None
        self._stderr_thread = None

    @staticmethod
    def _drain_stream(stream, buffer: deque[str]) -> None:
        for line in iter(stream.readline, ""):
            buffer.append(line)
        try:
            stream.close()
        except Exception:
            return

    @staticmethod
    def _collect_output(buffer: deque[str]) -> str:
        if not buffer:
            return ""
        return "".join(buffer)


def _resolve_repo_root() -> Path:
    current = Path(__file__).resolve()
    for directory in [current, *current.parents]:
        if (directory / "AGENTS.md").is_file() and (directory / "host" / "src" / "DevHub.Host" / "DevHub.Host.csproj").is_file():
            return directory
    raise RuntimeError("无法定位仓库根目录。")


def _create_isolated_process_kwargs() -> dict[str, Any]:
    if os.name == "nt":
        return {"creationflags": getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)}
    return {"start_new_session": True}


def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return

    if os.name == "nt":
        try:
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=10,
            )
        except Exception:
            process.kill()
        return

    try:
        os.killpg(os.getpgid(process.pid), signal.SIGKILL)
    except ProcessLookupError:
        return
