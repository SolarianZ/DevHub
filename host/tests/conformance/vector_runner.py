#!/usr/bin/env python3
"""
DevHub M5 最小 conformance runner。
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable


HOST_ROOT = Path(__file__).resolve().parents[2]
REPO_ROOT = Path(__file__).resolve().parents[3]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.test_base import get_test_project_root  # type: ignore  # noqa: E402
from tests.conformance.vector_setup import (  # type: ignore  # noqa: E402
    VectorExecutionContext,
    materialize_vector,
    start_suite_host,
    stop_process,
)


SCRIPT_DIR = Path(__file__).resolve().parent
WILDCARD_ANY_ISO_UTC = "${ANY_ISO_UTC}"
WILDCARD_ANY_NON_EMPTY_STRING = "${ANY_NON_EMPTY_STRING}"


def main() -> int:
    args = parse_args()
    suite_directory = resolve_suite_directory(args)
    ensure_prerequisites()

    vectors = load_vectors(suite_directory, args.vector_id)
    if not vectors:
        print("未找到符合条件的向量。", file=sys.stderr)
        return 1

    failure_count = 0
    with tempfile.TemporaryDirectory(prefix="devhub-conformance-") as temp_root_str:
        temp_root = Path(temp_root_str)
        host_context, process, log_file = start_suite_host(temp_root)
        try:
            for vector_path, vector in vectors:
                execution: VectorExecutionContext | None = None
                try:
                    execution = materialize_vector(vector_path, vector, host_context, temp_root)
                    result = run_vector(execution)
                except Exception as exc:  # noqa: BLE001
                    failure_count += 1
                    emit_failures(
                        [
                            {
                                "vectorId": vector.get("id", str(vector_path)),
                                "sdk": "runner",
                                "expected": vector.get("expectedDiscovery") or vector.get("expectedResponse"),
                                "actual": None,
                                "diffFields": ["$runner"],
                                "message": str(exc),
                            }
                        ]
                    )
                    continue
                finally:
                    if execution is not None:
                        execution.cleanup(host_context)

                if not result["passed"]:
                    failure_count += 1
                    emit_failures(result["failures"])
                    continue

                print(f"PASS  {execution.resolved_vector['id']}")
        finally:
            stop_process(process)
            log_file.close()

    print(f"SUMMARY  total={len(vectors)} passed={len(vectors) - failure_count} failed={failure_count}")
    return 0 if failure_count == 0 else 1


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="运行 DevHub 最小 conformance 向量。")
    parser.add_argument("--suite", default="v1.0.1", help="默认扫描的 suite 目录名。")
    parser.add_argument("--directory", help="显式指定向量目录。")
    parser.add_argument("--vector-id", help="只执行指定向量 ID。")
    return parser.parse_args()


def resolve_suite_directory(args: argparse.Namespace) -> Path:
    if args.directory:
        return Path(args.directory).resolve()
    return (SCRIPT_DIR / args.suite).resolve()


def ensure_prerequisites() -> None:
    dotnet_dll = get_dotnet_adapter_dll_path()
    if not dotnet_dll.is_file():
        raise FileNotFoundError(
            "未找到 .NET conformance 适配器，请先运行 "
            "'dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release'。"
        )

    javascript_dist = REPO_ROOT / "sdks" / "javascript" / "dist" / "index.js"
    if not javascript_dist.is_file():
        raise FileNotFoundError(
            "未找到 JS SDK dist/index.js，请先运行 'npm --prefix sdks/javascript run build'。"
        )


def load_vectors(directory: Path, vector_id: str | None) -> list[tuple[Path, dict[str, Any]]]:
    if not directory.is_dir():
        raise FileNotFoundError(f"未找到向量目录：{directory}")

    vectors: list[tuple[Path, dict[str, Any]]] = []
    for path in sorted(directory.glob("*.json")):
        payload = json.loads(path.read_text(encoding="utf-8"))
        validate_vector_shape(path, payload)
        if vector_id and payload["id"] != vector_id:
            continue
        vectors.append((path, payload))

    return vectors


def validate_vector_shape(path: Path, payload: dict[str, Any]) -> None:
    required_fields = ["id", "description", "transport", "request", "expectedResponse", "tags"]
    missing = [field for field in required_fields if field not in payload]
    if missing:
        raise ValueError(f"向量缺少必需字段：{path} -> {missing}")


def run_vector(execution: VectorExecutionContext) -> dict[str, Any]:
    sdk_results = [
        run_adapter("dotnet", get_dotnet_adapter_command(execution.context_path)),
        run_adapter("typescript", get_typescript_adapter_command(execution.context_path)),
        run_adapter("python", get_python_adapter_command(execution.context_path)),
    ]

    vector = execution.resolved_vector
    is_discovery = "expectedDiscovery" in vector
    expected = vector["expectedDiscovery"] if is_discovery else vector["expectedResponse"]

    failures: list[dict[str, Any]] = []
    normalized_results: dict[str, Any] = {}
    for sdk_result in sdk_results:
        if sdk_result.get("error") is not None:
            failures.append(
                {
                    "vectorId": vector["id"],
                    "sdk": sdk_result["sdk"],
                    "expected": expected,
                    "actual": sdk_result.get("actual"),
                    "diffFields": ["$process"],
                    "message": sdk_result["error"],
                }
            )
            continue

        comparable = build_comparable_payload(sdk_result, is_discovery)
        diffs = collect_differences(expected, comparable)
        if diffs:
            failures.append(
                {
                    "vectorId": vector["id"],
                    "sdk": sdk_result["sdk"],
                    "expected": expected,
                    "actual": comparable,
                    "diffFields": diffs,
                }
            )
            continue

        normalized_results[sdk_result["sdk"]] = canonicalize_with_expected(comparable, expected)

    if not failures and normalized_results:
        baseline_sdk, baseline_payload = next(iter(normalized_results.items()))
        for sdk_name, comparable in normalized_results.items():
            if sdk_name == baseline_sdk:
                continue
            diffs = collect_differences(baseline_payload, comparable)
            if diffs:
                failures.append(
                    {
                        "vectorId": vector["id"],
                        "sdk": sdk_name,
                        "expected": baseline_payload,
                        "actual": comparable,
                        "diffFields": diffs,
                        "message": f"与 {baseline_sdk} 的语义结果不一致。",
                    }
                )

    return {
        "passed": not failures,
        "failures": failures,
    }


def run_adapter(sdk_name: str, command: list[str]) -> dict[str, Any]:
    completed = subprocess.run(
        command,
        cwd=get_test_project_root(),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )

    stdout = completed.stdout.strip()
    stderr = completed.stderr.strip()
    parsed = try_parse_json_line(stdout)
    if parsed is None:
        return {
            "sdk": sdk_name,
            "error": {
                "message": "适配器未输出可解析 JSON。",
                "exitCode": completed.returncode,
                "stdout": stdout,
                "stderr": stderr,
            },
            "actual": None,
        }

    parsed.setdefault("sdk", sdk_name)
    if completed.returncode != 0 and parsed.get("error") is None:
        parsed["error"] = {
            "message": "适配器进程返回非零退出码。",
            "exitCode": completed.returncode,
            "stderr": stderr,
        }
    return parsed


def try_parse_json_line(stdout: str) -> dict[str, Any] | None:
    if not stdout:
        return None

    for line in reversed([line.strip() for line in stdout.splitlines() if line.strip()]):
        try:
            payload = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict):
            return payload
    return None


def build_comparable_payload(result: dict[str, Any], is_discovery: bool) -> Any:
    if is_discovery:
        return {
            "phase": result.get("phase"),
            "outcome": result.get("outcome"),
            "actual": result.get("actual"),
        }
    return result.get("actual")


def canonicalize_with_expected(actual: Any, expected: Any) -> Any:
    if is_wildcard(expected, actual):
        return expected

    if isinstance(expected, dict) and isinstance(actual, dict):
        return {
            key: canonicalize_with_expected(actual[key], expected[key]) if key in expected else actual[key]
            for key in actual
        }

    if isinstance(expected, list) and isinstance(actual, list):
        return [
            canonicalize_with_expected(actual[index], expected[index]) if index < len(expected) else actual[index]
            for index in range(len(actual))
        ]

    return actual


def collect_differences(expected: Any, actual: Any, path: str = "$") -> list[str]:
    if is_wildcard(expected, actual):
        return []

    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return [path]

        diffs: list[str] = []
        for key in expected:
            child_path = f"{path}.{key}"
            if key not in actual:
                diffs.append(child_path)
                continue
            diffs.extend(collect_differences(expected[key], actual[key], child_path))

        for key in actual:
            if key not in expected:
                diffs.append(f"{path}.{key}")
        return diffs

    if isinstance(expected, list):
        if not isinstance(actual, list):
            return [path]
        diffs: list[str] = []
        if len(expected) != len(actual):
            diffs.append(path)
        for index in range(min(len(expected), len(actual))):
            diffs.extend(collect_differences(expected[index], actual[index], f"{path}[{index}]"))
        return diffs

    if expected != actual:
        return [path]
    return []


def is_wildcard(expected: Any, actual: Any) -> bool:
    if expected == WILDCARD_ANY_ISO_UTC:
        if not isinstance(actual, str):
            return False
        try:
            datetime.fromisoformat(actual.replace("Z", "+00:00"))
            return True
        except ValueError:
            return False

    if expected == WILDCARD_ANY_NON_EMPTY_STRING:
        return isinstance(actual, str) and actual.strip() != ""

    return False


def emit_failures(failures: Iterable[dict[str, Any]]) -> None:
    for failure in failures:
        print(f"FAIL  {failure['vectorId']}  [{failure['sdk']}]")
        if failure.get("message"):
            print(f"      Message: {json.dumps(failure['message'], ensure_ascii=False)}")
        print(f"      Expected: {json.dumps(failure['expected'], ensure_ascii=False, sort_keys=True)}")
        print(f"      Actual: {json.dumps(failure['actual'], ensure_ascii=False, sort_keys=True)}")
        print(f"      Diff: {', '.join(failure['diffFields'])}")


def get_python_adapter_command(context_path: Path) -> list[str]:
    return [sys.executable, str(SCRIPT_DIR / "adapters" / "devhub_conformance_py.py"), str(context_path)]


def get_typescript_adapter_command(context_path: Path) -> list[str]:
    return ["node", str(SCRIPT_DIR / "adapters" / "devhub_conformance_js.mjs"), str(context_path)]


def get_dotnet_adapter_command(context_path: Path) -> list[str]:
    return ["dotnet", str(get_dotnet_adapter_dll_path()), str(context_path)]


def get_dotnet_adapter_dll_path() -> Path:
    return (
        REPO_ROOT
        / "sdks"
        / "dotnet"
        / "tests"
        / "DevHub.Sdk.ConformanceAdapter"
        / "bin"
        / "Release"
        / "net10.0"
        / "DevHub.Sdk.ConformanceAdapter.dll"
    )


if __name__ == "__main__":
    raise SystemExit(main())
