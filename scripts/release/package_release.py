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
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence
from xml.etree import ElementTree

import tomllib


REPO_ROOT = Path(__file__).resolve().parents[2]
VERSION_SYNC_SCRIPT = REPO_ROOT / "scripts" / "release" / "sync_versions.py"
HOST_PROJECT = REPO_ROOT / "host" / "src" / "DevHub.Host" / "DevHub.Host.csproj"
DOTNET_SDK_PROJECT = REPO_ROOT / "sdks" / "dotnet" / "src" / "DevHub.Sdk" / "DevHub.Sdk.csproj"
DOTNET_SDK_DI_PROJECT = (
    REPO_ROOT / "sdks" / "dotnet" / "src" / "DevHub.Sdk.DependencyInjection" / "DevHub.Sdk.DependencyInjection.csproj"
)
JS_SDK_DIR = REPO_ROOT / "sdks" / "javascript"
PYTHON_SDK_DIR = REPO_ROOT / "sdks" / "python"
DEFAULT_HOST_RIDS = ("win-x64", "linux-x64", "osx-arm64")
NPM_COMMAND = "npm.cmd" if os.name == "nt" else "npm"
SAFE_RELEASE_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")


@dataclass
class ValidationRecord:
    name: str
    command: list[str]
    cwd: str
    logPath: str
    status: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build DevHub release assets and validation output.")
    parser.add_argument("--release-id", required=True, help="Output folder name under artifacts/release.")
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
        "--output-root",
        default=str(REPO_ROOT / "artifacts" / "release"),
        help="Directory that contains release-id subdirectories.",
    )
    parser.add_argument(
        "--host-rid",
        action="append",
        dest="host_rids",
        help="Host RID to publish. Can be passed multiple times. Defaults to win-x64/linux-x64/osx-arm64.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    ensure_version_metadata_consistency()
    output_root = Path(args.output_root).resolve()
    release_id = validate_release_label(args.release_id, field_name="release-id")
    release_tag = validate_release_label(args.release_tag or release_id, field_name="release-tag")
    release_name = args.release_name or f"DevHub {release_id}"
    commit = args.commit or read_git_output(["git", "rev-parse", "HEAD"]).strip()
    host_rids = tuple(args.host_rids or DEFAULT_HOST_RIDS)
    generated_at = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    output_dir = resolve_release_output_dir(output_root, release_id)
    checks_dir = output_dir / "checks"

    if output_dir.exists():
        shutil.rmtree(output_dir)

    checks_dir.mkdir(parents=True, exist_ok=True)
    validation_records: list[ValidationRecord] = []

    versions = {
        "host": read_msbuild_version(HOST_PROJECT),
        "dotnetSdk": read_msbuild_version(DOTNET_SDK_PROJECT),
        "dotnetSdkDependencyInjection": read_msbuild_version(DOTNET_SDK_DI_PROJECT),
        "javascriptSdk": read_json_version(JS_SDK_DIR / "package.json"),
        "pythonSdk": read_toml_version(PYTHON_SDK_DIR / "pyproject.toml"),
    }

    run_release_validation(checks_dir, validation_records)
    asset_paths = build_release_assets(output_dir, checks_dir, host_rids, validation_records)

    validation_summary = {
        "schemaVersion": 1,
        "executedAtUtc": generated_at,
        "records": [asdict(record) for record in validation_records],
    }
    write_json(checks_dir / "validation-summary.json", validation_summary)

    manifest = build_manifest(
        output_dir=output_dir,
        asset_paths=asset_paths,
        versions=versions,
        release_id=release_id,
        channel=args.channel,
        release_tag=release_tag,
        release_name=release_name,
        commit=commit,
        generated_at=generated_at,
    )
    write_json(output_dir / "release-manifest.json", manifest)
    write_release_notes(
        output_dir=output_dir,
        manifest=manifest,
        release_name=release_name,
    )

    ensure_asset_integrity(output_dir, host_rids, validation_records)
    write_json(checks_dir / "validation-summary.json", validation_summary_with_integrity(validation_records))

    print(f"Release assets ready: {output_dir}")
    return 0


def validation_summary_with_integrity(records: Sequence[ValidationRecord]) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "executedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "records": [asdict(record) for record in records],
    }


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


