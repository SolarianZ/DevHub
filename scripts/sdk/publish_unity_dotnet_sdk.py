#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SDK_PROJECT = REPO_ROOT / "sdks" / "dotnet" / "src" / "DevHub.Sdk" / "DevHub.Sdk.csproj"
UNITY_PUBLISH_TOOL_PROJECT = (
    REPO_ROOT / "sdks" / "dotnet" / "tools" / "DevHub.Sdk.UnityPublish" / "DevHub.Sdk.UnityPublish.csproj"
)
DEFAULT_OUTPUT = REPO_ROOT / "artifacts" / "sdk" / "dotnet-for-unity"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Publish DevHub.Sdk for Unity and strip Newtonsoft.Json strong-name metadata.")
    parser.add_argument(
        "--output",
        default=str(DEFAULT_OUTPUT),
        help="Publish output directory. Relative paths are resolved from the repository root.",
    )
    parser.add_argument(
        "--configuration",
        default="Release",
        help="Build configuration used by dotnet publish and the rewrite tool.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    configuration = args.configuration.strip() or "Release"
    output_dir = resolve_output_path(args.output)
    assembly_path = output_dir / "DevHub.Sdk.dll"

    prepare_output_directory(output_dir)

    run(
        [
            "dotnet",
            "publish",
            str(SDK_PROJECT),
            "-c",
            configuration,
            "-o",
            str(output_dir),
        ]
    )
    run(
        [
            "dotnet",
            "run",
            "--project",
            str(UNITY_PUBLISH_TOOL_PROJECT),
            "-c",
            configuration,
            "--",
            str(assembly_path),
        ]
    )

    print(f"[devhub-sdk] Unity publish directory: {output_dir}")
    print(f"[devhub-sdk] Rewritten assembly: {assembly_path}")
    return 0


def resolve_output_path(value: str) -> Path:
    candidate = Path(value)
    if candidate.is_absolute():
        return candidate
    return (REPO_ROOT / candidate).resolve()


def prepare_output_directory(output_dir: Path) -> None:
    if output_dir.exists() and not output_dir.is_dir():
        raise RuntimeError(f"输出路径不是目录：{output_dir}")

    if output_dir == Path(output_dir.anchor) or output_dir == REPO_ROOT:
        raise RuntimeError(f"输出路径不能是磁盘根目录：{output_dir}")

    if output_dir.exists():
        shutil.rmtree(output_dir)


def run(command: list[str]) -> None:
    print(f"[devhub-sdk] > {' '.join(command)}", flush=True)
    subprocess.run(command, cwd=REPO_ROOT, check=True)


if __name__ == "__main__":
    raise SystemExit(main())
