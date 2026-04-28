from __future__ import annotations

import argparse
import json
import sys

from console_output import console_print
from package_shared import read_git_output


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Resolve DevHub release channel metadata.")
    parser.add_argument("--release-ref", required=True, help="Branch or tag ref used to determine the release channel.")
    parser.add_argument("--commit", help="Commit SHA recorded in the release metadata. Defaults to HEAD.")
    return parser.parse_args(argv)


def resolve_release_metadata(*, release_ref: str, commit: str) -> dict[str, object]:
    short_sha = commit[:7]
    commit_stamp = read_git_output(["git", "show", "-s", "--date=format:%Y%m%dT%H%M%SZ", "--format=%cd", commit]).strip()

    if release_ref in {"preview", "refs/heads/preview"}:
        return {
            "channel": "preview",
            "releaseId": "preview-latest",
            "releaseTag": "preview-latest",
            "releaseName": "DevHub Preview",
            "prerelease": True,
        }

    if release_ref in {"main", "refs/heads/main"}:
        release_id = f"main-{commit_stamp}-{short_sha}"
        return {
            "channel": "main-snapshot",
            "releaseId": release_id,
            "releaseTag": release_id,
            "releaseName": f"DevHub Main Snapshot {commit_stamp} {short_sha}",
            "prerelease": True,
        }

    if release_ref.startswith("refs/tags/v"):
        release_tag = release_ref.removeprefix("refs/tags/")
        return {
            "channel": "stable",
            "releaseId": release_tag,
            "releaseTag": release_tag,
            "releaseName": f"DevHub {release_tag}",
            "prerelease": False,
        }

    if release_ref.startswith("v"):
        return {
            "channel": "stable",
            "releaseId": release_ref,
            "releaseTag": release_ref,
            "releaseName": f"DevHub {release_ref}",
            "prerelease": False,
        }

    version_tags = [
        line.strip()
        for line in read_git_output(["git", "tag", "--points-at", commit, "--list", "v*"]).splitlines()
        if line.strip()
    ]
    if len(version_tags) == 1:
        release_tag = version_tags[0]
        return {
            "channel": "stable",
            "releaseId": release_tag,
            "releaseTag": release_tag,
            "releaseName": f"DevHub {release_tag}",
            "prerelease": False,
        }

    raise RuntimeError("release-ref must resolve to preview, main, or a unique v* tag.")


def main(argv: list[str] | None = None) -> int:
    args = parse_args(list(sys.argv[1:] if argv is None else argv))
    commit = read_git_output(["git", "rev-parse", args.commit or "HEAD"]).strip()
    payload = {
        "commit": commit,
        **resolve_release_metadata(release_ref=args.release_ref, commit=commit),
    }
    console_print(json.dumps(payload, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        console_print(f"Release metadata resolution failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
