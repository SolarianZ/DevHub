from __future__ import annotations

import argparse
import sys
from pathlib import Path

from console_output import console_print
from package_models import (
    PackageHelp,
    PackageHelpOption,
    PackageHelpSection,
    PythonSdkPackageOptions,
    PythonSdkPackageResult,
)
from package_shared import (
    PYTHON_SDK_DIR,
    REPO_ROOT,
    add_release_output_arguments,
    build_validation_summary,
    create_argument_parser,
    describe_release_assets,
    maybe_print_help,
    read_toml_version,
    remove_tree,
    resolve_release_output_dir,
    run_logged_command,
    validate_release_label,
    write_json,
)


DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "sdk" / "python"


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/package_py_sdk.py --help",
        summary="Validate and package the repository Python SDK into the standard release-style SDK layout.",
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
                title="Python SDK Parameters",
                options=(
                    PackageHelpOption(
                        "--verify-only",
                        "Install test dependencies and run pytest, then stop before building wheel/sdist artifacts.",
                    ),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = create_argument_parser("Build DevHub Python SDK release assets and validation output.")
    add_release_output_arguments(
        parser,
        default_output_root=DEFAULT_OUTPUT_ROOT,
        output_root_help="Directory that contains release-id subdirectories for Python SDK packaging output.",
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only run Python SDK validation without package output.",
    )
    return parser.parse_args(argv)


def package_py_sdk(options: PythonSdkPackageOptions) -> PythonSdkPackageResult:
    validation_records = []
    versions = {
        "pythonSdk": read_toml_version(PYTHON_SDK_DIR / "pyproject.toml"),
    }
    python_env = {
        # 优先导入当前仓库工作树中的 Python SDK，避免本机全局 editable install 污染发布验证。
        "PYTHONPATH": str(PYTHON_SDK_DIR / "src"),
    }

    if not options.skip_validation:
        run_logged_command(
            name="Python SDK install",
            command=[sys.executable, "-m", "pip", "install", "-e", "./sdks/python[test]"],
            cwd=REPO_ROOT,
            log_path=options.checks_dir / "python-install.log",
            validation_records=validation_records,
            env_overrides=python_env,
        )
        run_logged_command(
            name="Python SDK tests",
            command=[sys.executable, "-m", "pytest", "sdks/python/tests"],
            cwd=REPO_ROOT,
            log_path=options.checks_dir / "python-tests.log",
            validation_records=validation_records,
            env_overrides=python_env,
        )

    assets = []
    if not options.verify_only:
        assets = build_py_sdk_assets(
            output_dir=options.output_dir,
            checks_dir=options.checks_dir,
            validation_records=validation_records,
        )

    return PythonSdkPackageResult(
        output_dir=options.output_dir,
        assets=tuple(assets),
        versions=versions,
        validation_records=tuple(validation_records),
    )


def build_py_sdk_assets(
    *,
    output_dir: Path,
    checks_dir: Path,
    validation_records: list,
):
    python_dir = output_dir / "sdk" / "python"
    if python_dir.exists():
        remove_tree(python_dir)
    python_dir.mkdir(parents=True, exist_ok=True)

    run_logged_command(
        name="Python build backend install",
        command=[sys.executable, "-m", "pip", "install", "build"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-build-backend.log",
        validation_records=validation_records,
        env_overrides={"PYTHONPATH": str(PYTHON_SDK_DIR / "src")},
    )
    run_logged_command(
        name="Python SDK pack",
        command=[sys.executable, "-m", "build", "--sdist", "--wheel", "--outdir", str(python_dir), "sdks/python"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-pack.log",
        validation_records=validation_records,
        env_overrides={"PYTHONPATH": str(PYTHON_SDK_DIR / "src")},
    )

    return describe_release_assets(sorted(python_dir.glob("*")))


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

    result = package_py_sdk(
        PythonSdkPackageOptions(
            output_dir=output_dir,
            checks_dir=checks_dir,
            verify_only=bool(args.verify_only),
        )
    )
    write_json(checks_dir / "validation-summary.json", build_validation_summary(result.validation_records))

    if args.verify_only:
        console_print(f"Python SDK validation ready: {checks_dir}")
    else:
        console_print(f"Python SDK release assets ready: {result.output_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        console_print(f"Python SDK packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
