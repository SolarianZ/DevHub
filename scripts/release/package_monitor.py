from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import shlex
import shutil
import subprocess
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence

import tomllib


REPO_ROOT = Path(__file__).resolve().parents[2]
MONITOR_DIR = REPO_ROOT / "apps" / "monitor"
MONITOR_TAURI_DIR = MONITOR_DIR / "src-tauri"
MONITOR_PACKAGE_JSON = MONITOR_DIR / "package.json"
MONITOR_PACKAGE_LOCK = MONITOR_DIR / "package-lock.json"
MONITOR_TAURI_CONFIG = MONITOR_TAURI_DIR / "tauri.conf.json"
MONITOR_CARGO_TOML = MONITOR_TAURI_DIR / "Cargo.toml"
MONITOR_VERSION_METADATA = MONITOR_DIR / "src" / "generated" / "version-metadata.json"
NPM_COMMAND = "npm.cmd" if os.name == "nt" else "npm"
SAFE_RELEASE_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")
MONITOR_SDK_SOURCE_ENV = "DEVHUB_MONITOR_SDK_SOURCE"
MONITOR_SDK_SOURCE_CHOICES = ("release", "local-src")


@dataclass
class ValidationRecord:
    name: str
    command: list[str]
    cwd: str
    logPath: str
    status: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build DevHub Monitor release assets and validation output.")
    parser.add_argument("--release-id", required=True, help="Output folder name under artifacts/monitor.")
    parser.add_argument(
        "--output-root",
        default=str(REPO_ROOT / "artifacts" / "monitor"),
        help="Directory that contains release-id subdirectories.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run validation and version consistency checks without invoking tauri build.",
    )
    parser.add_argument(
        "--sdk-source",
        choices=MONITOR_SDK_SOURCE_CHOICES,
        default="release",
        help="JS SDK source for Monitor validation and packaging. Use local-src only for local development bundles.",
    )
    parser.add_argument(
        "tauri_args",
        nargs=argparse.REMAINDER,
        help="Additional arguments passed to `npm run tauri:build -- ...`.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output_root = Path(args.output_root).resolve()
    release_id = validate_release_label(args.release_id, field_name="release-id")
    output_dir = resolve_release_output_dir(output_root, release_id)
    checks_dir = output_dir / "checks"
    sdk_source = args.sdk_source

    if output_dir.exists():
        remove_tree(output_dir)

    checks_dir.mkdir(parents=True, exist_ok=True)
    validation_records: list[ValidationRecord] = []
    versions = ensure_monitor_version_consistency()

    run_monitor_validation(checks_dir, validation_records, sdk_source=sdk_source)
    versions.update(read_monitor_version_metadata(expected_monitor_version=versions["monitor"]))
    write_json(checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    if args.verify_only:
        print(f"Monitor validation ready: {checks_dir}")
        return 0

    asset_paths = build_monitor_assets(
        output_dir=output_dir,
        checks_dir=checks_dir,
        validation_records=validation_records,
        sdk_source=sdk_source,
        tauri_args=normalize_tauri_args(args.tauri_args),
    )
    manifest = build_manifest(
        output_dir=output_dir,
        release_id=release_id,
        versions=versions,
        asset_paths=asset_paths,
        sdk_source=sdk_source,
    )
    write_json(output_dir / "release-manifest.json", manifest)
    write_release_notes(output_dir=output_dir, manifest=manifest)

    ensure_asset_integrity(output_dir=output_dir, validation_records=validation_records)
    write_json(checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    print(f"Monitor release assets ready: {output_dir}")
    return 0


def normalize_tauri_args(tauri_args: Sequence[str]) -> list[str]:
    if tauri_args and tauri_args[0] == "--":
        return list(tauri_args[1:])
    return list(tauri_args)


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


def ensure_monitor_version_consistency() -> dict[str, str]:
    package_json_version = read_json_version(MONITOR_PACKAGE_JSON)

    package_lock_payload = json.loads(MONITOR_PACKAGE_LOCK.read_text(encoding="utf-8"))
    package_lock_version = package_lock_payload.get("version")
    package_lock_root_version = package_lock_payload.get("packages", {}).get("", {}).get("version")

    tauri_config_version = read_json_version(MONITOR_TAURI_CONFIG)
    cargo_toml_payload = tomllib.loads(MONITOR_CARGO_TOML.read_text(encoding="utf-8"))
    cargo_version = cargo_toml_payload.get("package", {}).get("version")

    versions = {
        "packageJson": package_json_version,
        "packageLock": package_lock_version,
        "packageLockRoot": package_lock_root_version,
        "tauriConfig": tauri_config_version,
        "cargoToml": cargo_version,
    }

    normalized_versions = {name: value for name, value in versions.items() if isinstance(value, str) and value.strip()}
    if len(normalized_versions) != len(versions):
        missing = sorted(set(versions) - set(normalized_versions))
        raise RuntimeError(f"Monitor 版本元数据缺失：{', '.join(missing)}。")

    distinct_versions = sorted(set(normalized_versions.values()))
    if len(distinct_versions) != 1:
        details = ", ".join(f"{name}={value!r}" for name, value in normalized_versions.items())
        raise RuntimeError(f"Monitor 版本元数据不一致：{details}")

    return {
        "monitor": distinct_versions[0],
        **normalized_versions,
    }


def read_monitor_version_metadata(expected_monitor_version: str) -> dict[str, str]:
    if not MONITOR_VERSION_METADATA.is_file():
        raise RuntimeError(f"未找到 Monitor 共享版本元数据：{MONITOR_VERSION_METADATA}")

    payload = json.loads(MONITOR_VERSION_METADATA.read_text(encoding="utf-8"))
    monitor_version = payload.get("monitorVersion")
    sdk_version = payload.get("sdkVersion")
    if not isinstance(monitor_version, str) or not monitor_version.strip():
        raise RuntimeError(f"Monitor 共享版本元数据缺少有效 monitorVersion：{MONITOR_VERSION_METADATA}")
    if not isinstance(sdk_version, str) or not sdk_version.strip():
        raise RuntimeError(f"Monitor 共享版本元数据缺少有效 sdkVersion：{MONITOR_VERSION_METADATA}")
    if monitor_version.strip() != expected_monitor_version:
        raise RuntimeError(
            "Monitor 共享版本元数据与 package.json/tauri/Cargo 版本不一致："
            f" metadata={monitor_version!r}, expected={expected_monitor_version!r}"
        )

    return {
        "sdk": sdk_version.strip(),
    }


def run_monitor_validation(
    checks_dir: Path,
    validation_records: list[ValidationRecord],
    sdk_source: str,
) -> None:
    run_logged_command(
        name="Monitor install",
        command=[NPM_COMMAND, "ci"],
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-install.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Monitor verify",
        command=[NPM_COMMAND, "run", "verify"],
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-verify.log",
        validation_records=validation_records,
        env_overrides={MONITOR_SDK_SOURCE_ENV: sdk_source},
    )


def build_monitor_assets(
    output_dir: Path,
    checks_dir: Path,
    validation_records: list[ValidationRecord],
    sdk_source: str,
    tauri_args: Sequence[str],
) -> list[Path]:
    bundle_source_dir = MONITOR_TAURI_DIR / "target" / "release" / "bundle"
    bundle_output_dir = output_dir / "bundle"

    if bundle_source_dir.exists():
        remove_tree(bundle_source_dir)

    command = [NPM_COMMAND, "run", "tauri:build"]
    if tauri_args:
        command.extend(["--", *tauri_args])

    run_logged_command(
        name="Monitor bundle build",
        command=command,
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-bundle-build.log",
        validation_records=validation_records,
        env_overrides={MONITOR_SDK_SOURCE_ENV: sdk_source},
    )

    if not bundle_source_dir.exists():
        raise RuntimeError(f"未找到 Monitor bundle 输出目录：{bundle_source_dir}")

    shutil.copytree(bundle_source_dir, bundle_output_dir)
    asset_paths = sorted(path for path in bundle_output_dir.rglob("*") if path.is_file())
    if not asset_paths:
        raise RuntimeError("Monitor bundle 输出目录为空。")

    return asset_paths


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


def build_manifest(
    output_dir: Path,
    release_id: str,
    versions: dict[str, str],
    asset_paths: Sequence[Path],
    sdk_source: str,
) -> dict[str, object]:
    generated_at = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    assets: list[dict[str, object]] = []

    for asset_path in asset_paths:
        relative_path = asset_path.relative_to(output_dir).as_posix()
        assets.append(
            {
                "name": asset_path.name,
                "category": categorize_monitor_asset(asset_path.relative_to(output_dir)),
                "path": relative_path,
                "sha256": sha256_file(asset_path),
                "sizeBytes": asset_path.stat().st_size,
            }
        )

    return {
        "schemaVersion": 1,
        "product": "monitor",
        "releaseId": release_id,
        "generatedAtUtc": generated_at,
        "targetPlatform": current_platform_tag(),
        "versions": versions,
        "sdkSource": sdk_source,
        "developmentOnly": sdk_source != "release",
        "assets": assets,
        "validation": {
            "executed": True,
            "summaryPath": "checks/validation-summary.json",
        },
        "entryPoints": {
            "monitorReadme": "apps/monitor/README.md",
            "script": "scripts/release/package_monitor.py",
        },
    }


def categorize_monitor_asset(relative_path: Path) -> str:
    parts = relative_path.parts
    if len(parts) >= 2 and parts[0] == "bundle":
        return f"bundle-{parts[1]}"
    if parts:
        return parts[0]
    return "bundle"


def current_platform_tag() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    machine_aliases = {
        "amd64": "x64",
        "x86_64": "x64",
        "aarch64": "arm64",
    }
    return f"{system}-{machine_aliases.get(machine, machine)}"


def write_release_notes(output_dir: Path, manifest: dict[str, object]) -> None:
    lines = [
        "# DevHub Monitor Release",
        "",
        f"- Release ID: `{manifest['releaseId']}`",
        f"- Version: `{manifest['versions']['monitor']}`",
        f"- JS SDK Version: `{manifest['versions']['sdk']}`",
        f"- SDK Source: `{manifest['sdkSource']}`",
        f"- Target Platform: `{manifest['targetPlatform']}`",
        f"- Generated At (UTC): `{manifest['generatedAtUtc']}`",
    ]

    if manifest["developmentOnly"]:
        lines.extend(
            [
                "- Package Type: `development-only`",
                "- This package was built against `local-src` for local SDK/Monitor integration work and is not a formal release candidate.",
            ]
        )

    lines.extend(
        [
            "",
            "## Assets",
            "",
            "| Name | Category | SHA256 |",
            "| --- | --- | --- |",
        ]
    )

    for asset in manifest["assets"]:
        lines.append(f"| `{asset['name']}` | `{asset['category']}` | `{asset['sha256']}` |")

    lines.extend(
        [
            "",
            "## Validation",
            "",
            "- Validation executed locally through `python scripts/release/package_monitor.py`.",
            "- Summary file: `checks/validation-summary.json`.",
            "- This packaging flow is not wired into the current GitHub Release workflow.",
            "",
            "## Next Steps",
            "",
            "- Monitor workspace: `apps/monitor/README.md`",
            "- Repository release flow: `docs/developer/publishing/release-process.md`",
            "",
        ]
    )

    (output_dir / "release-notes.md").write_text("\n".join(lines), encoding="utf-8")


def ensure_asset_integrity(output_dir: Path, validation_records: list[ValidationRecord]) -> None:
    required_paths = [
        output_dir / "release-manifest.json",
        output_dir / "release-notes.md",
        output_dir / "checks" / "validation-summary.json",
    ]

    for path in required_paths:
        if not path.exists():
            raise RuntimeError(f"Missing required monitor release asset: {path}")

    bundle_files = sorted(path for path in (output_dir / "bundle").rglob("*") if path.is_file())
    if not bundle_files:
        raise RuntimeError(f"Monitor bundle 输出为空：{output_dir / 'bundle'}")

    validation_records.append(
        ValidationRecord(
            name="Monitor release asset integrity",
            command=["internal-check", "monitor-release-assets"],
            cwd=str(REPO_ROOT),
            logPath="checks/validation-summary.json",
            status="passed",
        )
    )


def build_validation_summary(records: Sequence[ValidationRecord]) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "executedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "records": [asdict(record) for record in records],
    }


def run_logged_command(
    name: str,
    command: Sequence[str],
    cwd: Path,
    log_path: Path,
    validation_records: list[ValidationRecord],
    env_overrides: dict[str, str] | None = None,
) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    print(f"==> {name}")
    print(f"    {format_command(command)}")

    with log_path.open("w", encoding="utf-8", errors="replace") as log_handle:
        command_env = os.environ.copy()
        if env_overrides:
            command_env.update(env_overrides)
        process = subprocess.Popen(
            list(command),
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=command_env,
        )
        assert process.stdout is not None
        for line in process.stdout:
            sys.stdout.write(line)
            log_handle.write(line)
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


def read_json_version(path: Path) -> str:
    payload = json.loads(path.read_text(encoding="utf-8"))
    version = payload.get("version")
    if not isinstance(version, str) or not version.strip():
        raise RuntimeError(f"Unable to resolve version from {path}.")
    return version.strip()


def write_json(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def format_command(command: Sequence[str]) -> str:
    return " ".join(shlex.quote(part) for part in command)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f"Monitor packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