def run_release_validation(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
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
    run_logged_command(
        name=".NET SDK tests",
        command=["dotnet", "test", str(REPO_ROOT / "sdks" / "dotnet" / "DevHub.DotNetSdk.slnx"), "-c", "Release"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "dotnet-sdk-tests.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK install",
        command=[NPM_COMMAND, "ci"],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-install.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK build",
        command=[NPM_COMMAND, "run", "build"],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-build.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK tests",
        command=[NPM_COMMAND, "test"],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-tests.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Python SDK install",
        command=[sys.executable, "-m", "pip", "install", "-e", "./sdks/python[test]"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-install.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Python SDK tests",
        command=[sys.executable, "-m", "pytest", "sdks/python/tests"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-tests.log",
        validation_records=validation_records,
    )


def build_release_assets(
    output_dir: Path,
    checks_dir: Path,
    host_rids: Sequence[str],
    validation_records: list[ValidationRecord],
) -> list[Path]:
    host_dir = output_dir / "host"
    dotnet_dir = output_dir / "sdk" / "dotnet"
    javascript_dir = output_dir / "sdk" / "javascript"
    python_dir = output_dir / "sdk" / "python"
    staging_dir = output_dir / ".staging"

    host_dir.mkdir(parents=True, exist_ok=True)
    dotnet_dir.mkdir(parents=True, exist_ok=True)
    javascript_dir.mkdir(parents=True, exist_ok=True)
    python_dir.mkdir(parents=True, exist_ok=True)
    staging_dir.mkdir(parents=True, exist_ok=True)

    asset_paths: list[Path] = []

    for rid in host_rids:
        publish_dir = staging_dir / "host" / rid
        publish_dir.mkdir(parents=True, exist_ok=True)
        run_logged_command(
            name=f"Host restore ({rid})",
            command=["dotnet", "restore", str(HOST_PROJECT), "-r", rid],
            cwd=REPO_ROOT,
            log_path=checks_dir / f"host-restore-{rid}.log",
            validation_records=validation_records,
        )
        run_logged_command(
            name=f"Host publish ({rid})",
            command=[
                "dotnet",
                "publish",
                str(HOST_PROJECT),
                "-c",
                "Release",
                "-r",
                rid,
                "--self-contained",
                "false",
                "-p:PublishSingleFile=true",
                "--no-restore",
                "-o",
                str(publish_dir),
            ],
            cwd=REPO_ROOT,
            log_path=checks_dir / f"host-publish-{rid}.log",
            validation_records=validation_records,
        )
        archive_path = host_dir / f"devhub-host-{rid}.zip"
        create_zip_archive(source_dir=publish_dir, archive_path=archive_path, root_name=f"devhub-host-{rid}")
        asset_paths.append(archive_path)

    run_logged_command(
        name=".NET SDK core pack",
        command=[
            "dotnet",
            "pack",
            str(DOTNET_SDK_PROJECT),
            "-c",
            "Release",
            f"-p:PackageOutputPath={dotnet_dir}",
        ],
        cwd=REPO_ROOT,
        log_path=checks_dir / "dotnet-sdk-pack.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name=".NET SDK dependency-injection pack",
        command=[
            "dotnet",
            "pack",
            str(DOTNET_SDK_DI_PROJECT),
            "-c",
            "Release",
            f"-p:PackageOutputPath={dotnet_dir}",
        ],
        cwd=REPO_ROOT,
        log_path=checks_dir / "dotnet-sdk-di-pack.log",
        validation_records=validation_records,
    )
    asset_paths.extend(sorted(dotnet_dir.glob("*")))

    run_logged_command(
        name="JS SDK install for pack",
        command=[NPM_COMMAND, "ci"],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-pack-install.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK build for pack",
        command=[NPM_COMMAND, "run", "build"],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-pack-build.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="JS SDK pack",
        command=[NPM_COMMAND, "pack", "--pack-destination", str(javascript_dir)],
        cwd=JS_SDK_DIR,
        log_path=checks_dir / "javascript-pack.log",
        validation_records=validation_records,
    )
    asset_paths.extend(sorted(javascript_dir.glob("*")))

    run_logged_command(
        name="Python build backend install",
        command=[sys.executable, "-m", "pip", "install", "build"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-build-backend.log",
        validation_records=validation_records,
    )
    run_logged_command(
        name="Python SDK pack",
        command=[sys.executable, "-m", "build", "--sdist", "--wheel", "--outdir", str(python_dir), "sdks/python"],
        cwd=REPO_ROOT,
        log_path=checks_dir / "python-pack.log",
        validation_records=validation_records,
    )
    asset_paths.extend(sorted(python_dir.glob("*")))

    if staging_dir.exists():
        shutil.rmtree(staging_dir)

    return asset_paths


def build_manifest(
    output_dir: Path,
    asset_paths: Sequence[Path],
    versions: dict[str, str],
    release_id: str,
    channel: str,
    release_tag: str,
    release_name: str,
    commit: str,
    generated_at: str,
) -> dict[str, object]:
    assets = []
    for asset_path in sorted(asset_paths):
        relative_path = asset_path.relative_to(output_dir).as_posix()
        assets.append(
            {
                "name": asset_path.name,
                "category": categorize_asset(asset_path),
                "target": asset_target(asset_path),
                "path": relative_path,
                "sha256": sha256_file(asset_path),
                "sizeBytes": asset_path.stat().st_size,
            }
        )

    return {
        "schemaVersion": 1,
        "releaseId": release_id,
        "releaseName": release_name,
        "channel": channel,
        "releaseTag": release_tag,
        "commit": commit,
        "generatedAtUtc": generated_at,
        "versions": versions,
        "assets": assets,
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
        "| Name | Category | Target | SHA256 |",
        "| --- | --- | --- | --- |",
    ]

    for asset in manifest["assets"]:
        lines.append(
            f"| `{asset['name']}` | `{asset['category']}` | `{asset['target']}` | `{asset['sha256']}` |"
        )

    lines.extend(
        [
            "",
            "## Validation",
            "",
            "- Validation executed locally through `python scripts/release/package_release.py`.",
            "- Summary file: `checks/validation-summary.json`.",
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
    host_rids: Sequence[str],
    validation_records: list[ValidationRecord],
) -> None:
    required_paths = [output_dir / "release-manifest.json", output_dir / "release-notes.md"]
    required_paths.extend(output_dir / "host" / f"devhub-host-{rid}.zip" for rid in host_rids)
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


def run_host_smoke(checks_dir: Path, validation_records: list[ValidationRecord]) -> None:
    smoke_data_dir = checks_dir / "smoke-data"
    runtime_dir = smoke_data_dir / "runtime"
    hub_json_path = runtime_dir / "hub.json"
    stdout_log = checks_dir / "smoke-host.stdout.log"
    stderr_log = checks_dir / "smoke-host.stderr.log"
    runner_log = checks_dir / "smoke-runner.log"

    if smoke_data_dir.exists():
        shutil.rmtree(smoke_data_dir)
    smoke_data_dir.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env["DEVHUB_DATA_DIR"] = str(smoke_data_dir)

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
            env=env,
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
                env=env,
            )
        finally:
            terminate_process(process)

    if smoke_data_dir.exists():
        shutil.rmtree(smoke_data_dir)


def wait_for_path(path: Path, timeout_seconds: int) -> None:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if path.exists():
            return
        time.sleep(1)
    raise RuntimeError(f"Timed out waiting for required file: {path}")


def terminate_process(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def run_logged_command(
    name: str,
    command: Sequence[str],
    cwd: Path,
    log_path: Path,
    validation_records: list[ValidationRecord],
    env: dict[str, str] | None = None,
) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    print(f"==> {name}")
    print(f"    {format_command(command)}")

    with log_path.open("w", encoding="utf-8", errors="replace") as log_handle:
        process = subprocess.Popen(
            list(command),
            cwd=cwd,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
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


def read_json_version(package_json_path: Path) -> str:
    payload = json.loads(package_json_path.read_text(encoding="utf-8"))
    version = payload.get("version")
    if not isinstance(version, str) or not version.strip():
        raise RuntimeError(f"Unable to resolve version from {package_json_path}.")
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


def format_command(command: Sequence[str]) -> str:
    return " ".join(shlex.quote(part) for part in command)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f"Release packaging failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
