from __future__ import annotations

import argparse
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

from console_output import console_print
from package_models import PackageHelp, PackageHelpOption, PackageHelpSection, ValidationRecord
from package_shared import NPM_COMMAND, REPO_ROOT, build_validation_summary, maybe_print_help, remove_tree, run_logged_command, write_json


CI_NPM_VERSION = "11.12.1"
DEFAULT_OUTPUT_ROOT = REPO_ROOT / "artifacts" / "lockfile-refresh"


@dataclass(frozen=True)
class LockfileWorkspace:
    key: str
    label: str
    directory: Path
    lockfile: Path


WORKSPACES = (
    LockfileWorkspace(
        key="js-sdk",
        label="JS SDK",
        directory=REPO_ROOT / "sdks" / "javascript",
        lockfile=REPO_ROOT / "sdks" / "javascript" / "package-lock.json",
    ),
    LockfileWorkspace(
        key="monitor",
        label="Monitor",
        directory=REPO_ROOT / "apps" / "monitor",
        lockfile=REPO_ROOT / "apps" / "monitor" / "package-lock.json",
    ),
)
WORKSPACE_LOOKUP = {workspace.key: workspace for workspace in WORKSPACES}


def build_help() -> PackageHelp:
    return PackageHelp(
        command="python scripts/release/refresh_js_lockfiles.py --help",
        summary="Refresh repository JavaScript package-lock.json files and validate them with npm ci.",
        sections=(
            PackageHelpSection(
                title="Parameters",
                options=(
                    PackageHelpOption(
                        "--npm-basis <ci|local>",
                        f"Select the npm runtime used for refresh and validation. Default: ci ({CI_NPM_VERSION}).",
                    ),
                    PackageHelpOption(
                        "--only <js-sdk|monitor>",
                        "Limit the refresh to a single workspace. Default: refresh both JS SDK and Monitor.",
                    ),
                    PackageHelpOption(
                        "--output-root <dir>",
                        f"Directory used for validation logs and summary output. Default: {DEFAULT_OUTPUT_ROOT}",
                    ),
                    PackageHelpOption("--help", "Print this capability and parameter summary without refreshing lockfiles."),
                ),
            ),
        ),
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Refresh repository JavaScript lockfiles and validate them with npm ci.",
        add_help=False,
    )
    parser.add_argument(
        "--npm-basis",
        choices=("ci", "local"),
        default="ci",
        help="Choose whether to use the CI npm version or the current local npm.",
    )
    parser.add_argument(
        "--only",
        choices=tuple(WORKSPACE_LOOKUP),
        help="Refresh only one workspace lockfile.",
    )
    parser.add_argument(
        "--output-root",
        default=str(DEFAULT_OUTPUT_ROOT),
        help="Directory used to write refresh logs and validation summary output.",
    )
    return parser.parse_args(argv)


def select_workspaces(only: str | None) -> tuple[LockfileWorkspace, ...]:
    if only is None:
        return WORKSPACES
    return (WORKSPACE_LOOKUP[only],)


def npm_command(npm_basis: str, npm_args: Sequence[str]) -> list[str]:
    if npm_basis == "local":
        return [NPM_COMMAND, *npm_args]
    return [NPM_COMMAND, "exec", "--yes", f"npm@{CI_NPM_VERSION}", "--", *npm_args]


def git_has_uncommitted_changes(path: Path) -> bool:
    completed = subprocess.run(
        ["git", "status", "--short", "--", str(path.relative_to(REPO_ROOT).as_posix())],
        cwd=REPO_ROOT,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    return bool(completed.stdout.strip())


def warn_existing_lockfile_changes(workspace: LockfileWorkspace) -> None:
    if git_has_uncommitted_changes(workspace.lockfile):
        console_print(
            f"Warning: {workspace.lockfile.relative_to(REPO_ROOT).as_posix()} already has uncommitted changes and will be overwritten."
        )


def refresh_workspace(
    workspace: LockfileWorkspace,
    *,
    npm_basis: str,
    checks_dir: Path,
    validation_records: list[ValidationRecord],
) -> None:
    console_print(f"Refreshing {workspace.label} lockfile...")
    warn_existing_lockfile_changes(workspace)

    node_modules_dir = workspace.directory / "node_modules"
    if node_modules_dir.exists():
        console_print(f"Removing {node_modules_dir.relative_to(REPO_ROOT).as_posix()}")
        remove_tree(node_modules_dir)

    run_logged_command(
        name=f"{workspace.label} lockfile refresh",
        command=npm_command(npm_basis, ["install", "--package-lock-only"]),
        cwd=workspace.directory,
        log_path=checks_dir / f"{workspace.key}-refresh.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name=f"{workspace.label} npm ci validation",
        command=npm_command(npm_basis, ["ci"]),
        cwd=workspace.directory,
        log_path=checks_dir / f"{workspace.key}-ci.log",
        validation_records=validation_records,
    )


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)
    if maybe_print_help(args_list, build_help()):
        return 0

    args = parse_args(args_list)
    workspaces = select_workspaces(args.only)

    output_root = Path(args.output_root).resolve()
    checks_dir = output_root / args.npm_basis / (args.only or "all") / "checks"
    if checks_dir.parent.exists():
        remove_tree(checks_dir.parent)
    checks_dir.mkdir(parents=True, exist_ok=True)

    validation_records: list[ValidationRecord] = []
    for workspace in workspaces:
        refresh_workspace(
            workspace,
            npm_basis=args.npm_basis,
            checks_dir=checks_dir,
            validation_records=validation_records,
        )

    write_json(checks_dir / "validation-summary.json", build_validation_summary(validation_records))
    console_print(f"Lockfile refresh validation ready: {checks_dir}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        console_print(f"Lockfile refresh failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
