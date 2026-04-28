from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Sequence

from package_dotnet_sdk import package_dotnet_sdk
from package_host import package_host
from package_js_sdk import package_js_sdk
from package_models import (
    DotNetSdkPackageOptions,
    HostPackageOptions,
    JavaScriptSdkPackageOptions,
    MonitorPackageOptions,
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
    PythonSdkPackageOptions,
    ReleaseAsset,
    ReleasePackageOptions,
    ReleasePackageResult,
    ValidationRecord,
)
from package_monitor import copy_monitor_package_outputs, package_monitor
from package_py_sdk import package_py_sdk
from package_shared import (
    DEFAULT_HOST_RIDS,
    DOTNET_SDK_PROJECTS,
    HOST_PROJECT,
    HOST_VARIANTS,
    JS_SDK_DIR,
    MONITOR_PACKAGE_JSON,
    PYTHON_SDK_DIR,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    current_utc_timestamp,
    describe_release_assets,
    ensure_version_metadata_consistency,
    maybe_print_help,
    read_existing_validation_records,
    read_git_output,
    read_json_version,
    read_msbuild_version,
    read_toml_version,
    release_asset_sort_key,
    remove_tree,
    resolve_release_output_dir,
    sha256_file,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "release"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_release.py --help",
        summary="Build the full DevHub local release candidate by orchestrating Host, SDK, and optional Monitor component packagers.",
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
                title="Release Parameters",
                options=(
                    PackageHelpOption("--channel <local|preview|main-snapshot|stable>", "Release channel identifier."),
                    PackageHelpOption("--release-tag <tag>", "GitHub Release tag recorded in the manifest. Defaults to release-id."),
                    PackageHelpOption("--release-name <name>", "Release display name recorded in the manifest."),
                    PackageHelpOption("--commit <sha>", "Commit SHA recorded in the manifest and release notes."),
                    PackageHelpOption(
                        "--host-rid <rid>",
                        "Override the Host RID matrix. Repeat for multiple targets. Defaults to win-x64, linux-x64, osx-arm64.",
                    ),
                    PackageHelpOption("--skip-monitor", "Skip local Monitor packaging for preview/main-snapshot channels."),
                    PackageHelpOption(
                        "--validated-externally",
                        "Assume same-grade Host/SDK/Monitor verification already completed upstream and only run packaging/assembly steps.",
                    ),
                    PackageHelpOption(
                        "--monitor-assets-root <dir>",
                        "Merge Monitor package outputs from an external root instead of building them locally.",
                    ),
                    PackageHelpOption(
                        "--reuse-existing-output",
                        "Refresh manifest, notes, and integrity checks from an existing release output directory.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for full release packaging output.",
    )
    parser.add_argument(
        "--channel",
        required=True,
        choices=("local", "preview", "main-snapshot", "stable"),
        help="Release channel identifier.",
    )
    parser.add_argument("--release-tag", help="GitHub Release tag associated with this build.")
    parser.add_argument("--release-name", help="GitHub Release display name associated with this build.")
    parser.add_argument("--commit", help="Commit SHA for manifest and release notes.")
    parser.add_argument(
        "--host-rid",
        action="append",
        dest="host_rids",
        help="Host RID to publish. Can be passed multiple times. Defaults to win-x64/linux-x64/osx-arm64.",
    )
    parser.add_argument(
        "--skip-monitor",
        action="store_true",
        help="Do not run local Monitor packaging for preview/main-snapshot channels.",
    )
    parser.add_argument(
        "--validated-externally",
        action="store_true",
        help="Assume same-grade verification already completed upstream and only perform packaging/assembly steps.",
    )
    parser.add_argument(
        "--monitor-assets-root",
        help="Directory containing platform Monitor package outputs to merge into the release output.",
    )
    parser.add_argument(
        "--reuse-existing-output",
        action="store_true",
        help="Refresh manifest, notes, and integrity checks from an existing release output directory.",
    )
    return parser.parse_args(argv)


def package_release(options: ReleasePackageOptions) -> ReleasePackageResult:
    ensure_version_metadata_consistency()

    output_dir = resolve_release_output_dir(options.output_root, options.release_id)
    checks_dir = output_dir / "checks"
    monitor_assets_root = options.monitor_assets_root.resolve() if options.monitor_assets_root is not None else None

    if options.reuse_existing_output:
        if not output_dir.exists():
            raise RuntimeError(f"无法刷新不存在的发布输出目录：{output_dir}")
    elif output_dir.exists():
        remove_tree(output_dir)

    checks_dir.mkdir(parents=True, exist_ok=True)
    validation_records = (
        [record for record in read_existing_validation_records(checks_dir / "validation-summary.json") if record.name != "Release asset integrity"]
        if options.reuse_existing_output
        else []
    )

    if options.reuse_existing_output:
        assets = discover_core_release_assets(output_dir, options.host_rids)
        versions = collect_release_versions()
    else:
        assets, versions = build_core_release_assets(
            output_dir=output_dir,
            checks_dir=checks_dir,
            host_rids=options.host_rids,
            validation_records=validation_records,
            skip_validation=options.validated_externally,
        )

    include_local_monitor = options.channel in {"preview", "main-snapshot"} and not options.skip_monitor
    monitor_assets: list[ReleaseAsset] = []
    if monitor_assets_root is not None:
        if options.channel == "stable":
            raise RuntimeError("stable 渠道不接受 Monitor App 发布资产。")
        monitor_assets.extend(copy_monitor_package_outputs(output_dir, monitor_assets_root))
    elif include_local_monitor:
        local_monitor_assets, monitor_records = package_local_monitor_assets(
            output_dir=output_dir,
            release_id=options.release_id,
            skip_validation=options.validated_externally,
        )
        monitor_assets.extend(local_monitor_assets)
        validation_records.extend(monitor_records)
    assets.extend(monitor_assets)

    write_json(checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    generated_at = current_utc_timestamp()
    manifest = build_manifest(
        output_dir=output_dir,
        assets=assets,
        versions=versions,
        release_id=options.release_id,
        channel=options.channel,
        release_tag=options.release_tag,
        release_name=options.release_name,
        commit=options.commit,
        generated_at=generated_at,
    )
    write_json(output_dir / "release-manifest.json", manifest)
    write_release_notes(
        output_dir=output_dir,
        manifest=manifest,
        release_name=options.release_name,
    )

    ensure_asset_integrity(
        output_dir=output_dir,
        manifest=manifest,
        host_rids=options.host_rids,
        validation_records=validation_records,
        require_monitor=bool(monitor_assets),
    )
    write_json(checks_dir / "validation-summary.json", build_validation_summary(validation_records))

    return ReleasePackageResult(
        output_dir=output_dir,
        assets=tuple(assets),
        versions=versions,
        manifest=manifest,
        includes_monitor=bool(monitor_assets),
        validation_records=tuple(validation_records),
    )


def collect_release_versions() -> dict[str, str]:
    return {
        "host": read_msbuild_version(HOST_PROJECT),
        "dotnetSdk": read_msbuild_version(DOTNET_SDK_PROJECTS[0]),
        "dotnetSdkDependencyInjection": read_msbuild_version(DOTNET_SDK_PROJECTS[1]),
        "javascriptSdk": read_json_version(JS_SDK_DIR / "package.json"),
        "pythonSdk": read_toml_version(PYTHON_SDK_DIR / "pyproject.toml"),
        "monitor": read_json_version(MONITOR_PACKAGE_JSON),
    }


def build_core_release_assets(
    *,
    output_dir: Path,
    checks_dir: Path,
    host_rids: tuple[str, ...],
    validation_records: list[ValidationRecord],
    skip_validation: bool,
) -> tuple[list[ReleaseAsset], dict[str, str]]:
    assets: list[ReleaseAsset] = []
    versions: dict[str, str] = {}

    host_result = package_host(
        HostPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            host_rids=host_rids,
            skip_validation=skip_validation,
        )
    )
    assets.extend(host_result.assets)
    versions.update(host_result.versions)
    validation_records.extend(host_result.validation_records)

    dotnet_result = package_dotnet_sdk(
        DotNetSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            skip_validation=skip_validation,
        )
    )
    assets.extend(dotnet_result.assets)
    versions.update(dotnet_result.versions)
    validation_records.extend(dotnet_result.validation_records)

    javascript_result = package_js_sdk(
        JavaScriptSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            skip_validation=skip_validation,
        )
    )
    assets.extend(javascript_result.assets)
    versions.update(javascript_result.versions)
    validation_records.extend(javascript_result.validation_records)

    python_result = package_py_sdk(
        PythonSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            skip_validation=skip_validation,
        )
    )
    assets.extend(python_result.assets)
    versions.update(python_result.versions)
    validation_records.extend(python_result.validation_records)

    versions["monitor"] = read_json_version(MONITOR_PACKAGE_JSON)
    return assets, versions


