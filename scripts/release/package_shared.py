from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
import zipfile
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence
from xml.etree import ElementTree

import tomllib

from console_output import console_print, write_console_text
from package_models import HOST_VARIANTS, HOST_VARIANT_ORDER, PackageHelp, ReleaseAsset, ValidationRecord


REPO_ROOT = Path(__file__).resolve().parents[2]
VERSION_SYNC_SCRIPT = REPO_ROOT / "scripts" / "release" / "sync_versions.py"
HOST_PROJECT = REPO_ROOT / "host" / "src" / "DevHub.Host" / "DevHub.Host.csproj"
DOTNET_SDK_PROJECTS = (
    REPO_ROOT / "sdks" / "dotnet" / "src" / "DevHub.Sdk" / "DevHub.Sdk.csproj",
    REPO_ROOT / "sdks" / "dotnet" / "src" / "DevHub.Sdk.DependencyInjection" / "DevHub.Sdk.DependencyInjection.csproj",
)
JS_SDK_DIR = REPO_ROOT / "sdks" / "javascript"
PYTHON_SDK_DIR = REPO_ROOT / "sdks" / "python"
MONITOR_DIR = REPO_ROOT / "apps" / "monitor"
MONITOR_TAURI_DIR = MONITOR_DIR / "src-tauri"
MONITOR_PACKAGE_JSON = MONITOR_DIR / "package.json"
MONITOR_PACKAGE_LOCK = MONITOR_DIR / "package-lock.json"
MONITOR_TAURI_CONFIG = MONITOR_TAURI_DIR / "tauri.conf.json"
MONITOR_CARGO_TOML = MONITOR_TAURI_DIR / "Cargo.toml"
MONITOR_VERSION_METADATA = MONITOR_DIR / "src" / "generated" / "version-metadata.json"
DEFAULT_HOST_RIDS = ("win-x64", "linux-x64", "osx-arm64")
NPM_COMMAND = "npm.cmd" if os.name == "nt" else "npm"
SAFE_RELEASE_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")
MONITOR_PUBLISHABLE_BUNDLE_VARIANT_EXTENSIONS: dict[str, tuple[str, ...]] = {
    "bundle-appimage": (".AppImage",),
    "bundle-deb": (".deb",),
    "bundle-dmg": (".dmg",),
    "bundle-msi": (".msi",),
    "bundle-nsis": (".exe",),
    "bundle-rpm": (".rpm",),
}
MONITOR_NON_PUBLISHABLE_BUNDLE_VARIANTS = frozenset(
    {
        "bundle-macos",
        "bundle-share",
    }
)


def current_utc_timestamp() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def create_argument_parser(description: str) -> argparse.ArgumentParser:
    return argparse.ArgumentParser(description=description, add_help=False)


def add_release_output_arguments(
    parser: argparse.ArgumentParser,
    *,
    default_output_root: Path,
    output_root_help: str,
) -> None:
    parser.add_argument("--release-id", required=True, help="Output folder name under the selected output root.")
    parser.add_argument(
        "--output-root",
        default=str(default_output_root),
        help=output_root_help,
    )


def maybe_print_help(argv: Sequence[str], help_info: PackageHelp) -> bool:
    if "--help" not in argv:
        return False

    console_print(render_package_help(help_info))
    return True


def render_package_help(help_info: PackageHelp) -> str:
    lines = [
        help_info.command,
        "",
        help_info.summary,
    ]

    for section in help_info.sections:
        lines.extend(["", f"{section.title}:"])
        width = max((len(option.flag) for option in section.options), default=0)
        for option in section.options:
            lines.append(f"  {option.flag.ljust(width)}  {option.description}")

    return "\n".join(lines)


def build_validation_summary(records: Sequence[ValidationRecord]) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "executedAtUtc": current_utc_timestamp(),
        "records": [asdict(record) for record in records],
    }


