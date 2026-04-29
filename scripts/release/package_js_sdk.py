from __future__ import annotations

import argparse
import sys
from pathlib import Path

from console_output import console_print
from package_models import (
    JavaScriptSdkPackageOptions,
    JavaScriptSdkPackageResult,
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
)
from package_shared import (
    JS_SDK_DIR,
    NPM_COMMAND,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    describe_release_assets,
    maybe_print_help,
    read_json_version,
    remove_tree,
    resolve_release_output_dir,
    run_logged_command,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "sdk" / "javascript"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_js_sdk.py --help",
        summary="Validate and package the repository JS/TS SDK into the standard release-style SDK layout.",
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
                title="JS SDK Parameters",
                options=(
                    PackageHelpOption(
                        "--verify-only",
                        "Run npm install, build, and tests, then stop before npm pack.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub JS/TS SDK release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for JS/TS SDK packaging output.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run JS/TS SDK validation without package output.",
    )
    return parser.parse_args(argv)


def package_js_sdk(options: JavaScriptSdkPackageOptions) -> JavaScriptSdkPackageResult:
    validation_records = []
    versions = {
        "javascriptSdk": read_json_version(JS_SDK_DIR / "package.json"),
    }

    run_logged_command(
        name="JS SDK install",
        command=[NPM_COMMAND, "ci"],
        cwd=JS_SDK_DIR,
        log_path=options.checks_dir / "javascript-install.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK build",
        command=[NPM_COMMAND, "run", "build"],
        cwd=JS_SDK_DIR,
        log_path=options.checks_dir / "javascript-build.log",
        validation_records=validation_records,
    )
    if not options.skip_validation:
        run_logged_command(
            name="JS SDK tests",
            command=[NPM_COMMAND, "test"],
            cwd=JS_SDK_DIR,
            log_path=options.checks_dir / "javascript-tests.log",
            validation_records=validation_records,
        )

    assets = []
    if not options.verify_only:
        assets = build_js_sdk_assets(
            output_dir=options.output_dir,
            checks_dir=options.checks_dir,
            validation_records=validation_records,
        )

    return JavaScriptSdkPackageResult(
        output_dir=options.output_dir,
        assets=tuple(assets),
        versions=versions,
        validation_records=tuple(validation_records),
    )


def build_js_sdk_assets(
    *,
    output_dir: Path,
    checks_dir: Path,
    validation_records: list,
):
    javascript_dir = output_dir / "sdk" / "javascript"
    if javascript_dir.exists():
        remove_tree(javascript_dir)
    javascript_dir.mkdir(parents=True, exist_ok=True)

    run_logged_command(
        name="JS SDK pack",
        command=[NPM_COMMAND, "pack", "--pack-destination", str(javascript_dir)],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-pack.log",
        validation_records=validation_records,
    )

    return describe_release_assets(sorted(javascript_dir.glob("*")))


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

    result = package_js_sdk(
        JavaScriptSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            verify_only=bool(args.verify_only),
        )
    )
    write_json(checks_dir / "validation-summary.json", build_validation_summary(result.validation_records))

    if args.verify_only:
        console_print(f"JS SDK validation ready: {checks_dir}")
    else:
        console_print(f"JS SDK release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        console_print(f"JS SDK packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