def discover_core_release_assets(output_dir: Path, host_rids: Sequence[str]) -> list[ReleaseAsset]:
    assets: list[ReleaseAsset] = []
    host_dir = output_dir / "host"
    for rid in host_rids:
        for variant in HOST_VARIANTS:
            assets.append(
                ReleaseAsset(
                    path=host_dir / variant.archive_name(rid),
                    category="host",
                    target=rid,
                    variant=variant.name,
                )
            )

    for relative_dir in (
        Path("sdk") / "dotnet",
        Path("sdk") / "javascript",
        Path("sdk") / "python",
    ):
        assets.extend(describe_release_assets(sorted((output_dir / relative_dir).glob("*"))))

    return assets


def package_local_monitor_assets(
    *,
    output_dir: Path,
    release_id: str,
    skip_validation: bool,
) -> tuple[list[ReleaseAsset], tuple[ValidationRecord, ...]]:
    monitor_staging_root = output_dir / ".monitor-staging"
    if monitor_staging_root.exists():
        remove_tree(monitor_staging_root)

    try:
        monitor_output_dir = resolve_release_output_dir(monitor_staging_root, release_id)
        monitor_result = package_monitor(
            MonitorPackageOptions(
                release_id=release_id,
                output_dir=monitor_output_dir,
                checks_dir=monitor_output_dir / "checks",
                skip_validation=skip_validation,
            )
        )
        return copy_monitor_package_outputs(output_dir, monitor_staging_root), monitor_result.validation_records
    finally:
        if monitor_staging_root.exists():
            remove_tree(monitor_staging_root)


