#!/usr/bin/env python3
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HOST_BUILD_CONFIGURATION = "Release"
HOST_TARGET_FRAMEWORK = "net10.0"
REPO_ROOT = Path(__file__).resolve().parents[2]
HOST_PROJECT_PATH = REPO_ROOT / "host" / "src" / "DevHub.Host" / "DevHub.Host.csproj"


def main() -> int:
    temp_root = Path(tempfile.mkdtemp(prefix="devhub-sdk-full-")).resolve()

    try:
        host_build_root = temp_root / "host-build"
        host_assembly_path = build_shared_host(host_build_root)

        environment = os.environ.copy()
        environment["DEVHUB_SDK_HOST_ASSEMBLY"] = str(host_assembly_path)

        print(f"[devhub-sdk] 共享 Host: {host_assembly_path}")
        run(["dotnet", "test", "sdks/dotnet/DevHub.DotNetSdk.slnx", "-c", HOST_BUILD_CONFIGURATION], environment)
        run(["npm", "--prefix", "sdks/javascript", "test"], environment)
        run([sys.executable, "-m", "pytest", "sdks/python/tests"], environment)
        return 0
    finally:
        shutil.rmtree(temp_root, ignore_errors=True)


def build_shared_host(build_root: Path) -> Path:
    run(
        [
            "dotnet",
            "build",
            str(HOST_PROJECT_PATH),
            "-c",
            HOST_BUILD_CONFIGURATION,
            "--nologo",
            f"-p:BaseOutputPath={ensure_trailing_separator(build_root / 'bin')}",
        ],
        os.environ.copy(),
    )

    host_assembly_path = build_root / "bin" / HOST_BUILD_CONFIGURATION / HOST_TARGET_FRAMEWORK / "DevHub.Host.dll"
    if not host_assembly_path.is_file():
        raise FileNotFoundError(f"未找到已构建的 Host 程序：{host_assembly_path}")

    return host_assembly_path


def run(command: list[str], environment: dict[str, str]) -> None:
    print(f"[devhub-sdk] > {' '.join(command)}")
    subprocess.run(command, cwd=REPO_ROOT, env=environment, check=True)


def ensure_trailing_separator(path_value: Path) -> str:
    value = str(path_value)
    return value if value.endswith(os.sep) else f"{value}{os.sep}"


if __name__ == "__main__":
    raise SystemExit(main())
