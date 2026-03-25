#!/usr/bin/env python3
"""
DevHub M5 最小 conformance runner。
"""

from __future__ import annotations

import argparse
import copy
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable


HOST_ROOT = Path(__file__).resolve().parents[2]
REPO_ROOT = Path(__file__).resolve().parents[3]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.test_base import (  # type: ignore  # noqa: E402
    RpcClient,
    get_test_project_root,
    start_isolated_hub_process,
)


SCRIPT_DIR = Path(__file__).resolve().parent
VECTOR_TOKEN_PATTERN = re.compile(r"\$\{([A-Z0-9_]+)\}")
WILDCARD_ANY_ISO_UTC = "${ANY_ISO_UTC}"
WILDCARD_ANY_NON_EMPTY_STRING = "${ANY_NON_EMPTY_STRING}"


@dataclass
class HostRuntimeContext:
    data_dir: Path
    runtime_dir: Path
    hub_json_path: Path
    token_file: Path
    token: str
    hub_info: dict[str, Any]

    @property
    def http_base_url(self) -> str:
        return str(self.hub_info["httpBaseUrl"])

    @property
    def ws_url(self) -> str:
        return str(self.hub_info["wsUrl"])


@dataclass
class VectorExecutionContext:
    source_path: Path
    resolved_vector: dict[str, Any]
    data_dir: Path
    environment_data_dir: Path | None
    context_path: Path


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
        host_context, process, log_file = start_isolated_hub(temp_root)
        try:
            for vector_path, vector in vectors:
                execution = materialize_vector(vector_path, vector, host_context, temp_root)
                result = run_vector(execution)
                if not result["passed"]:
                    failure_count += 1
                    emit_failures(result["failures"])
                else:
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


def start_isolated_hub(temp_root: Path) -> tuple[HostRuntimeContext, subprocess.Popen[str], Any]:
    host_data_dir = temp_root / "isolated-hub-data"
    host_data_dir.mkdir(parents=True, exist_ok=True)
    log_path = temp_root / "isolated-hub.log"
    log_file = open(log_path, "w+", encoding="utf-8")
    process = start_isolated_hub_process(str(host_data_dir), log_file)

    try:
        host_context = wait_for_host_runtime(host_data_dir, process, log_file)
        return host_context, process, log_file
    except Exception:
        stop_process(process)
        log_file.close()
        raise


def wait_for_host_runtime(
    host_data_dir: Path,
    process: subprocess.Popen[str],
    log_file,
) -> HostRuntimeContext:
    runtime_dir = host_data_dir / "runtime"
    hub_json_path = runtime_dir / "hub.json"
    deadline = time.time() + 45
    while time.time() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"隔离 Hub 提前退出，日志片段：{read_log_tail(log_file)}")
        if hub_json_path.is_file():
            break
        time.sleep(0.2)
    else:
        raise RuntimeError(f"等待隔离 Hub 生成 hub.json 超时，日志片段：{read_log_tail(log_file)}")

    hub_info = json.loads(hub_json_path.read_text(encoding="utf-8"))
    token_file = Path(hub_info["tokenFile"])
    if not token_file.is_file():
        raise FileNotFoundError(f"隔离 Hub 的 tokenFile 不存在：{token_file}")

    token = token_file.read_text(encoding="utf-8").strip()
    client = RpcClient(str(hub_info["httpBaseUrl"]), token)
    ping_deadline = time.time() + 20
    last_error = "unknown"
    while time.time() < ping_deadline:
        if process.poll() is not None:
            raise RuntimeError(f"隔离 Hub 在 ping 前退出，日志片段：{read_log_tail(log_file)}")
        try:
            response = client.call("hub.ping")
            if response.get("result", {}).get("ok") is True:
                return HostRuntimeContext(
                    data_dir=host_data_dir,
                    runtime_dir=runtime_dir,
                    hub_json_path=hub_json_path,
                    token_file=token_file,
                    token=token,
                    hub_info=hub_info,
                )
            last_error = json.dumps(response, ensure_ascii=False)
        except Exception as exc:  # noqa: BLE001
            last_error = str(exc)
        time.sleep(0.3)

    raise RuntimeError(f"隔离 Hub 在超时时间内不可达：{last_error}；日志片段：{read_log_tail(log_file)}")