def build_manifest(
    output_dir: Path,
    assets: Sequence[ReleaseAsset],
    versions: dict[str, str],
    release_id: str,
    channel: str,
    release_tag: str,
    release_name: str,
    commit: str,
    generated_at: str,
) -> dict[str, object]:
    manifest_assets = []
    for asset in sorted(assets, key=release_asset_sort_key):
        relative_path = asset.path.relative_to(output_dir).as_posix()
        asset_entry = {
            "name": asset.path.name,
            "category": asset.category,
            "target": asset.target,
            "path": relative_path,
            "sha256": sha256_file(asset.path),
            "sizeBytes": asset.path.stat().st_size,
        }
        if asset.variant is not None:
            asset_entry["variant"] = asset.variant
        if asset.monitorVersion is not None:
            asset_entry["monitorVersion"] = asset.monitorVersion
        if asset.javascriptSdkVersion is not None:
            asset_entry["javascriptSdkVersion"] = asset.javascriptSdkVersion
        manifest_assets.append(asset_entry)

    monitor_packages = build_monitor_package_entries(output_dir)

    manifest: dict[str, object] = {
        "schemaVersion": 1,
        "releaseId": release_id,
        "releaseName": release_name,
        "channel": channel,
        "releaseTag": release_tag,
        "commit": commit,
        "generatedAtUtc": generated_at,
        "versions": versions,
        "assets": manifest_assets,
        "validation": {
            "executed": True,
            "summaryPath": "checks/validation-summary.json",
        },
        "entryPoints": {
            "hostQuickstart": "docs/user/host/quickstart.md",
            "sdkGuide": "docs/user/sdk/README.md",
            "releaseProcess": "docs/developer/publishing/release-process.md",
        },
    }
    if monitor_packages:
        manifest["monitorPackages"] = monitor_packages
    return manifest


