from __future__ import annotations

import argparse
import sys
from pathlib import Path

from package_models import (
    DotNetSdkPackageOptions,
    DotNetSdkPackageResult,
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
)
from package_shared import (
    DOTNET_SDK_PROJECTS,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    describe_release_assets,
    maybe_print_help,
    read_msbuild_version,
    remove_tree,
    resolve_release_output_dir,
    run_logged_command,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "sdk" / "dotnet"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_dotnet_sdk.py --help",
        summary="Validate and package the repository .NET SDK projects into the standard release-style SDK layout.",
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
                title=".NET SDK Parameters",
                options=(
                    PackageHelpOption(
                        "--verify-only",
                        "Run .NET SDK solution tests and stop before emitting package files.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub .NET SDK release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for .NET SDK packaging output.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run .NET SDK validation without package output.",
    )
    return parser.parse_args(argv)


def package_dotnet_sdk(options: DotNetSdkPackageOptions) -> DotNetSdkPackageResult:
    validation_records = []
    versions = {
        "dotnetSdk": read_msbuild_version(DOTNET_SDK_PROJECTS[0]),
        "dotnetSdkDependencyInjection": read_msbuild_version(DOTNET_SDK_PROJECTS[1]),
    }

    run_logged_command(
        name=".NET SDK tests",
        command=["dotnet", "test", str(REPO_ROOT / "sdks" / "dotnet" / "DevHub.DotNetSdk.slnx"), "-c", "Release"],
        cwd=REPO_ROOT,
        log_path=options.checks_dir / "dotnet-sdk-tests.log",
        validation_records=validation_records,
    )

    assets = []
    if not options.verify_only:
        assets = build_dotnet_sdk_assets(
            output_dir=options.output_dir,
            checks_dir=options.checks_dir,
            validation_records=validation_records,
        )

    return DotNetSdkPackageResult(
        output_dir=options.output_dir,
        assets=tuple(assets),
        versions=versions,
        validation_records=tuple(validation_records),
    )


def build_dotnet_sdk_assets(
    *,
    output_dir: Path,
    checks_dir: Path,
    validation_records: list,
):
    dotnet_dir = output_dir / "sdk" / "dotnet"
    if dotnet_dir.exists():
        remove_tree(dotnet_dir)
    dotnet_dir.mkdir(parents=True, exist_ok=True)

    for name, project, log_name in (
        (".NET SDK core pack", DOTNET_SDK_PROJECTS[0], "dotnet-sdk-core-pack.log"),
        (".NET SDK DI pack", DOTNET_SDK_PROJECTS[1], "dotnet-sdk-dependency-injection-pack.log"),
    ):
        run_logged_command(
            name=name,
            command=[
                "dotnet",
                "pack",
                str(project),
                "-c",
                "Release",
                f"-p:PackageOutputPath={dotnet_dir}",
            ],
            cwd=REPO_ROOT,
            log_path=checks_dir / log_name,
            validation_records=validation_records,
        )

    return describe_release_assets(sorted(dotnet_dir.glob("*")))


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)
    if maybe_print_help(args_list, build_help()):
        return 0

    args = parse_args(args_list)
    release_id = validate_release_label(args.release_id, field_name="release-id")
    output_root = Path(args.output_root).resolve()
    output_dir = resolve_release_output_dir(output_root, release_id)

    if output_dir.exists():
        remove_tree(output_dir)

    checks_dir = output_dir / "checks"
    checks_dir.mkdir(parents=True, exist_ok=True)

    result = package_dotnet_sdk(
        DotNetSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            verify_only=bool(args.verify_only),
        )
    )
    write_json(checks_dir / "validation-summary.json", build_validation_summary(result.validation_records))

    if args.verify_only:
        print(f".NET SDK validation ready: {checks_dir}")
    else:
        print(f".NET SDK release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f".NET SDK packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