def read_log_tail(log_file, max_chars: int = 4000) -> str:
    try:
        log_file.flush()
        log_file.seek(0)
        content = log_file.read()
    except Exception:  # noqa: BLE001
        return ""

    content = content.strip()
    if len(content) > max_chars:
        return content[-max_chars:]
    return content


def stop_process(process: subprocess.Popen[str] | None) -> None:
    if process is None or process.poll() is not None:
        return

    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def materialize_vector(
    vector_path: Path,
    vector: dict[str, Any],
    host_context: HostRuntimeContext,
    temp_root: Path,
) -> VectorExecutionContext:
    vector_temp_dir = temp_root / sanitize_file_name(str(vector["id"]))
    vector_temp_dir.mkdir(parents=True, exist_ok=True)

    data_dir = host_context.data_dir
    environment_data_dir: Path | None = None
    placeholders = build_host_placeholders(host_context)

    if "expectedDiscovery" in vector:
        runtime_setup = vector.get("setup", {}).get("runtime", {})
        if runtime_setup.get("mode") != "copy_host_runtime":
            raise ValueError(f"暂不支持的 Discovery setup：{vector_path}")

        data_dir = vector_temp_dir / "data"
        runtime_dir = data_dir / "runtime"
        runtime_dir.mkdir(parents=True, exist_ok=True)
        token_file = runtime_dir / "token.txt"
        token_file.write_text(host_context.token, encoding="utf-8")

        hub_info = copy.deepcopy(host_context.hub_info)
        hub_info["tokenFile"] = str(token_file)
        (runtime_dir / "hub.json").write_text(
            json.dumps(hub_info, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

        placeholders.update(
            {
                "VECTOR_DATA_DIR": str(data_dir),
                "VECTOR_RUNTIME_DIR": str(runtime_dir),
                "VECTOR_TOKEN_FILE": str(token_file),
            }
        )
        if vector["request"].get("useEnvironmentDataDir") is True:
            environment_data_dir = data_dir

    resolved_vector = substitute_placeholders(copy.deepcopy(vector), placeholders)
    context_payload = {
        "vector": resolved_vector,
        "dataDir": str(data_dir),
        "environmentDataDir": str(environment_data_dir) if environment_data_dir else None,
    }

    context_path = vector_temp_dir / "execution-context.json"
    context_path.write_text(json.dumps(context_payload, ensure_ascii=False, indent=2), encoding="utf-8")

    return VectorExecutionContext(
        source_path=vector_path,
        resolved_vector=resolved_vector,
        data_dir=data_dir,
        environment_data_dir=environment_data_dir,
        context_path=context_path,
    )


def build_host_placeholders(host_context: HostRuntimeContext) -> dict[str, str]:
    return {
        "HOST_DATA_DIR": str(host_context.data_dir),
        "HOST_RUNTIME_DIR": str(host_context.runtime_dir),
        "HOST_HUB_JSON": str(host_context.hub_json_path),
        "HOST_TOKEN_FILE": str(host_context.token_file),
        "HOST_TOKEN": host_context.token,
        "HOST_HTTP_BASE_URL": host_context.http_base_url,
        "HOST_WS_URL": host_context.ws_url,
    }


def substitute_placeholders(value: Any, placeholders: dict[str, str]) -> Any:
    if isinstance(value, str):
        return VECTOR_TOKEN_PATTERN.sub(lambda match: placeholders.get(match.group(1), match.group(0)), value)
    if isinstance(value, list):
        return [substitute_placeholders(item, placeholders) for item in value]
    if isinstance(value, dict):
        return {key: substitute_placeholders(item, placeholders) for key, item in value.items()}
    return value


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


def sanitize_file_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", value)


if __name__ == "__main__":
    raise SystemExit(main())