def read_existing_validation_records(summary_path: Path) -> list[ValidationRecord]:
    if not summary_path.is_file():
        raise RuntimeError(f"无法刷新发布输出，缺少验证摘要：{summary_path}")

    payload = json.loads(summary_path.read_text(encoding="utf-8"))
    records = payload.get("records")
    if not isinstance(records, list):
        raise RuntimeError(f"验证摘要缺少 records 数组：{summary_path}")

    validation_records: list[ValidationRecord] = []
    for record in records:
        if not isinstance(record, dict):
            raise RuntimeError(f"验证摘要包含无效记录：{summary_path}")
        name = record.get("name")
        command = record.get("command")
        cwd = record.get("cwd")
        log_path = record.get("logPath")
        status = record.get("status")
        if not isinstance(name, str) or not isinstance(command, list):
            raise RuntimeError(f"验证摘要记录缺少 name 或 command：{summary_path}")
        if not isinstance(cwd, str) or not isinstance(log_path, str) or not isinstance(status, str):
            raise RuntimeError(f"验证摘要记录缺少 cwd、logPath 或 status：{summary_path}")
        validation_records.append(
            ValidationRecord(
                name=name,
                command=[str(part) for part in command],
                cwd=cwd,
                logPath=log_path,
                status=status,
            )
        )

    return validation_records


def ensure_version_metadata_consistency() -> None:
    subprocess.run(
        [sys.executable, str(VERSION_SYNC_SCRIPT), "--check"],
        cwd=REPO_ROOT,
        check=True,
        text=True,
        encoding="utf-8",
    )


def validate_release_label(value: str, field_name: str) -> str:
    normalized = value.strip()
    if not normalized:
        raise RuntimeError(f"{field_name} 不能为空。")
    if not SAFE_RELEASE_LABEL_PATTERN.fullmatch(normalized):
        raise RuntimeError(
            f"{field_name} 只能包含字母、数字、点、下划线、连字符或加号，且必须以字母或数字开头：{value!r}"
        )
    return normalized


def resolve_release_output_dir(output_root: Path, release_id: str) -> Path:
    resolved_output_root = output_root.resolve()
    candidate = (resolved_output_root / release_id).resolve()

    try:
        candidate.relative_to(resolved_output_root)
    except ValueError as exc:
        raise RuntimeError(f"release-id 解析后的输出目录超出发布根目录：{candidate}") from exc

    if candidate == resolved_output_root:
        raise RuntimeError("release-id 不能直接指向发布根目录。")

    return candidate


def remove_tree(path: Path, retries: int = 10, delay_seconds: float = 0.2) -> None:
    if not path.exists():
        return

    last_error: OSError | None = None
    for attempt in range(retries):
        try:
            shutil.rmtree(path)
            return
        except FileNotFoundError:
            return
        except OSError as exc:
            last_error = exc
            if attempt == retries - 1:
                raise
            time.sleep(delay_seconds * (attempt + 1))

    if last_error is not None:
        raise last_error