def build_monitor_package_entries(output_dir: Path) -> list[dict[str, object]]:
    monitor_root = output_dir / "monitor"
    if not monitor_root.exists():
        return []

    packages: list[dict[str, object]] = []
    for manifest_path in sorted(monitor_root.glob("*/release-manifest.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        target_platform = manifest.get("targetPlatform")
        versions = manifest.get("versions")
        if not isinstance(target_platform, str) or not isinstance(versions, dict):
            raise RuntimeError(f"Monitor 平台 manifest 缺少 targetPlatform 或 versions：{manifest_path}")
        monitor_version = versions.get("monitor")
        javascript_sdk_version = versions.get("sdk")
        if not isinstance(monitor_version, str) or not isinstance(javascript_sdk_version, str):
            raise RuntimeError(f"Monitor 平台 manifest 缺少版本字段：{manifest_path}")
        platform_dir = manifest_path.parent
        packages.append(
            {
                "targetPlatform": target_platform,
                "monitorVersion": monitor_version,
                "javascriptSdkVersion": javascript_sdk_version,
                "manifestPath": manifest_path.relative_to(output_dir).as_posix(),
                "releaseNotesPath": (platform_dir / "release-notes.md").relative_to(output_dir).as_posix(),
                "validationSummaryPath": (platform_dir / "checks" / "validation-summary.json").relative_to(
                    output_dir
                ).as_posix(),
            }
        )

    return packages


def write_release_notes(output_dir: Path, manifest: dict[str, object], release_name: str) -> None:
    lines = [
        f"# {release_name}",
        "",
        f"- Channel: `{manifest['channel']}`",
        f"- Release ID: `{manifest['releaseId']}`",
        f"- Release Tag: `{manifest['releaseTag']}`",
        f"- Commit: `{manifest['commit']}`",
        f"- Generated At (UTC): `{manifest['generatedAtUtc']}`",
        "",
        "## Assets",
        "",
        "| Name | Category | Target | Variant | SHA256 |",
        "| --- | --- | --- | --- | --- |",
    ]

    for asset in manifest["assets"]:
        variant = asset.get("variant", "-")
        lines.append(
            f"| `{asset['name']}` | `{asset['category']}` | `{asset['target']}` | `{variant}` | `{asset['sha256']}` |"
        )

    monitor_packages = manifest.get("monitorPackages")
    if isinstance(monitor_packages, list) and monitor_packages:
        lines.extend(
            [
                "",
                "## Monitor Packages",
                "",
                "| Target Platform | Monitor Version | JS SDK Version | Manifest | Validation |",
                "| --- | --- | --- | --- | --- |",
            ]
        )
        for package in monitor_packages:
            if not isinstance(package, dict):
                continue
            lines.append(
                "| "
                f"`{package['targetPlatform']}` | "
                f"`{package['monitorVersion']}` | "
                f"`{package['javascriptSdkVersion']}` | "
                f"`{package['manifestPath']}` | "
                f"`{package['validationSummaryPath']}` |"
            )

    lines.extend(
        [
            "",
            "## Validation",
            "",
            "- Packaging and integrity steps for this output are recorded in `checks/validation-summary.json`.",
            "- Workflow-driven releases may satisfy same-grade verification in upstream CI jobs before asset assembly.",
            "",
            "## Next Steps",
            "",
            "- Host onboarding: `docs/user/host/quickstart.md`",
            "- SDK onboarding: `docs/user/sdk/README.md`",
            "- Release process: `docs/developer/publishing/release-process.md`",
            "- Release checklist: `docs/developer/publishing/release-checklist.md`",
            "",
        ]
    )

    (output_dir / "release-notes.md").write_text("\n".join(lines), encoding="utf-8")


def ensure_asset_integrity(
    output_dir: Path,
    manifest: dict[str, object],
    host_rids: Sequence[str],
    validation_records: list[ValidationRecord],
    require_monitor: bool,
) -> None:
    required_paths = [output_dir / "release-manifest.json", output_dir / "release-notes.md"]
    expected_host_asset_names = {variant.archive_name(rid) for rid in host_rids for variant in HOST_VARIANTS}
    required_paths.extend(output_dir / "host" / asset_name for asset_name in sorted(expected_host_asset_names))
    required_paths.extend(
        [
            output_dir / "checks" / "validation-summary.json",
            next_existing(output_dir / "sdk" / "dotnet", "DevHub.Sdk.DotNet.*.nupkg"),
            next_existing(output_dir / "sdk" / "dotnet", "DevHub.Sdk.DotNet.*.snupkg"),
            next_existing(output_dir / "sdk" / "dotnet", "DevHub.Sdk.DotNet.DependencyInjection.*.nupkg"),
            next_existing(output_dir / "sdk" / "dotnet", "DevHub.Sdk.DotNet.DependencyInjection.*.snupkg"),
            next_existing(output_dir / "sdk" / "javascript", "*.tgz"),
            next_existing(output_dir / "sdk" / "python", "*.tar.gz"),
            next_existing(output_dir / "sdk" / "python", "*.whl"),
        ]
    )

    for path in required_paths:
        if not path.exists():
            raise RuntimeError(f"Missing required release asset: {path}")

    actual_host_asset_names = {path.name for path in sorted((output_dir / "host").glob("*.zip"))}
    missing_host_asset_names = sorted(expected_host_asset_names - actual_host_asset_names)
    unexpected_host_asset_names = sorted(actual_host_asset_names - expected_host_asset_names)
    if missing_host_asset_names:
        raise RuntimeError(f"Missing Host variants: {', '.join(missing_host_asset_names)}")
    if unexpected_host_asset_names:
        raise RuntimeError(f"Unexpected Host variants: {', '.join(unexpected_host_asset_names)}")

    manifest_assets = manifest.get("assets")
    if not isinstance(manifest_assets, list):
        raise RuntimeError("release-manifest.json 缺少 assets 数组。")

    expected_manifest_variants = {(rid, variant.name) for rid in host_rids for variant in HOST_VARIANTS}
    actual_manifest_variants: set[tuple[str, str]] = set()
    for asset in manifest_assets:
        if not isinstance(asset, dict) or asset.get("category") != "host":
            continue
        target = asset.get("target")
        variant = asset.get("variant")
        if not isinstance(target, str) or not isinstance(variant, str):
            raise RuntimeError("Host 资产缺少 target 或 variant 字段。")
        actual_manifest_variants.add((target, variant))

    if actual_manifest_variants != expected_manifest_variants:
        raise RuntimeError(
            "Host manifest 变体矩阵不完整。"
            f" expected={sorted(expected_manifest_variants)!r}"
            f" actual={sorted(actual_manifest_variants)!r}"
        )

    monitor_assets = [
        asset
        for asset in manifest_assets
        if isinstance(asset, dict) and asset.get("category") == "monitor-app"
    ]
    if require_monitor:
        monitor_packages = manifest.get("monitorPackages")
        if not isinstance(monitor_packages, list) or not monitor_packages:
            raise RuntimeError("release-manifest.json 缺少 Monitor 平台包信息。")
        if not monitor_assets:
            raise RuntimeError("release-manifest.json 缺少 Monitor App 资产。")
        platforms = {
            package.get("targetPlatform")
            for package in monitor_packages
            if isinstance(package, dict) and isinstance(package.get("targetPlatform"), str)
        }
        asset_platforms = {
            asset.get("target")
            for asset in monitor_assets
            if isinstance(asset.get("target"), str)
        }
        if platforms != asset_platforms:
            raise RuntimeError(
                "Monitor manifest 平台集合不完整。"
                f" packages={sorted(platforms)!r}"
                f" assets={sorted(asset_platforms)!r}"
            )
        for package in monitor_packages:
            if not isinstance(package, dict):
                raise RuntimeError("Monitor 平台包信息包含无效条目。")
            for field_name in ("manifestPath", "releaseNotesPath", "validationSummaryPath"):
                relative_path = package.get(field_name)
                if not isinstance(relative_path, str):
                    raise RuntimeError(f"Monitor 平台包缺少 {field_name} 字段。")
                if not (output_dir / relative_path).is_file():
                    raise RuntimeError(f"Monitor 平台包引用的文件不存在：{relative_path}")

    release_notes_text = (output_dir / "release-notes.md").read_text(encoding="utf-8")
    if "| Name | Category | Target | Variant | SHA256 |" not in release_notes_text:
        raise RuntimeError("release-notes.md 未输出 Host variant 列。")
    for asset_name in expected_host_asset_names:
        if asset_name not in release_notes_text:
            raise RuntimeError(f"release-notes.md 缺少 Host 资产条目：{asset_name}")
    if require_monitor and "## Monitor Packages" not in release_notes_text:
        raise RuntimeError("release-notes.md 缺少 Monitor 平台包说明。")
    for asset in monitor_assets:
        asset_name = asset.get("name")
        if isinstance(asset_name, str) and asset_name not in release_notes_text:
            raise RuntimeError(f"release-notes.md 缺少 Monitor 资产条目：{asset_name}")

    validation_records.append(
        ValidationRecord(
            name="Release asset integrity",
            command=["internal-check", "release-assets"],
            cwd=str(REPO_ROOT),
            logPath="checks/validation-summary.json",
            status="passed",
        )
    )


def next_existing(base_dir: Path, pattern: str) -> Path:
    match = next(iter(sorted(base_dir.glob(pattern))), None)
    if match is None:
        return base_dir / f"__missing__{pattern.replace('*', 'star')}"
    return match


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)
    if maybe_print_help(args_list, build_help()):
        return 0

    args = parse_args(args_list)
    release_id = validate_release_label(args.release_id, field_name="release-id")
    release_tag = validate_release_label(args.release_tag or release_id, field_name="release-tag")
    release_name = args.release_name or f"DevHub {release_id}"
    commit = args.commit or read_git_output(["git", "rev-parse", "HEAD"]).strip()
    host_rids = tuple(args.host_rids or DEFAULT_HOST_RIDS)

    result = package_release(
        ReleasePackageOptions(
            release_id=release_id,
            channel=args.channel,
            release_tag=release_tag,
            release_name=release_name,
            commit=commit,
            output_root=Path(args.output_root).resolve(),
            host_rids=host_rids,
            skip_monitor=bool(args.skip_monitor),
            monitor_assets_root=Path(args.monitor_assets_root).resolve() if args.monitor_assets_root else None,
            reuse_existing_output=bool(args.reuse_existing_output),
            validated_externally=bool(args.validated_externally),
        )
    )
    print(f"Release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f"Release packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
