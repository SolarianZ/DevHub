from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

from console_output import console_print
from package_models import (
    HostPackageOptions,
    HostPackageResult,
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
    ReleaseAsset,
    ValidationRecord,
)
from package_shared import (
    DEFAULT_HOST_RIDS,
    HOST_PROJECT,
    HOST_VARIANTS,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    create_zip_archive,
    maybe_print_help,
    read_msbuild_version,
    remove_tree,
    resolve_release_output_dir,
    run_logged_command,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "host"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_host.py --help",
        summary="Validate and package the DevHub Host domain into the standard release-style host layout.",
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
                title="Host Parameters",
                options=(
                    PackageHelpOption(
                        "--host-rid <rid>",
                        "Host RID to publish. Repeat for multiple targets. Defaults to win-x64, linux-x64, osx-arm64.",
                    ),
                    PackageHelpOption(
                        "--verify-only",
                        "Run Host build/tests and smoke validation, then stop before RID publish/package steps.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub Host release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for Host packaging output.",
    )
    parser.add_argument(
        "--host-rid",
        action="append",
        dest="host_rids",
        help="Host RID to publish. Can be passed multiple times. Defaults to win-x64/linux-x64/osx-arm64.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run Host validation without RID publish/package output.",
    )
    return parser.parse_args(argv)


def package_host(options: HostPackageOptions) -> HostPackageResult:
    validation_records = []
    versions = {
        "host": read_msbuild_version(HOST_PROJECT),
    }

    if not options.skip_validation:
        run_host_validation(options.checks_dir, validation_records)

    assets = []
    if not options.verify_only:
        assets = build_host_assets(
            output_dir=options.output_dir,
            checks_dir=options.checks_dir,
            host_rids=options.host_rids,
            validation_records=validation_records,
        )

    return HostPackageResult(
        output_dir=options.output_dir,
        assets=tuple(assets),
        versions=versions,
        validation_records=tuple(validation_records),
    )


def run_host_validation(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    run_logged_command(
        name="Host build",
        command=["dotnet", "build", str(REPO_ROOT / "host" / "DevHub.slnx"), "-c", "Release"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "host-build.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Host tests",
        command=["dotnet", "test", str(REPO_ROOT / "host" / "DevHub.slnx"), "-c", "Release"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "host-tests.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Host smoke dependencies",
        command=[sys.executable, "-m", "pip", "install", "requests"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "smoke-dependencies.log",
        validation_records=validation_records,
    )
    run_host_smoke(checks_dir, validation_records)


def build_host_assets(
    *,
    output_dir: Path,
    checks_dir: Path,
    host_rids: tuple[str, ...],
    validation_records: list[ValidationRecord],
) -> list[ReleaseAsset]:
    host_dir = output_dir / "host"
    staging_root = output_dir / ".staging" / "host"

    if host_dir.exists():
        remove_tree(host_dir)
    if staging_root.exists():
        remove_tree(staging_root)

    host_dir.mkdir(parents=True, exist_ok=True)
    staging_root.mkdir(parents=True, exist_ok=True)

    assets = []
    try:
        for rid in host_rids:
            restore_dir = staging_root / rid
            restore_dir.mkdir(parents=True, exist_ok=True)
            run_logged_command(
                name=f"Host restore ({rid})",
                command=["dotnet", "restore", str(HOST_PROJECT), "-r", rid],
                cwd=REPO_ROOT,
                log_path=checks_dir / f"host-restore-{rid}.log",
                validation_records=validation_records,
            )
            for variant in HOST_VARIANTS:
                publish_dir = staging_root / rid / variant.name
                publish_dir.mkdir(parents=True, exist_ok=True)
                run_logged_command(
                    name=f"Host publish ({rid}, {variant.name})",
                    command=[
                        "dotnet",
                        "publish",
                        str(HOST_PROJECT),
                        "-c",
                        "Release",
                        "-r",
                        rid,
                        *variant.publish_arguments,
                        "--no-restore",
                        "-o",
                        str(publish_dir),
                    ],
                    cwd=REPO_ROOT,
                    log_path=checks_dir / f"host-publish-{rid}-{variant.name}.log",
                    validation_records=validation_records,
                )
                archive_path = host_dir / variant.archive_name(rid)
                create_zip_archive(
                    source_dir=publish_dir,
                    archive_path=archive_path,
                    root_name=variant.archive_root_name(rid),
                )
                assets.append(
                    ReleaseAsset(
                        path=archive_path,
                        category="host",
                        target=rid,
                        variant=variant.name,
                    )
                )
    finally:
        if staging_root.exists():
            remove_tree(staging_root)

    return assets


def run_host_smoke(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    smoke_data_dir = checks_dir / "smoke-data"
    runtime_dir = smoke_data_dir / "runtime"
    hub_json_path = runtime_dir / "hub.json"
    stdout_log = checks_dir / "smoke-host.stdout.log"
    stderr_log = checks_dir / "smoke-host.stderr.log"
    runner_log = checks_dir / "smoke-runner.log"

    if smoke_data_dir.exists():
        remove_tree(smoke_data_dir)
    smoke_data_dir.mkdir(parents=True, exist_ok=True)

    env = {"DEVHUB_DATA_DIR": str(smoke_data_dir)}

    with stdout_log.open("w", encoding="utf-8", errors="replace") as stdout_handle, stderr_log.open(
        "w", encoding="utf-8", errors="replace"
    ) as stderr_handle:
        process = subprocess.Popen(
            [
                "dotnet",
                "run",
                "--project",
                str(HOST_PROJECT),
                "-c",
                "Release",
                "--no-build",
                "--no-launch-profile",
            ],
            cwd=REPO_ROOT,
            env={**os.environ, **env},
            stdout=stdout_handle,
            stderr=stderr_handle,
        )
        try:
            wait_for_path(hub_json_path, timeout_seconds=40)
            run_logged_command(
                name="Host smoke",
                command=[sys.executable, "host/tests/blackbox/test_runner.py", "--smoke", "--no-header"],
                cwd=REPO_ROOT,
                log_path=runner_log,
                validation_records=validation_records,
                env_overrides=env,
            )
        finally:
            terminate_process(process)

    if smoke_data_dir.exists():
        remove_tree(smoke_data_dir)


def wait_for_path(path: Path, timeout_seconds: int) -> None:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if path.exists():
            return
        time.sleep(1)
    raise RuntimeError(f"Timed out waiting for required file: {path}")


def terminate_process(process: object) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)
    if maybe_print_help(args_list, build_help()):
        return 0

    args = parse_args(args_list)
    release_id = validate_release_label(args.release_id, field_name="release-id")
    output_root = Path(args.output_root).resolve()
    output_dir = resolve_release_output_dir(output_root, release_id)
    host_rids = tuple(args.host_rids or DEFAULT_HOST_RIDS)

    if output_dir.exists():
        remove_tree(output_dir)

    checks_dir = output_dir / "checks"
    checks_dir.mkdir(parents=True, exist_ok=True)

    result = package_host(
        HostPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            host_rids=host_rids,
            verify_only=bool(args.verify_only),
        )
    )
    write_json(checks_dir / "validation-summary.json", build_validation_summary(result.validation_records))

    if args.verify_only:
        console_print(f"Host validation ready: {checks_dir}")
    else:
        console_print(f"Host release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        console_print(f"Host packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
