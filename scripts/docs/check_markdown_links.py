#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote

REPO_ROOT = Path(__file__).resolve().parents[2]
URI_SCHEME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
WINDOWS_ABSOLUTE_PATH_PATTERN = re.compile(r"^[A-Za-z]:[\\\\/]")


@dataclass(frozen=True)
class MarkdownLink:
    raw_text: str
    destination: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="扫描仓库内所有未被 .gitignore 忽略的 Markdown 文件，并校验其中的本地文件路径超链接。"
    )
    parser.add_argument(
        "--repo-root",
        default=str(REPO_ROOT),
        help="仓库根目录。默认自动推断为当前脚本所在仓库。",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_root = Path(args.repo_root).resolve()
    markdown_files = list_markdown_files(repo_root)
    broken_links = collect_broken_links(repo_root, markdown_files)

    if not broken_links:
        print("未发现失效的 Markdown 文件路径超链接。")
        return 0

    for markdown_file in sorted(broken_links):
        print(f"- {markdown_file.relative_to(repo_root).as_posix()}")
        for raw_link in broken_links[markdown_file]:
            print(f"    - {raw_link}")

    return 1


def list_markdown_files(repo_root: Path) -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.md"],
        cwd=repo_root,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )

    files: list[Path] = []
    for line in result.stdout.splitlines():
        relative_path = line.strip()
        if not relative_path:
            continue
        files.append((repo_root / relative_path).resolve())

    return files


def collect_broken_links(repo_root: Path, markdown_files: list[Path]) -> dict[Path, list[str]]:
    broken_links: dict[Path, list[str]] = defaultdict(list)
    seen_links: dict[Path, set[str]] = defaultdict(set)

    for markdown_file in markdown_files:
        content = markdown_file.read_text(encoding="utf-8")

        for link in extract_markdown_links(content):
            if not is_local_file_target(link.destination):
                continue

            resolved_target = resolve_target_path(repo_root, markdown_file, link.destination)
            if resolved_target.exists():
                continue

            if link.raw_text in seen_links[markdown_file]:
                continue

            seen_links[markdown_file].add(link.raw_text)
            broken_links[markdown_file].append(link.raw_text)

    return dict(broken_links)


def extract_markdown_links(text: str) -> list[MarkdownLink]:
    links: list[MarkdownLink] = []
    index = 0
    inline_code_ticks: int | None = None
    fenced_code_marker: tuple[str, int] | None = None

    while index < len(text):
        if is_line_start(text, index):
            current_fence = detect_fenced_code_marker(text, index)
            if current_fence is not None:
                if fenced_code_marker is None:
                    fenced_code_marker = current_fence
                elif current_fence[0] == fenced_code_marker[0] and current_fence[1] >= fenced_code_marker[1]:
                    fenced_code_marker = None

                index = move_to_next_line(text, index)
                continue

        if fenced_code_marker is not None:
            index = move_to_next_line(text, index)
            continue

        backtick_count = count_repeated_character(text, index, "`")
        if backtick_count > 0:
            if inline_code_ticks is None:
                inline_code_ticks = backtick_count
            elif inline_code_ticks == backtick_count:
                inline_code_ticks = None

            index += backtick_count
            continue

        if inline_code_ticks is not None:
            index += 1
            continue

        if text[index] != "[" or (index > 0 and text[index - 1] == "!"):
            index += 1
            continue

        label_end = find_link_label_end(text, index)
        if label_end is None:
            index += 1
            continue

        destination_start = skip_whitespace(text, label_end + 1)
        if destination_start >= len(text) or text[destination_start] != "(":
            index = label_end + 1
            continue

        destination_end = find_link_destination_end(text, destination_start)
        if destination_end is None:
            index = label_end + 1
            continue

        raw_destination = text[destination_start + 1 : destination_end]
        destination = parse_link_destination(raw_destination)
        if destination is not None:
            links.append(
                MarkdownLink(
                    raw_text=text[index : destination_end + 1],
                    destination=destination,
                )
            )

        index = destination_end + 1

    return links


def is_line_start(text: str, index: int) -> bool:
    return index == 0 or text[index - 1] == "\n"


