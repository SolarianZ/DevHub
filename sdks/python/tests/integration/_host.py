from __future__ import annotations

import atexit
import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
import sys
from collections import deque
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Lock, Thread
from typing import Any, Mapping
from uuid import uuid4

from devhub_sdk import DevHubClient, DevHubClientOptions, DevHubEventsClient

PREBUILT_HOST_ASSEMBLY_ENVIRONMENT_VARIABLE = "DEVHUB_PYTHON_SDK_HOST_ASSEMBLY"
SHARED_PREBUILT_HOST_ASSEMBLY_ENVIRONMENT_VARIABLE = "DEVHUB_SDK_HOST_ASSEMBLY"
HOST_BUILD_CONFIGURATION = "Release"
HOST_TARGET_FRAMEWORK = "net10.0"
TEST_LIVE_STATUS_ENV_VAR = "DEVHUB_TEST_LIVE_STATUS"
LONG_WAIT_STATUS_THRESHOLD_SECONDS = 8

_shared_host_assembly_path: Path | None = None
_shared_host_build_root: Path | None = None
_shared_host_assembly_lock = Lock()


def _read_live_status_enabled() -> bool:
    raw_value = os.environ.get(TEST_LIVE_STATUS_ENV_VAR)
    if raw_value is None:
        return False

    candidate = raw_value.strip().lower()
    if candidate in ("1", "true", "yes", "on"):
        return True
    if candidate in ("0", "false", "no", "off"):
        return False

    raise RuntimeError(f"{TEST_LIVE_STATUS_ENV_VAR} 必须是布尔值（1/0/true/false/yes/no/on/off）")


class _LongWaitStatus:
    """统一管理 Python SDK 集成测试中的长等待状态输出。"""

    def __init__(self, label: str, estimated_seconds: float) -> None:
        self._label = label
        self._estimated_seconds = max(0, int(estimated_seconds + 0.999))
        self._live_enabled = _read_live_status_enabled()
        self._started_at = time.monotonic()
        self._entered = False
        self._last_rendered_second = -1
        self._last_render_length = 0

    def tick(self) -> None:
        elapsed_seconds = int(time.monotonic() - self._started_at)
        if not self._entered and elapsed_seconds >= LONG_WAIT_STATUS_THRESHOLD_SECONDS:
            self._entered = True
            if not self._live_enabled:
                print(
                    f"[状态] {self._label} 开始，预计等待约 {self._estimated_seconds}s",
                    flush=True,
                )
                return

        if self._live_enabled and self._entered and elapsed_seconds != self._last_rendered_second:
            self._last_rendered_second = elapsed_seconds
            content = f"[状态] {self._label} 已等待 {elapsed_seconds}s"
            trailing_spaces = " " * max(0, self._last_render_length - len(content))
            sys.stdout.write(f"\r{content}{trailing_spaces}")
            sys.stdout.flush()
            self._last_render_length = max(self._last_render_length, len(content))

    def finish(self) -> None:
        if not (self._live_enabled and self._entered):
            return

        self.tick()
        sys.stdout.write("\n")
        sys.stdout.flush()


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
        host_assembly_path = _resolve_host_assembly_path(self._repo_root)
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
        status = _LongWaitStatus("等待 Python SDK Host fixture 生成 hub.json", 30)
        try:
            while time.time() < deadline:
                if hub_json_path.is_file():
                    return
                if self._process.poll() is not None:
                    stdout = self._collect_output(self._stdout_buffer)
                    stderr = self._collect_output(self._stderr_buffer)
                    raise RuntimeError(f"Host 进程提前退出。stdout={stdout} stderr={stderr}")
                time.sleep(0.25)
                status.tick()
        finally:
            status.finish()

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


def _resolve_host_assembly_path(repo_root: Path) -> Path:
    global _shared_host_assembly_path
    global _shared_host_build_root

    with _shared_host_assembly_lock:
        if _shared_host_assembly_path is not None and _shared_host_assembly_path.is_file():
            return _shared_host_assembly_path

        configured_host_assembly_path = _resolve_configured_host_assembly_path_from_environment(repo_root)
        if configured_host_assembly_path is not None:
            _shared_host_build_root = None
            _shared_host_assembly_path = configured_host_assembly_path
            return configured_host_assembly_path

        build_root = Path(tempfile.mkdtemp(prefix="devhub-python-sdk-host-build-"))
        try:
            host_assembly_path = _build_isolated_host_assembly(repo_root, build_root)
        except Exception:
            shutil.rmtree(build_root, ignore_errors=True)
            raise

        _shared_host_build_root = build_root
        _shared_host_assembly_path = host_assembly_path
        return host_assembly_path


def _resolve_configured_host_assembly_path(repo_root: Path, configured_path: str, environment_variable_name: str) -> Path:
    resolved_path = Path(configured_path)
    if not resolved_path.is_absolute():
        resolved_path = (repo_root / resolved_path).resolve()

    if resolved_path.is_file():
        return resolved_path

    raise RuntimeError(
        f"环境变量 {environment_variable_name} 指定的 Host 程序不存在：{resolved_path}"
    )


def _resolve_configured_host_assembly_path_from_environment(repo_root: Path) -> Path | None:
    for environment_variable_name in (
        PREBUILT_HOST_ASSEMBLY_ENVIRONMENT_VARIABLE,
        SHARED_PREBUILT_HOST_ASSEMBLY_ENVIRONMENT_VARIABLE,
    ):
        configured_path = os.environ.get(environment_variable_name, "").strip()
        if configured_path:
            return _resolve_configured_host_assembly_path(repo_root, configured_path, environment_variable_name)

    return None


def _resolve_built_host_assembly_path(build_root: Path) -> Path:
    host_assembly_path = build_root / "bin" / HOST_BUILD_CONFIGURATION / HOST_TARGET_FRAMEWORK / "DevHub.Host.dll"
    if host_assembly_path.is_file():
        return host_assembly_path

    raise RuntimeError(f"未找到构建后的 Host 程序：{host_assembly_path}")


def _build_isolated_host_assembly(repo_root: Path, build_root: Path) -> Path:
    _build_host_assembly(repo_root, build_root)
    return _resolve_built_host_assembly_path(build_root)


def _build_host_assembly(repo_root: Path, build_root: Path) -> None:
    host_project_path = repo_root / "host" / "src" / "DevHub.Host" / "DevHub.Host.csproj"
    if not host_project_path.is_file():
        raise RuntimeError(f"未找到 Host 工程：{host_project_path}")

    completed = subprocess.run(
        [
            "dotnet",
            "build",
            str(host_project_path),
            "-c",
            HOST_BUILD_CONFIGURATION,
            "--nologo",
            f"-p:BaseOutputPath={_ensure_trailing_separator(build_root / 'bin')}",
        ],
        cwd=repo_root,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            f"构建 Host 失败。stdout={completed.stdout or ''} stderr={completed.stderr or ''}"
        )


def _ensure_trailing_separator(path_value: Path) -> str:
    value = str(path_value)
    return value if value.endswith(os.sep) else f"{value}{os.sep}"


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
                stdin=subprocess.DEVNULL,
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


def _cleanup_shared_host_build_root() -> None:
    global _shared_host_assembly_path
    global _shared_host_build_root

    build_root = _shared_host_build_root
    _shared_host_assembly_path = None
    _shared_host_build_root = None

    if build_root is not None:
        shutil.rmtree(build_root, ignore_errors=True)


atexit.register(_cleanup_shared_host_build_root)
