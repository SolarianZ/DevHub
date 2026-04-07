from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

import tomllib


REPO_ROOT = Path(__file__).resolve().parents[2]
VERSION_PROPS = REPO_ROOT / "eng" / "Version.props"
JS_PACKAGE_JSON = REPO_ROOT / "sdks" / "javascript" / "package.json"
JS_PACKAGE_LOCK = REPO_ROOT / "sdks" / "javascript" / "package-lock.json"
PYTHON_PYPROJECT = REPO_ROOT / "sdks" / "python" / "pyproject.toml"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Synchronize or validate DevHub package versions against eng/Version.props."
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Only validate that package metadata matches eng/Version.props.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    version_info = read_version_props(VERSION_PROPS)
    package_version = version_info["DevHubVersion"]

    if args.check:
        mismatches = collect_mismatches(package_version)
        if mismatches:
            print("Version metadata drift detected:", file=sys.stderr)
            for mismatch in mismatches:
                print(f"- {mismatch}", file=sys.stderr)
            return 1

        print(f"Version metadata is consistent with {VERSION_PROPS.relative_to(REPO_ROOT)} ({package_version}).")
        return 0

    changed_paths: list[Path] = []
    if sync_json_version(JS_PACKAGE_JSON, package_version):
        changed_paths.append(JS_PACKAGE_JSON)
    if sync_package_lock_version(JS_PACKAGE_LOCK, package_version):
        changed_paths.append(JS_PACKAGE_LOCK)
    if sync_pyproject_version(PYTHON_PYPROJECT, package_version):
        changed_paths.append(PYTHON_PYPROJECT)

    mismatches = collect_mismatches(package_version)
    if mismatches:
        raise RuntimeError("同步后仍检测到版本漂移：" + "; ".join(mismatches))

    if changed_paths:
        print(f"Synchronized package metadata to {package_version}:")
        for path in changed_paths:
            print(f"- {path.relative_to(REPO_ROOT)}")
    else:
        print(f"Package metadata already matches {VERSION_PROPS.relative_to(REPO_ROOT)} ({package_version}).")

    return 0


def read_version_props(path: Path) -> dict[str, str]:
    root = ElementTree.fromstring(path.read_text(encoding="utf-8"))
    values: dict[str, str] = {}

    for property_name in (
        "DevHubVersion",
        "DevHubAssemblyVersion",
        "DevHubFileVersion",
        "DevHubInformationalVersion",
    ):
        value = root.findtext(f".//{property_name}")
        if not value or not value.strip():
            raise RuntimeError(f"Unable to resolve {property_name} from {path}.")
        values[property_name] = value.strip()

    return values


def collect_mismatches(expected_version: str) -> list[str]:
    mismatches: list[str] = []

    package_json = json.loads(JS_PACKAGE_JSON.read_text(encoding="utf-8"))
    if package_json.get("version") != expected_version:
        mismatches.append(
            f"{JS_PACKAGE_JSON.relative_to(REPO_ROOT)} version={package_json.get('version')!r}, expected {expected_version!r}"
        )

    package_lock = json.loads(JS_PACKAGE_LOCK.read_text(encoding="utf-8"))
    if package_lock.get("version") != expected_version:
        mismatches.append(
            f"{JS_PACKAGE_LOCK.relative_to(REPO_ROOT)} root version={package_lock.get('version')!r}, expected {expected_version!r}"
        )

    root_package_version = package_lock.get("packages", {}).get("", {}).get("version")
    if root_package_version != expected_version:
        mismatches.append(
            f"{JS_PACKAGE_LOCK.relative_to(REPO_ROOT)} packages[''].version={root_package_version!r}, expected {expected_version!r}"
        )

    pyproject = tomllib.loads(PYTHON_PYPROJECT.read_text(encoding="utf-8"))
    python_version = pyproject.get("project", {}).get("version")
    if python_version != expected_version:
        mismatches.append(
            f"{PYTHON_PYPROJECT.relative_to(REPO_ROOT)} version={python_version!r}, expected {expected_version!r}"
        )

    return mismatches


def sync_json_version(path: Path, expected_version: str) -> bool:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("version") == expected_version:
        return False

    payload["version"] = expected_version
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return True


def sync_package_lock_version(path: Path, expected_version: str) -> bool:
    payload = json.loads(path.read_text(encoding="utf-8"))
    changed = False

    if payload.get("version") != expected_version:
        payload["version"] = expected_version
        changed = True

    packages = payload.get("packages")
    if isinstance(packages, dict):
        root_package = packages.get("")
        if isinstance(root_package, dict) and root_package.get("version") != expected_version:
            root_package["version"] = expected_version
            changed = True

    if changed:
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    return changed


def sync_pyproject_version(path: Path, expected_version: str) -> bool:
    content = path.read_text(encoding="utf-8")
    updated_content, count = re.subn(
        r'(?m)^version = "[^"]+"$',
        f'version = "{expected_version}"',
        content,
        count=1,
    )
    if count != 1:
        raise RuntimeError(f"Unable to locate project.version in {path}.")

    if updated_content == content:
        return False

    path.write_text(updated_content, encoding="utf-8")
    return True


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        print(f"Version synchronization failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