def detect_fenced_code_marker(text: str, index: int) -> tuple[str, int] | None:
    line_end = text.find("\n", index)
    if line_end == -1:
        line_end = len(text)

    line = text[index:line_end]
    stripped_line = line.lstrip(" ")
    indent_width = len(line) - len(stripped_line)
    if indent_width > 3 or not stripped_line:
        return None

    marker = stripped_line[0]
    if marker not in ("`", "~"):
        return None

    marker_length = count_repeated_character(stripped_line, 0, marker)
    if marker_length < 3:
        return None

    return marker, marker_length


def move_to_next_line(text: str, index: int) -> int:
    next_line = text.find("\n", index)
    if next_line == -1:
        return len(text)
    return next_line + 1


def count_repeated_character(text: str, index: int, value: str) -> int:
    count = 0
    while index + count < len(text) and text[index + count] == value:
        count += 1
    return count


def find_link_label_end(text: str, start: int) -> int | None:
    depth = 1
    index = start + 1

    while index < len(text):
        character = text[index]
        if character == "\\":
            index += 2
            continue
        if character == "[":
            depth += 1
        elif character == "]":
            depth -= 1
            if depth == 0:
                return index
        index += 1

    return None


def skip_whitespace(text: str, index: int) -> int:
    while index < len(text) and text[index].isspace():
        index += 1
    return index


def find_link_destination_end(text: str, start: int) -> int | None:
    index = start + 1
    nested_parentheses = 0
    quote: str | None = None
    in_angle_brackets = False

    while index < len(text):
        character = text[index]

        if character == "\\":
            index += 2
            continue

        if quote is not None:
            if character == quote:
                quote = None
            index += 1
            continue

        if character in ('"', "'") and not in_angle_brackets:
            quote = character
            index += 1
            continue

        if character == "<" and not in_angle_brackets:
            in_angle_brackets = True
            index += 1
            continue

        if character == ">" and in_angle_brackets:
            in_angle_brackets = False
            index += 1
            continue

        if in_angle_brackets:
            index += 1
            continue

        if character == "(":
            nested_parentheses += 1
            index += 1
            continue

        if character == ")":
            if nested_parentheses == 0:
                return index
            nested_parentheses -= 1
            index += 1
            continue

        index += 1

    return None


def parse_link_destination(raw_destination: str) -> str | None:
    destination = raw_destination.strip()
    if not destination:
        return None

    if destination.startswith("<"):
        end_index = destination.find(">")
        if end_index == -1:
            return None
        parsed_destination = destination[1:end_index].strip()
        return unescape_markdown_text(parsed_destination) or None

    index = 0
    nested_parentheses = 0

    while index < len(destination):
        character = destination[index]
        if character == "\\":
            index += 2
            continue
        if character.isspace() and nested_parentheses == 0:
            break
        if character == "(":
            nested_parentheses += 1
        elif character == ")" and nested_parentheses > 0:
            nested_parentheses -= 1
        index += 1

    parsed_destination = destination[:index].strip()
    return unescape_markdown_text(parsed_destination) or None


def unescape_markdown_text(value: str) -> str:
    return re.sub(r"\\(.)", r"\1", value)


def is_local_file_target(destination: str) -> bool:
    if not destination or destination.startswith("#"):
        return False

    if destination.startswith("//"):
        return False

    if WINDOWS_ABSOLUTE_PATH_PATTERN.match(destination):
        return True

    return URI_SCHEME_PATTERN.match(destination) is None


def resolve_target_path(repo_root: Path, markdown_file: Path, destination: str) -> Path:
    path_part = destination.split("#", 1)[0].split("?", 1)[0].strip()
    decoded_path = unquote(path_part)

    if WINDOWS_ABSOLUTE_PATH_PATTERN.match(decoded_path):
        return Path(decoded_path)

    if decoded_path.startswith("/"):
        absolute_path = Path(decoded_path)
        if absolute_path.exists():
            return absolute_path
        return (repo_root / decoded_path.lstrip("/")).resolve()

    return (markdown_file.parent / decoded_path).resolve()


if __name__ == "__main__":
    raise SystemExit(main())
