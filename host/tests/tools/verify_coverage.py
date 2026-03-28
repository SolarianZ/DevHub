#!/usr/bin/env python3
"""
合并多个 Cobertura 报告并校验覆盖率阈值。

说明：
- 通过 line/condition 级别 key 做去重合并，避免不同测试项目重复统计同一行。
- 条件分支优先读取 <condition> 明细；若缺失则回退 condition-coverage 汇总值。
"""

import argparse
from collections import defaultdict
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET


CONDITION_COVERAGE_PATTERN = re.compile(r".*\((\d+)/(\d+)\)")


def discover_coverage_files(root_dir):
    pattern = os.path.join(root_dir, "host", "**", "TestResults", "**", "coverage.cobertura.xml")
    return sorted(set(glob.glob(pattern, recursive=True)))


def parse_percent(text):
    value = (text or "").strip().rstrip("%")
    try:
        return float(value)
    except ValueError:
        return 0.0


def parse_condition_coverage(text):
    match = CONDITION_COVERAGE_PATTERN.match(text or "")
    if not match:
        return 0, 0
    return int(match.group(1)), int(match.group(2))


def iter_class_lines(root):
    for package in root.findall(".//package"):
        package_name = package.attrib.get("name", "")
        for class_node in package.findall(".//class"):
            file_name = class_node.attrib.get("filename", "")
            lines_parent = class_node.find("lines")
            if lines_parent is None:
                continue
            for line_node in lines_parent.findall("line"):
                yield package_name, file_name, line_node


def collect_coverage_keys(file_path):
    document = ET.parse(file_path)
    root = document.getroot()

    line_total = set()
    line_covered = set()
    branch_total = set()
    branch_covered = set()

    for package_name, file_name, line_node in iter_class_lines(root):
        source_label = build_source_label(package_name, file_name)
        line_number_raw = line_node.attrib.get("number")
        if line_number_raw is None:
            continue

        try:
            line_number = int(line_number_raw)
        except ValueError:
            continue

        line_key = (source_label, line_number)
        line_total.add(line_key)

        hits_raw = line_node.attrib.get("hits", "0")
        try:
            hits = int(hits_raw)
        except ValueError:
            hits = 0

        if hits > 0:
            line_covered.add(line_key)

        if line_node.attrib.get("branch", "False") != "True":
            continue

        conditions_parent = line_node.find("conditions")
        condition_nodes = conditions_parent.findall("condition") if conditions_parent is not None else []

        if condition_nodes:
            for condition_node in condition_nodes:
                condition_number = condition_node.attrib.get("number", "0")
                condition_key = (source_label, line_number, condition_number)
                branch_total.add(condition_key)

                coverage_percent = parse_percent(condition_node.attrib.get("coverage", "0%"))
                if coverage_percent > 0:
                    branch_covered.add(condition_key)
        else:
            covered_count, total_count = parse_condition_coverage(line_node.attrib.get("condition-coverage", ""))
            for index in range(total_count):
                condition_key = (source_label, line_number, f"auto-{index}")
                branch_total.add(condition_key)
                if index < covered_count:
                    branch_covered.add(condition_key)

    return line_total, line_covered, branch_total, branch_covered


def format_percent(numerator, denominator):
    if denominator <= 0:
        return "0.00%"
    return f"{(numerator / denominator) * 100:.2f}%"


def build_source_label(package_name, file_name):
    normalized_file_name = (file_name or "").replace("\\", "/").strip().lstrip("./")
    normalized_package_name = (package_name or "").replace("\\", "/").strip()
    normalized_package_path = normalized_package_name.replace(".", "/")

    if normalized_file_name:
        if not normalized_package_name:
            return normalized_file_name

        if (
            normalized_file_name == normalized_package_name
            or normalized_file_name.startswith(f"{normalized_package_name}/")
            or normalized_file_name == normalized_package_path
            or normalized_file_name.startswith(f"{normalized_package_path}/")
        ):
            return normalized_file_name

        return f"{normalized_package_name}/{normalized_file_name}"

    if normalized_package_name:
        return normalized_package_name
    return "<unknown>"