def run_logged_command(
    name: str,
    command: Sequence[str],
    cwd: Path,
    log_path: Path,
    validation_records: list[ValidationRecord],
    env_overrides: dict[str, str] | None = None,
) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    console_print(f"==> {name}")
    console_print(f"    {format_command(command)}")

    command_env = os.environ.copy()
    if env_overrides:
        command_env.update(env_overrides)

    with log_path.open("w", encoding="utf-8", errors="replace") as log_handle:
        process = subprocess.Popen(
            list(command),
            cwd=cwd,
            env=command_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        assert process.stdout is not None
        for line in process.stdout:
            write_console_text(line)
            log_handle.write(line)
        process.stdout.close()
        return_code = process.wait()

    status = "passed" if return_code == 0 else "failed"
    validation_records.append(
        ValidationRecord(
            name=name,
            command=list(command),
            cwd=str(cwd),
            logPath=str(log_path.relative_to(REPO_ROOT).as_posix()),
            status=status,
        )
    )

    if return_code != 0:
        raise RuntimeError(f"{name} failed with exit code {return_code}. See {log_path}.")


def create_zip_archive(source_dir: Path, archive_path: Path, root_name: str) -> None:
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for file_path in sorted(source_dir.rglob("*")):
            if file_path.is_dir():
                continue
            archive_name = Path(root_name) / file_path.relative_to(source_dir)
            archive.write(file_path, archive_name.as_posix())


def read_msbuild_version(project_path: Path) -> str:
    for property_name in ("Version", "VersionPrefix"):
        version = read_msbuild_property(project_path, property_name)
        if version:
            return version

    root = ElementTree.fromstring(project_path.read_text(encoding="utf-8"))
    version = root.findtext(".//Version")
    if version:
        return version.strip()
    version = root.findtext(".//VersionPrefix")
    if version:
        return version.strip()
    raise RuntimeError(f"Unable to resolve version from {project_path}.")


def read_msbuild_property(project_path: Path, property_name: str) -> str | None:
    completed = subprocess.run(
        ["dotnet", "msbuild", str(project_path), f"-getProperty:{property_name}"],
        cwd=REPO_ROOT,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    if completed.returncode != 0:
        return None

    value = completed.stdout.strip()
    return value or None


def read_json_version(path: Path) -> str:
    payload = json.loads(path.read_text(encoding="utf-8"))
    version = payload.get("version")
    if not isinstance(version, str) or not version.strip():
        raise RuntimeError(f"Unable to resolve version from {path}.")
    return version.strip()


def read_toml_version(pyproject_path: Path) -> str:
    payload = tomllib.loads(pyproject_path.read_text(encoding="utf-8"))
    version = payload.get("project", {}).get("version")
    if not isinstance(version, str) or not version.strip():
        raise RuntimeError(f"Unable to resolve version from {pyproject_path}.")
    return version.strip()


def write_json(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read_git_output(command: Sequence[str]) -> str:
    completed = subprocess.run(
        list(command),
        cwd=REPO_ROOT,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    return completed.stdout


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def describe_release_assets(paths: Sequence[Path]) -> list[ReleaseAsset]:
    return [describe_release_asset(path) for path in paths]


def describe_release_asset(path: Path) -> ReleaseAsset:
    return ReleaseAsset(
        path=path,
        category=categorize_asset(path),
        target=asset_target(path),
    )


def release_asset_sort_key(asset: ReleaseAsset) -> tuple[int, str, int, str]:
    category_order = {
        "host": 0,
        "sdk-dotnet": 1,
        "sdk-javascript": 2,
        "sdk-python-sdist": 3,
        "sdk-python-wheel": 4,
        "monitor-app": 5,
        "auxiliary": 6,
    }
    return (
        category_order.get(asset.category, 99),
        asset.target,
        HOST_VARIANT_ORDER.get(asset.variant or "", 99),
        asset.path.name,
    )


def categorize_asset(path: Path) -> str:
    if path.parent.name == "host":
        return "host"
    if path.suffix in {".nupkg", ".snupkg"}:
        return "sdk-dotnet"
    if path.suffix == ".tgz":
        return "sdk-javascript"
    if path.suffix == ".whl":
        return "sdk-python-wheel"
    if path.suffixes[-2:] == [".tar", ".gz"]:
        return "sdk-python-sdist"
    return "auxiliary"


def asset_target(path: Path) -> str:
    name = path.name
    for rid in DEFAULT_HOST_RIDS:
        if rid in name:
            return rid
    if path.suffix in {".nupkg", ".snupkg"}:
        return "dotnet"
    if path.suffix == ".tgz":
        return "javascript"
    if path.suffix == ".whl":
        return "python-wheel"
    if path.suffixes[-2:] == [".tar", ".gz"]:
        return "python-sdist"
    return "n/a"


def is_publishable_monitor_asset(*, category: str, name: str) -> bool:
    if not category.startswith("bundle-"):
        return False

    allowed_suffixes = MONITOR_PUBLISHABLE_BUNDLE_VARIANT_EXTENSIONS.get(category)
    if allowed_suffixes is not None:
        return any(name.endswith(suffix) for suffix in allowed_suffixes)

    if category in MONITOR_NON_PUBLISHABLE_BUNDLE_VARIANTS:
        return False

    raise RuntimeError(f"未识别的 Monitor bundle 资产分类：category={category!r}, name={name!r}")


def format_command(command: Sequence[str]) -> str:
    return " ".join(shlex.quote(part) for part in command)
