#!/usr/bin/env python3
"""检查仓库文本文件编码并按需修复为 UTF-8。"""

from __future__ import annotations

import argparse
import codecs
import json
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


UTF_BOMS: dict[bytes, str] = {
    codecs.BOM_UTF8: "utf-8-sig",
    codecs.BOM_UTF32_LE: "utf-32",
    codecs.BOM_UTF32_BE: "utf-32",
    codecs.BOM_UTF16_LE: "utf-16",
    codecs.BOM_UTF16_BE: "utf-16",
}

LIKELY_TEXT_SUFFIXES = {
    ".bat",
    ".cmd",
    ".config",
    ".cs",
    ".csproj",
    ".css",
    ".editorconfig",
    ".gitattributes",
    ".gitignore",
    ".html",
    ".js",
    ".json",
    ".md",
    ".props",
    ".ps1",
    ".py",
    ".pyi",
    ".rc",
    ".resx",
    ".sh",
    ".slnx",
    ".sql",
    ".targets",
    ".toml",
    ".txt",
    ".xml",
    ".xsd",
    ".xsl",
    ".yaml",
    ".yml",
}

LEGACY_ENCODINGS = (
    "gb18030",
    "cp936",
    "big5",
    "cp1252",
)

SUSPICIOUS_MOJIBAKE_TOKENS = (
    "锟",
    "鈥",
    "闂",
    "�",
    "Ã",
    "Â",
    "ðŸ",
)


@dataclass(frozen=True)
class FileResult:
    """单个文件的检查结果。"""

    path: Path
    status: str
    original_encoding: str | None
    action: str
    details: str


def parse_args() -> argparse.Namespace:
    """解析命令行参数。"""
    parser = argparse.ArgumentParser(description="检查仓库文本文件编码并修复为 UTF-8")
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[3],
        help="仓库根目录，默认自动定位到当前仓库",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="仅检查，不写回文件",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="输出 JSON 汇总，便于脚本集成",
    )
    return parser.parse_args()


def list_candidate_files(root: Path) -> list[Path]:
    """列出仓库内未被 .gitignore 忽略的候选文件。"""
    command = ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"]
    completed = subprocess.run(
        command,
        cwd=root,
        capture_output=True,
        check=True,
    )
    items = [item for item in completed.stdout.split(b"\0") if item]
    return [root / Path(os.fsdecode(item)) for item in items]


def is_likely_text_path(path: Path) -> bool:
    """基于文件名快速筛选明显的文本文件。"""
    suffix = path.suffix.lower()
    if suffix in LIKELY_TEXT_SUFFIXES:
        return True

    name = path.name.lower()
    return name in {"license", "readme", ".gitignore", ".editorconfig", ".gitattributes"}


def looks_binary(data: bytes) -> bool:
    """粗略判断二进制文件，避免误改。"""
    if not data:
        return False

    if any(data.startswith(bom) for bom in UTF_BOMS):
        return False

    sample = data[:4096]
    if b"\x00" in sample:
        return True

    control_count = sum(byte < 9 or 13 < byte < 32 for byte in sample)
    return control_count / max(len(sample), 1) > 0.05


def utf16_without_bom_score(data: bytes) -> tuple[bool, str | None]:
    """识别无 BOM 的 UTF-16 文本。"""
    if len(data) < 4 or len(data) % 2 != 0:
        return False, None

    sample = data[: min(len(data), 4096)]
    even_zero_ratio = sample[0::2].count(0) / max(len(sample[0::2]), 1)
    odd_zero_ratio = sample[1::2].count(0) / max(len(sample[1::2]), 1)

    if odd_zero_ratio > 0.25 and even_zero_ratio < 0.05:
        return True, "utf-16-le"
    if even_zero_ratio > 0.25 and odd_zero_ratio < 0.05:
        return True, "utf-16-be"
    return False, None


def score_decoded_text(text: str) -> int:
    """对候选解码结果打分，分数越高越可信。"""
    score = 0
    printable = 0

    for char in text:
        code = ord(char)
        if char in "\r\n\t":
            score += 1
            printable += 1
            continue

        if 32 <= code <= 126:
            score += 2
            printable += 1
            continue

        if 0x4E00 <= code <= 0x9FFF:
            score += 4
            printable += 1
            continue

        if char.isprintable():
            score += 2
            printable += 1
            continue

        score -= 8

    suspicious_penalty = sum(text.count(token) for token in SUSPICIOUS_MOJIBAKE_TOKENS)
    score -= suspicious_penalty * 6
    score += printable
    return score