def build_file_coverage_stats(line_total, line_covered, branch_total, branch_covered):
    stats = defaultdict(lambda: {
        "line_total": 0,
        "line_covered": 0,
        "branch_total": 0,
        "branch_covered": 0,
    })

    for source_label, _ in line_total:
        stats[source_label]["line_total"] += 1
    for source_label, _ in line_covered:
        stats[source_label]["line_covered"] += 1
    for source_label, _, _ in branch_total:
        stats[source_label]["branch_total"] += 1
    for source_label, _, _ in branch_covered:
        stats[source_label]["branch_covered"] += 1

    return stats


def print_low_coverage_files(stats, metric_name, covered_key, total_key, max_items=10):
    candidates = []
    for source_label, values in stats.items():
        total = values[total_key]
        covered = values[covered_key]
        if total <= 0:
            continue

        uncovered = total - covered
        candidates.append((covered / total, -uncovered, source_label, covered, total))

    if not candidates:
        print(f"No per-file {metric_name} coverage data found.")
        return

    candidates.sort(key=lambda item: (item[0], item[1], item[2]))
    print(f"Lowest {metric_name} coverage files (top {min(max_items, len(candidates))}):")
    for _, _, source_label, covered, total in candidates[:max_items]:
        uncovered = total - covered
        print(
            f"- {source_label}: {metric_name}={format_percent(covered, total)} "
            f"({covered}/{total}), uncovered={uncovered}"
        )


def main():
    parser = argparse.ArgumentParser(description="校验覆盖率阈值")
    parser.add_argument("--root", default=".", help="仓库根目录")
    parser.add_argument("--line-threshold", type=float, required=True, help="行覆盖率阈值（0~1）")
    parser.add_argument("--branch-threshold", type=float, required=True, help="分支覆盖率阈值（0~1）")
    args = parser.parse_args()

    coverage_files = discover_coverage_files(args.root)
    if not coverage_files:
        print("ERROR: coverage.cobertura.xml files not found.")
        return 1

    print("Discovered coverage files:")
    for file_path in coverage_files:
        print(f"- {file_path}")

    merged_line_total = set()
    merged_line_covered = set()
    merged_branch_total = set()
    merged_branch_covered = set()

    for file_path in coverage_files:
        line_total, line_covered, branch_total, branch_covered = collect_coverage_keys(file_path)
        merged_line_total.update(line_total)
        merged_line_covered.update(line_covered)
        merged_branch_total.update(branch_total)
        merged_branch_covered.update(branch_covered)

    line_valid = len(merged_line_total)
    line_covered_count = len(merged_line_covered)
    branch_valid = len(merged_branch_total)
    branch_covered_count = len(merged_branch_covered)

    if line_valid <= 0:
        print("ERROR: no line coverage data found.")
        return 1
    if branch_valid <= 0:
        print("ERROR: no branch coverage data found.")
        return 1

    line_rate = line_covered_count / line_valid
    branch_rate = branch_covered_count / branch_valid
    file_coverage_stats = build_file_coverage_stats(
        merged_line_total,
        merged_line_covered,
        merged_branch_total,
        merged_branch_covered,
    )

    print(
        "Merged coverage: "
        f"line={format_percent(line_covered_count, line_valid)} ({line_covered_count}/{line_valid}), "
        f"branch={format_percent(branch_covered_count, branch_valid)} ({branch_covered_count}/{branch_valid})"
    )
    print(
        "Thresholds: "
        f"line>={args.line_threshold * 100:.2f}%, "
        f"branch>={args.branch_threshold * 100:.2f}%"
    )

    failed = False
    if line_rate < args.line_threshold:
        print("ERROR: line coverage below threshold.")
        print_low_coverage_files(file_coverage_stats, "line", "line_covered", "line_total")
        failed = True
    if branch_rate < args.branch_threshold:
        print("ERROR: branch coverage below threshold.")
        print_low_coverage_files(file_coverage_stats, "branch", "branch_covered", "branch_total")
        failed = True

    if failed:
        return 1

    print("OK: coverage thresholds satisfied.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
