from __future__ import annotations

import argparse
import json
import platform
import shutil
import sys
from pathlib import Path
from typing import Sequence

import tomllib

from package_models import (
    MonitorPackageOptions,
    MonitorPackageResult,
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
    ReleaseAsset,
    ValidationRecord,
)
from package_shared import (
    MONITOR_CARGO_TOML,
    MONITOR_DIR,
    MONITOR_PACKAGE_JSON,
    MONITOR_PACKAGE_LOCK,
    MONITOR_TAURI_CONFIG,
    MONITOR_TAURI_DIR,
    MONITOR_VERSION_METADATA,
    NPM_COMMAND,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    current_utc_timestamp,
    maybe_print_help,
    read_json_version,
    remove_tree,
    resolve_release_output_dir,
    run_logged_command,
    sha256_file,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "monitor"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_monitor.py --help",
        summary="Validate and package the DevHub Monitor app for the current platform with stable manifest and notes output.",
        sections=(
            PackageHelpSection(
                title="Common Parameters",
                options=(
                    PackageHelpOption("--help", "Print this capability and parameter summary without validation or packaging."),
                    PackageHelpOption("--release-id <id>", "Output folder name under the selected output root."),
                    PackageHelpOption(
                        "--output-root <dir>",
                        f"Directory that contains release-id subdirectories. Default: {DEFAULT_OUTPUT_ROOT}",
                    ),
                ),
            ),
            PackageHelpSection(
                title="Monitor Parameters",
                options=(
                    PackageHelpOption(
                        "--verify-only",
                        "Run version checks and Monitor validation, then stop before Tauri bundle generation.",
                    ),
                    PackageHelpOption(
                        "--validated-externally",
                        "Assume Monitor verification already completed upstream and only install dependencies plus build the bundle.",
                    ),
                    PackageHelpOption(
                        "-- <tauri-args...>",
                        "Forward the remaining arguments to `npm run tauri:build -- ...`.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub Monitor release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for Monitor packaging output.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run Monitor validation without invoking tauri build.",
    )
    parser.add_argument(
        "--validated-externally",
        action="store_true",
        help="Assume Monitor verification already completed upstream and only perform packaging steps.",
    )
    parser.add_argument(
        "tauri_args",
        nargs=argparse.REMAINDER,
        help="Additional arguments passed to `npm run tauri:build -- ...`.",
    )
    return parser.parse_args(argv)


def normalize_tauri_args(tauri_args: Sequence[str]) -> tuple[str, ...]:
    if tauri_args and tauri_args[0] == "--":
        return tuple(tauri_args[1:])
    return tuple(tauri_args)


def package_monitor(options: MonitorPackageOptions) -> MonitorPackageResult:
    if options.output_dir.exists():
        remove_tree(options.output_dir)

    options.checks_dir.mkdir(parents=True, exist_ok=True)

    validation_records: list[ValidationRecord] = []
    versions = ensure_monitor_version_consistency()

    prepare_monitor_workspace(options.checks_dir, validation_records)
    sync_monitor_version_metadata(options.checks_dir, validation_records)
    if not options.skip_validation:
        run_monitor_validation(options.checks_dir, validation_records)
    versions.update(read_monitor_version_metadata(expected_monitor_version=versions["monitor"]))
    write_json(options.checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    target_platform = current_platform_tag()
    if options.verify_only:
        return MonitorPackageResult(
            output_dir=options.output_dir,
            target_platform=target_platform,
            assets=tuple(),
            versions=versions,
            manifest=None,
            validation_records=tuple(validation_records),
        )

    asset_paths = build_monitor_assets(
        output_dir=options.output_dir,
        checks_dir=options.checks_dir,
        validation_records=validation_records,
        tauri_args=options.tauri_args,
    )
    manifest = build_manifest(
        output_dir=options.output_dir,
        release_id=options.release_id,
        versions=versions,
        asset_paths=asset_paths,
    )
    write_json(options.output_dir / "release-manifest.json", manifest)
    write_release_notes(output_dir=options.output_dir, manifest=manifest)

    ensure_asset_integrity(output_dir=options.output_dir, validation_records=validation_records)
    write_json(options.checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    return MonitorPackageResult(
        output_dir=options.output_dir,
        target_platform=target_platform,
        assets=tuple(describe_monitor_release_assets(options.output_dir, manifest)),
        versions=versions,
        manifest=manifest,
        validation_records=tuple(validation_records),
    )


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


def run_monitor_validation(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    run_logged_command(
        name="Monitor verify",
        command=[NPM_COMMAND, "run", "verify"],
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-verify.log",
        validation_records=validation_records,
    )


def prepare_monitor_workspace(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    run_logged_command(
        name="Monitor install",
        command=[NPM_COMMAND, "ci"],
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-install.log",
        validation_records=validation_records,
    )


def sync_monitor_version_metadata(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    run_logged_command(
        name="Monitor sync version metadata",
        command=[NPM_COMMAND, "run", "sync:version-metadata"],
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-sync-version-metadata.log",
        validation_records=validation_records,
    )


def build_monitor_assets(
    output_dir: Path,
    checks_dir: Path,
    validation_records: list[ValidationRecord],
    tauri_args: Sequence[str],
) -> list[Path]:
    bundle_source_dir = MONITOR_TAURI_DIR / "target" / "release" / "bundle"
    bundle_output_dir = output_dir / "bundle"

    if bundle_source_dir.exists():
        remove_tree(bundle_source_dir)
    if bundle_output_dir.exists():
        remove_tree(bundle_output_dir)

    command = [NPM_COMMAND, "run", "tauri:build"]
    if tauri_args:
        command.extend(["--", *tauri_args])

    run_logged_command(
        name="Monitor bundle build",
        command=command,
        cwd=MONITOR_DIR,
        log_path=checks_dir / "monitor-bundle-build.log",
        validation_records=validation_records,
    )

    if not bundle_source_dir.exists():
        raise RuntimeError(f"未找到 Monitor bundle 输出目录：{bundle_source_dir}")

    shutil.copytree(bundle_source_dir, bundle_output_dir)
    asset_paths = sorted(path for path in bundle_output_dir.rglob("*") if path.is_file())
    if not asset_paths:
        raise RuntimeError("Monitor bundle 输出目录为空。")

    return asset_paths


def build_manifest(
    output_dir: Path,
    release_id: str,
    versions: dict[str, str],
    asset_paths: Sequence[Path],
) -> dict[str, object]:
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
        "generatedAtUtc": current_utc_timestamp(),
        "targetPlatform": current_platform_tag(),
        "versions": versions,
        "sdkSource": "repository-source",
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
        "",
        "## Assets",
        "",
        "| Name | Category | SHA256 |",
        "| --- | --- | --- |",
    ]

    for asset in manifest["assets"]:
        lines.append(f"| `{asset['name']}` | `{asset['category']}` | `{asset['sha256']}` |")

    lines.extend(
        [
            "",
            "## Validation",
            "",
            "- Packaging and integrity steps for this output are recorded in `checks/validation-summary.json`.",
            "- Workflow-driven releases may satisfy same-grade verification in upstream CI jobs before bundling.",
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


def copy_monitor_package_outputs(output_dir: Path, monitor_assets_root: Path) -> list[ReleaseAsset]:
    package_dirs = find_monitor_package_dirs(monitor_assets_root)
    if not package_dirs:
        raise RuntimeError(f"未找到 Monitor 打包输出：{monitor_assets_root}")

    monitor_output_root = output_dir / "monitor"
    if monitor_output_root.exists():
        remove_tree(monitor_output_root)
    monitor_output_root.mkdir(parents=True, exist_ok=True)

    assets: list[ReleaseAsset] = []
    seen_platforms: set[str] = set()
    for package_dir in package_dirs:
        manifest_path = package_dir / "release-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("product") != "monitor":
            raise RuntimeError(f"Monitor manifest product 字段无效：{manifest_path}")
        target_platform = manifest.get("targetPlatform")
        if not isinstance(target_platform, str) or not target_platform.strip():
            raise RuntimeError(f"Monitor manifest 缺少 targetPlatform：{manifest_path}")
        target_platform = validate_release_label(target_platform.strip(), field_name="monitor-target-platform")
        if target_platform in seen_platforms:
            raise RuntimeError(f"重复的 Monitor 目标平台：{target_platform}")
        seen_platforms.add(target_platform)

        destination_dir = monitor_output_root / target_platform
        shutil.copytree(package_dir, destination_dir)
        assets.extend(describe_monitor_release_assets(destination_dir, manifest))

    return assets


def find_monitor_package_dirs(monitor_assets_root: Path) -> list[Path]:
    if not monitor_assets_root.exists():
        raise RuntimeError(f"Monitor 资产根目录不存在：{monitor_assets_root}")

    manifests = sorted(monitor_assets_root.rglob("release-manifest.json"))
    return sorted({manifest.parent.resolve() for manifest in manifests})


def describe_monitor_release_assets(package_dir: Path, manifest: dict[str, object]) -> list[ReleaseAsset]:
    target_platform = str(manifest["targetPlatform"])
    versions = manifest.get("versions")
    if not isinstance(versions, dict):
        raise RuntimeError(f"Monitor manifest 缺少 versions：{package_dir / 'release-manifest.json'}")
    monitor_version = versions.get("monitor")
    javascript_sdk_version = versions.get("sdk")
    if not isinstance(monitor_version, str) or not isinstance(javascript_sdk_version, str):
        raise RuntimeError(f"Monitor manifest 缺少 Monitor 或 JS SDK 版本：{package_dir / 'release-manifest.json'}")

    manifest_assets = manifest.get("assets")
    if not isinstance(manifest_assets, list):
        raise RuntimeError(f"Monitor manifest 缺少 assets 数组：{package_dir / 'release-manifest.json'}")

    assets: list[ReleaseAsset] = []
    for asset in manifest_assets:
        if not isinstance(asset, dict):
            raise RuntimeError(f"Monitor manifest 包含无效资产条目：{package_dir / 'release-manifest.json'}")
        relative_path = asset.get("path")
        category = asset.get("category")
        if not isinstance(relative_path, str) or not isinstance(category, str):
            raise RuntimeError(f"Monitor manifest 资产缺少 path 或 category：{package_dir / 'release-manifest.json'}")
        asset_path = (package_dir / relative_path).resolve()
        try:
            asset_path.relative_to(package_dir.resolve())
        except ValueError as exc:
            raise RuntimeError(f"Monitor 资产路径超出平台目录：{asset_path}") from exc
        assets.append(
            ReleaseAsset(
                path=asset_path,
                category="monitor-app",
                target=target_platform,
                variant=category,
                monitorVersion=monitor_version,
                javascriptSdkVersion=javascript_sdk_version,
            )
        )

    return assets


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)
    if maybe_print_help(args_list, build_help()):
        return 0

    args = parse_args(args_list)
    if args.verify_only and args.validated_externally:
        raise RuntimeError("`--verify-only` 与 `--validated-externally` 不能同时使用。")
    output_root = Path(args.output_root).resolve()
    release_id = validate_release_label(args.release_id, field_name="release-id")
    output_dir = resolve_release_output_dir(output_root, release_id)
    checks_dir = output_dir / "checks"

    result = package_monitor(
        MonitorPackageOptions(
            release_id=release_id,
            output_dir=output_dir,
            checks_dir=checks_dir,
            tauri_args=normalize_tauri_args(args.tauri_args),
            verify_only=bool(args.verify_only),
            skip_validation=bool(args.validated_externally),
        )
    )

    if args.verify_only:
        print(f"Monitor validation ready: {checks_dir}")
    else:
        print(f"Monitor release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f"Monitor packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