def try_decode_with_candidates(data: bytes) -> tuple[str | None, str | None, str | None]:
    """尝试识别文件编码，并返回文本与编码。"""
    for bom, encoding in UTF_BOMS.items():
        if data.startswith(bom):
            return data.decode(encoding), encoding, "bom"

    try:
        return data.decode("utf-8"), "utf-8", "valid_utf8"
    except UnicodeDecodeError:
        pass

    utf16_match, utf16_encoding = utf16_without_bom_score(data)
    if utf16_match and utf16_encoding is not None:
        try:
            return data.decode(utf16_encoding), utf16_encoding, "utf16_heuristic"
        except UnicodeDecodeError:
            pass

    candidates: list[tuple[int, str, str]] = []
    for encoding in LEGACY_ENCODINGS:
        try:
            text = data.decode(encoding)
        except UnicodeDecodeError:
            continue
        candidates.append((score_decoded_text(text), encoding, text))

    if not candidates:
        return None, None, None

    candidates.sort(key=lambda item: item[0], reverse=True)
    best_score, best_encoding, best_text = candidates[0]
    second_score = candidates[1][0] if len(candidates) > 1 else -10**9

    if best_score < 0:
        return None, None, None

    if best_score - second_score < 12 and best_encoding not in {"gb18030", "cp936"}:
        return None, None, None

    return best_text, best_encoding, "legacy_heuristic"


def normalize_to_utf8(text: str) -> bytes:
    """统一输出为无 BOM 的 UTF-8，并保留原始换行风格。"""
    return text.encode("utf-8")


def inspect_file(path: Path, check_only: bool) -> FileResult:
    """检查并按需修复单个文件。"""
    data = path.read_bytes()
    if looks_binary(data):
        return FileResult(path, "skipped", None, "skip", "二进制或疑似二进制文件")

    text, encoding, reason = try_decode_with_candidates(data)
    if text is None or encoding is None:
        return FileResult(path, "unknown", None, "manual", "无法可靠识别编码，已跳过")

    target_bytes = normalize_to_utf8(text)
    if encoding == "utf-8" and data == target_bytes:
        return FileResult(path, "ok", encoding, "none", "已是 UTF-8")

    if encoding == "utf-8-sig":
        detail = "检测到 UTF-8 BOM，已转换为无 BOM UTF-8"
    else:
        detail = f"检测到 {encoding}，将转换为 UTF-8"

    if not check_only:
        path.write_bytes(target_bytes)

    return FileResult(path, "fixed", encoding, "rewrite", detail)


def render_text_report(results: Iterable[FileResult], root: Path) -> int:
    """输出文本报告。"""
    fixed = 0
    unknown = 0
    for result in results:
        relative_path = result.path.relative_to(root).as_posix()
        print(f"[{result.status}] {relative_path} :: {result.details}")
        if result.status == "fixed":
            fixed += 1
        elif result.status == "unknown":
            unknown += 1

    print(f"\n汇总: fixed={fixed}, unknown={unknown}")
    return 1 if unknown else 0


def render_json_report(results: Iterable[FileResult], root: Path) -> int:
    """输出 JSON 报告。"""
    payload = []
    fixed = 0
    unknown = 0

    for result in results:
        if result.status == "fixed":
            fixed += 1
        elif result.status == "unknown":
            unknown += 1

        payload.append(
            {
                "path": result.path.relative_to(root).as_posix(),
                "status": result.status,
                "originalEncoding": result.original_encoding,
                "action": result.action,
                "details": result.details,
            }
        )

    print(json.dumps({"summary": {"fixed": fixed, "unknown": unknown}, "files": payload}, ensure_ascii=False, indent=2))
    return 1 if unknown else 0


def main() -> int:
    """程序入口。"""
    args = parse_args()
    root = args.root.resolve()

    results: list[FileResult] = []
    for path in list_candidate_files(root):
        if not path.is_file() or not is_likely_text_path(path):
            continue
        results.append(inspect_file(path, check_only=args.check))

    if args.json:
        return render_json_report(results, root)
    return render_text_report(results, root)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        print(f"执行 Git 命令失败: {error}", file=sys.stderr)
        raise SystemExit(2) from error
