#!/usr/bin/env python3
"""
DevHub conformance runner。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


HOST_ROOT = Path(__file__).resolve().parents[2]
REPO_ROOT = Path(__file__).resolve().parents[3]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.blackbox.test_base import get_test_project_root  # type: ignore  # noqa: E402
from tests.conformance.raw_protocol_helper import (  # type: ignore  # noqa: E402
    OrchestrationFailure,
    RawProtocolHelper,
    load_orchestration_phase,
)
from tests.conformance.vector_setup import (  # type: ignore  # noqa: E402
    HostRuntimeContext,
    VectorExecutionContext,
    materialize_vector,
    read_log_tail,
    sanitize_file_name,
    start_suite_host,
    stop_process,
)


SCRIPT_DIR = Path(__file__).resolve().parent
OFFICIAL_SDKS = ("dotnet", "typescript", "python")
MANIFEST_VERSION = 1
WILDCARD_ANY_ISO_UTC = "${ANY_ISO_UTC}"
WILDCARD_ANY_NON_EMPTY_STRING = "${ANY_NON_EMPTY_STRING}"
WILDCARD_ANY_NON_NEGATIVE_INT = "${ANY_NON_NEGATIVE_INT}"
SNAPSHOT_DIR_NAME = "conformance_snapshots"
CASE_ID_PATTERN = re.compile(r"^CONF-\d{3}$")
ALLOWED_ADAPTER_OUTCOMES = frozenset({"success", "error"})
ADAPTER_TIMEOUT_SECONDS = 60


@dataclass(frozen=True)
class AdapterTarget:
    """描述一次 conformance 执行目标。"""

    name: str
    command_prefix: tuple[str, ...]
    working_directory: Path
    env_overrides: dict[str, str]
    source: str
    is_official: bool

    def build_command(self, context_path: Path) -> list[str]:
        """为当前执行上下文拼出最终命令。"""

        return [*self.command_prefix, str(context_path)]

    def build_environment(self, context_path: Path) -> dict[str, str]:
        """为适配器进程构造环境变量。"""

        environment = os.environ.copy()
        environment["DEVHUB_CONFORMANCE_CONTEXT"] = str(context_path)
        environment.update(self.env_overrides)
        return environment


@dataclass(frozen=True)
class AdapterOutputContract:
    """描述单类向量期望的 adapter 输出契约。"""

    phase: str
    allowed_operations: tuple[str, ...] | None = None


def main() -> int:
    args = parse_args()
    suite_directory = resolve_suite_directory(args)
    adapter_targets = resolve_adapter_targets(args)
    ensure_prerequisites(adapter_targets)

    vectors = load_vectors(suite_directory, args.vector_id, args.case_id)
    if not vectors:
        print("未找到符合条件的向量。", file=sys.stderr)
        return 1

    failure_count = 0
    snapshot_run_root = build_snapshot_run_root()
    with tempfile.TemporaryDirectory(prefix="devhub-conformance-") as temp_root_str:
        temp_root = Path(temp_root_str).resolve()
        host_context, process, log_file = start_suite_host(temp_root)
        try:
            for vector_path, vector in vectors:
                try:
                    result = run_vector(
                        vector_path,
                        vector,
                        host_context,
                        temp_root,
                        adapter_targets,
                    )
                except Exception as exc:  # noqa: BLE001
                    failure_count += 1
                    failures = attach_failure_snapshots(
                        [
                            {
                                "vectorId": vector.get("id", str(vector_path)),
                                "sdk": "runner",
                                "expected": vector.get("expectedDiscovery") or vector.get("expectedResponse"),
                                "actual": None,
                                "diffFields": ["$runner"],
                                "message": str(exc),
                                "vectorPath": str(vector_path),
                                "resolvedVector": vector,
                            }
                        ],
                        log_file=log_file,
                        snapshot_run_root=snapshot_run_root,
                    )
                    emit_failures(failures)
                    continue

                if not result["passed"]:
                    failure_count += 1
                    failures = attach_failure_snapshots(
                        result["failures"],
                        log_file=log_file,
                        snapshot_run_root=snapshot_run_root,
                    )
                    emit_failures(failures)
                    continue

                print(f"PASS  {format_vector_label(vector)}")
        finally:
            stop_process(process)
            log_file.close()

    print(f"SUMMARY  total={len(vectors)} passed={len(vectors) - failure_count} failed={failure_count}")
    return 0 if failure_count == 0 else 1


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="运行 DevHub conformance 向量。")
    parser.add_argument("--suite", default="v1.0.1", help="默认扫描的 suite 目录名。")
    parser.add_argument("--directory", help="显式指定向量目录。")
    parser.add_argument("--vector-id", help="只执行指定向量 ID。")
    parser.add_argument("--case-id", help="只执行指定 CONF-* 编号。")
    parser.add_argument(
        "--official-sdk",
        action="append",
        choices=OFFICIAL_SDKS,
        help="只启用指定官方适配器；可重复传入多次。",
    )
    parser.add_argument(
        "--adapter-manifest",
        action="append",
        help="加载外部适配器 manifest；可重复传入多次。",
    )
    parser.add_argument(
        "--include-official-adapters",
        action="store_true",
        help="当存在 --adapter-manifest 时，额外附带官方适配器一起执行。",
    )
    return parser.parse_args()


def resolve_suite_directory(args: argparse.Namespace) -> Path:
    if args.directory:
        return Path(args.directory).resolve()
    return (SCRIPT_DIR / args.suite).resolve()


def resolve_adapter_targets(args: argparse.Namespace) -> list[AdapterTarget]:
    manifest_targets = load_manifest_adapters(args.adapter_manifest or [])

    if args.official_sdk:
        official_names = deduplicate_names(args.official_sdk)
    elif manifest_targets and not args.include_official_adapters:
        official_names = []
    else:
        official_names = list(OFFICIAL_SDKS)

    targets = [*build_official_adapter_targets(official_names), *manifest_targets]
    if not targets:
        raise ValueError("至少需要一个 conformance 适配器。")

    duplicate_names = find_duplicate_names(target.name for target in targets)
    if duplicate_names:
        raise ValueError(f"适配器名称重复：{duplicate_names}。")

    return targets


def load_manifest_adapters(manifest_paths: list[str]) -> list[AdapterTarget]:
    targets: list[AdapterTarget] = []
    for manifest_path in manifest_paths:
        targets.extend(load_adapter_manifest(Path(manifest_path).resolve()))
    return targets


def load_adapter_manifest(manifest_path: Path) -> list[AdapterTarget]:
    if not manifest_path.is_file():
        raise FileNotFoundError(f"未找到适配器 manifest：{manifest_path}")

    payload = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict):
        raise ValueError(f"适配器 manifest 必须为对象：{manifest_path}")

    manifest_version = payload.get("manifestVersion")
    if manifest_version != MANIFEST_VERSION:
        raise ValueError(
            f"适配器 manifestVersion 不支持：{manifest_path} -> {manifest_version!r}；"
            f"当前仅支持 {MANIFEST_VERSION}。"
        )

    adapters = payload.get("adapters")
    if not isinstance(adapters, list) or not adapters:
        raise ValueError(f"适配器 manifest 必须包含非空 adapters 数组：{manifest_path}")

    targets: list[AdapterTarget] = []
    manifest_dir = manifest_path.parent
    for index, item in enumerate(adapters):
        if not isinstance(item, dict):
            raise ValueError(f"adapters[{index}] 必须为对象：{manifest_path}")

        entry_path = f"{manifest_path}::adapters[{index}]"
        name = require_manifest_string(item.get("name"), f"{entry_path}.name")
        command = read_manifest_command(item.get("command"), f"{entry_path}.command")
        working_directory = resolve_manifest_working_directory(
            manifest_dir,
            item.get("cwd"),
            f"{entry_path}.cwd",
        )
        env_overrides = read_manifest_env(item.get("env"), f"{entry_path}.env")

        targets.append(
            AdapterTarget(
                name=name,
                command_prefix=tuple(command),
                working_directory=working_directory,
                env_overrides=env_overrides,
                source=str(manifest_path),
                is_official=False,
            )
        )

    return targets


def build_official_adapter_targets(official_names: list[str]) -> list[AdapterTarget]:
    targets: list[AdapterTarget] = []
    working_directory = Path(get_test_project_root()).resolve()

    for sdk_name in official_names:
        if sdk_name == "python":
            command_prefix = (
                sys.executable,
                str(get_python_adapter_script_path()),
            )
        elif sdk_name == "typescript":
            command_prefix = (
                "node",
                str(get_typescript_adapter_script_path()),
            )
        elif sdk_name == "dotnet":
            command_prefix = (
                "dotnet",
                str(get_dotnet_adapter_dll_path()),
            )
        else:
            raise ValueError(f"不支持的官方适配器：{sdk_name}")

        targets.append(
            AdapterTarget(
                name=sdk_name,
                command_prefix=command_prefix,
                working_directory=working_directory,
                env_overrides={},
                source=f"official:{sdk_name}",
                is_official=True,
            )
        )

    return targets


def deduplicate_names(values: Iterable[str]) -> list[str]:
    ordered: list[str] = []
    seen: set[str] = set()
    for value in values:
        if value in seen:
            continue
        ordered.append(value)
        seen.add(value)
    return ordered


def find_duplicate_names(values: Iterable[str]) -> list[str]:
    seen: set[str] = set()
    duplicates: list[str] = []
    for value in values:
        if value in seen and value not in duplicates:
            duplicates.append(value)
            continue
        seen.add(value)
    return duplicates


def require_manifest_string(value: Any, path: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{path} 必须为非空字符串。")
    return value


def read_manifest_command(value: Any, path: str) -> list[str]:
    if not isinstance(value, list) or not value:
        raise ValueError(f"{path} 必须为非空字符串数组。")

    command: list[str] = []
    for index, item in enumerate(value):
        command.append(require_manifest_string(item, f"{path}[{index}]"))
    return command


def read_manifest_env(value: Any, path: str) -> dict[str, str]:
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ValueError(f"{path} 必须为对象。")

    env: dict[str, str] = {}
    for key, item in value.items():
        if not isinstance(key, str) or not key.strip():
            raise ValueError(f"{path} 的键必须为非空字符串。")
        if not isinstance(item, str):
            raise ValueError(f"{path}.{key} 必须为字符串。")
        env[key] = item
    return env


def resolve_manifest_working_directory(manifest_dir: Path, value: Any, path: str) -> Path:
    if value is None:
        resolved = manifest_dir.resolve()
    else:
        raw_path = require_manifest_string(value, path)
        candidate = Path(raw_path)
        resolved = candidate.resolve() if candidate.is_absolute() else (manifest_dir / candidate).resolve()

    if not resolved.is_dir():
        raise FileNotFoundError(f"{path} 指向的目录不存在：{resolved}")
    return resolved


def ensure_prerequisites(adapter_targets: list[AdapterTarget]) -> None:
    selected_official = {target.name for target in adapter_targets if target.is_official}

    if "dotnet" in selected_official:
        dotnet_dll = get_dotnet_adapter_dll_path()
        if not dotnet_dll.is_file():
            raise FileNotFoundError(
                "未找到 .NET conformance 适配器，请先运行 "
                "'dotnet build sdks/dotnet/DevHub.DotNetSdk.slnx -c Release'。"
            )

    if "python" in selected_official:
        python_adapter = get_python_adapter_script_path()
        if not python_adapter.is_file():
            raise FileNotFoundError(f"未找到 Python conformance 适配器脚本：{python_adapter}")

    if "typescript" in selected_official:
        typescript_adapter = get_typescript_adapter_script_path()
        if not typescript_adapter.is_file():
            raise FileNotFoundError(f"未找到 JS/TS conformance 适配器脚本：{typescript_adapter}")
        javascript_dist = REPO_ROOT / "sdks" / "javascript" / "dist" / "index.js"
        if not javascript_dist.is_file():
            raise FileNotFoundError(
                "未找到 JS SDK dist/index.js，请先运行 'npm --prefix sdks/javascript run build'。"
            )


def load_vectors(
    directory: Path,
    vector_id: str | None,
    case_id: str | None,
) -> list[tuple[Path, dict[str, Any]]]:
    if not directory.is_dir():
        raise FileNotFoundError(f"未找到向量目录：{directory}")

    vectors: list[tuple[Path, dict[str, Any]]] = []
    for path in sorted(directory.glob("*.json")):
        payload = json.loads(path.read_text(encoding="utf-8"))
        validate_vector_shape(path, payload)
        if vector_id and payload["id"] != vector_id:
            continue
        if case_id and payload["caseId"] != case_id:
            continue
        vectors.append((path, payload))

    return vectors


def validate_vector_shape(path: Path, payload: dict[str, Any]) -> None:
    required_fields = ["id", "description", "transport", "request", "expectedResponse", "tags"]
    missing = [field for field in required_fields if field not in payload]
    if missing:
        raise ValueError(f"向量缺少必需字段：{path} -> {missing}")

    case_id = payload.get("caseId")
    if not isinstance(case_id, str) or not CASE_ID_PATTERN.fullmatch(case_id):
        raise ValueError(f"向量 caseId 非法：{path} -> {case_id!r}")


def build_snapshot_run_root() -> Path:
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return REPO_ROOT / "temp" / SNAPSHOT_DIR_NAME / timestamp


def attach_failure_snapshots(
    failures: list[dict[str, Any]],
    *,
    log_file,
    snapshot_run_root: Path,
) -> list[dict[str, Any]]:
    if not failures:
        return failures

    host_log_tail = read_log_tail(log_file)
    for failure in failures:
        failure["snapshotPath"] = str(write_failure_snapshot(failure, snapshot_run_root, host_log_tail))
    return failures


def write_failure_snapshot(failure: dict[str, Any], snapshot_run_root: Path, host_log_tail: str) -> Path:
    vector_id = sanitize_file_name(format_failure_label(failure))
    sdk_name = sanitize_file_name(str(failure.get("sdk", "unknown-sdk")))
    snapshot_dir = snapshot_run_root / vector_id / sdk_name
    snapshot_dir.mkdir(parents=True, exist_ok=True)

    payload = {
        "caseId": get_failure_case_id(failure),
        "vectorId": failure.get("vectorId"),
        "sdk": failure.get("sdk"),
        "message": failure.get("message"),
        "diffFields": failure.get("diffFields"),
        "expected": failure.get("expected"),
        "actual": failure.get("actual"),
        "vectorPath": failure.get("vectorPath"),
        "adapterMeta": failure.get("adapterMeta"),
    }
    (snapshot_dir / "failure.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    resolved_vector = failure.get("resolvedVector")
    if resolved_vector is not None:
        (snapshot_dir / "resolved-vector.json").write_text(
            json.dumps(resolved_vector, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    if host_log_tail:
        (snapshot_dir / "suite-host.log").write_text(host_log_tail, encoding="utf-8")

    adapter_meta = failure.get("adapterMeta")
    if isinstance(adapter_meta, dict):
        stdout = adapter_meta.get("stdout")
        stderr = adapter_meta.get("stderr")
        if isinstance(stdout, str):
            (snapshot_dir / "adapter.stdout.txt").write_text(stdout, encoding="utf-8")
        if isinstance(stderr, str):
            (snapshot_dir / "adapter.stderr.txt").write_text(stderr, encoding="utf-8")

    return snapshot_dir


def run_vector(
    vector_path: Path,
    vector: dict[str, Any],
    host_context: HostRuntimeContext,
    temp_root: Path,
    adapter_targets: list[AdapterTarget],
) -> dict[str, Any]:
    is_discovery = "expectedDiscovery" in vector
    failures: list[dict[str, Any]] = []
    normalized_results: dict[str, Any] = {}

    for adapter in adapter_targets:
        execution: VectorExecutionContext | None = None
        try:
            execution = materialize_vector(
                vector_path,
                vector,
                host_context,
                temp_root,
                execution_name=adapter.name,
            )
            adapter_result = execute_adapter_vector(adapter, execution, host_context)
            expected = (
                execution.resolved_vector["expectedDiscovery"]
                if is_discovery
                else execution.resolved_vector["expectedResponse"]
            )

            helper_invocation_id = adapter_result.pop("_helperInvocationId", None)
            adapter_meta = adapter_result.pop("_adapterMeta", None)
            should_validate_contract = adapter_result.pop("_contractValidated", False)

            if should_validate_contract:
                contract_failure = validate_adapter_result_contract(execution.resolved_vector, adapter_result)
                if contract_failure is not None:
                    failures.append(
                        {
                            "vectorId": execution.resolved_vector["id"],
                            "sdk": adapter.name,
                            "expected": contract_failure["expected"],
                            "actual": contract_failure["actual"],
                            "diffFields": contract_failure["diffFields"],
                            "message": contract_failure["message"],
                            "vectorPath": str(vector_path),
                            "resolvedVector": execution.resolved_vector,
                            "adapterMeta": adapter_meta,
                        }
                    )
                    continue

            if helper_invocation_id is not None:
                caller_invocation_id = extract_caller_invocation_id(adapter_result)
                if caller_invocation_id and caller_invocation_id != helper_invocation_id:
                    failures.append(
                        {
                            "vectorId": execution.resolved_vector["id"],
                            "sdk": adapter.name,
                            "expected": {"invocationId": helper_invocation_id},
                            "actual": adapter_result.get("actual"),
                            "diffFields": ["$.actual.invocationId"],
                            "message": "caller 与 helper 观察到的 invocationId 不一致。",
                            "vectorPath": str(vector_path),
                            "resolvedVector": execution.resolved_vector,
                            "adapterMeta": adapter_meta,
                        }
                    )
                    continue

            if adapter_result.get("error") is not None:
                failures.append(
                    {
                        "vectorId": execution.resolved_vector["id"],
                        "sdk": adapter.name,
                        "expected": expected,
                        "actual": adapter_result.get("actual"),
                        "diffFields": ["$process"],
                        "message": adapter_result["error"],
                        "vectorPath": str(vector_path),
                        "resolvedVector": execution.resolved_vector,
                        "adapterMeta": adapter_meta,
                    }
                )
                continue

            comparable = build_comparable_payload(adapter_result, is_discovery)
            diffs = collect_differences(expected, comparable)
            if diffs:
                failures.append(
                    {
                        "vectorId": execution.resolved_vector["id"],
                        "sdk": adapter.name,
                        "expected": expected,
                        "actual": comparable,
                        "diffFields": diffs,
                        "vectorPath": str(vector_path),
                        "resolvedVector": execution.resolved_vector,
                        "adapterMeta": adapter_meta,
                    }
                )
                continue

            if not is_discovery:
                normalized_results[adapter.name] = canonicalize_with_expected(comparable, expected)
        except OrchestrationFailure as exc:
            failures.append(
                {
                    "vectorId": vector["id"],
                    "sdk": adapter.name,
                    "expected": exc.expected,
                    "actual": exc.actual,
                    "diffFields": exc.diff_fields or ["$helper"],
                    "message": (
                        "helper 编排失败: "
                        f"phase={exc.phase}, step={exc.step_index}, action={exc.action}, detail={exc.message}"
                    ),
                    "vectorPath": str(vector_path),
                    "resolvedVector": execution.resolved_vector if execution is not None else vector,
                    "adapterMeta": build_static_adapter_meta(adapter),
                }
            )
        except Exception as exc:  # noqa: BLE001
            failures.append(
                {
                    "vectorId": vector["id"],
                    "sdk": adapter.name,
                    "expected": vector.get("expectedDiscovery") or vector.get("expectedResponse"),
                    "actual": None,
                    "diffFields": ["$runner"],
                    "message": str(exc),
                    "vectorPath": str(vector_path),
                    "resolvedVector": execution.resolved_vector if execution is not None else vector,
                    "adapterMeta": build_static_adapter_meta(adapter),
                }
            )
        finally:
            if execution is not None:
                execution.cleanup(host_context)

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
                        "vectorPath": str(vector_path),
                        "resolvedVector": vector,
                    }
                )

    return {
        "passed": not failures,
        "failures": failures,
    }


def execute_adapter_vector(
    adapter: AdapterTarget,
    execution: VectorExecutionContext,
    host_context: HostRuntimeContext,
) -> dict[str, Any]:
    command = adapter.build_command(execution.context_path)
    if should_use_orchestration(execution.resolved_vector):
        return run_adapter_with_orchestration(adapter, command, execution, host_context)
    return run_adapter(adapter, command, execution.context_path)


def should_use_orchestration(vector: dict[str, Any]) -> bool:
    request = vector.get("request")
    if isinstance(request, dict):
        kind = request.get("kind")
        if isinstance(kind, str) and kind.startswith("sdk."):
            return True
    return isinstance(vector.get("orchestration"), dict)


def run_adapter_with_orchestration(
    adapter: AdapterTarget,
    command: list[str],
    execution: VectorExecutionContext,
    host_context: HostRuntimeContext,
) -> dict[str, Any]:
    helper = RawProtocolHelper(
        vector_id=execution.resolved_vector["id"],
        host_context=host_context,
        cleanup_ledger=execution.cleanup_ledger,
    )
    helper.run_phase("beforeCaller", load_orchestration_phase(execution.resolved_vector, "beforeCaller"))

    process = subprocess.Popen(
        command,
        cwd=adapter.working_directory,
        env=adapter.build_environment(execution.context_path),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )

    try:
        helper.run_phase("duringCaller", load_orchestration_phase(execution.resolved_vector, "duringCaller"))
        result = wait_adapter_process(adapter, process)
        helper.run_phase("afterCaller", load_orchestration_phase(execution.resolved_vector, "afterCaller"))
    except Exception:
        terminate_adapter_process(process)
        raise
    finally:
        if process.poll() is None:
            terminate_adapter_process(process)

    if helper.state.last_invocation_id is not None:
        result["_helperInvocationId"] = helper.state.last_invocation_id
    return result


def run_adapter(adapter: AdapterTarget, command: list[str], context_path: Path) -> dict[str, Any]:
    try:
        completed = subprocess.run(
            command,
            cwd=adapter.working_directory,
            env=adapter.build_environment(context_path),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=ADAPTER_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = normalize_process_output(exc.stdout)
        stderr = normalize_process_output(exc.stderr)
        return {
            "sdk": adapter.name,
            "error": {
                "message": "适配器进程执行超时。",
                "exitCode": None,
                "stdout": stdout,
                "stderr": stderr,
            },
            "actual": None,
            "_contractValidated": False,
            "_adapterMeta": build_adapter_meta(adapter, None, stdout, stderr),
        }

    return parse_adapter_output(adapter, completed.stdout, completed.stderr, completed.returncode)


def wait_adapter_process(adapter: AdapterTarget, process: subprocess.Popen[str], timeout_seconds: int = 60) -> dict[str, Any]:
    try:
        stdout, stderr = process.communicate(timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        process.kill()
        stdout, stderr = process.communicate()
        return {
            "sdk": adapter.name,
            "error": {
                "message": "适配器进程执行超时。",
                "exitCode": process.returncode,
                "stdout": stdout.strip(),
                "stderr": stderr.strip(),
            },
            "actual": None,
            "_contractValidated": False,
            "_adapterMeta": build_adapter_meta(adapter, process.returncode, stdout, stderr),
        }

    return parse_adapter_output(adapter, stdout, stderr, process.returncode or 0)


def normalize_process_output(value: str | bytes | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace").strip()
    return value.strip()


def parse_adapter_output(adapter: AdapterTarget, stdout: str, stderr: str, exit_code: int) -> dict[str, Any]:
    stdout = stdout.strip()
    stderr = stderr.strip()
    parsed = try_parse_json_line(stdout)
    if parsed is None:
        return {
            "sdk": adapter.name,
            "error": {
                "message": "适配器未输出可解析 JSON。",
                "exitCode": exit_code,
                "stdout": stdout,
                "stderr": stderr,
            },
            "actual": None,
            "_contractValidated": False,
            "_adapterMeta": build_adapter_meta(adapter, exit_code, stdout, stderr),
        }

    parsed["sdk"] = adapter.name
    if exit_code != 0 and parsed.get("error") is None:
        parsed["error"] = {
            "message": "适配器进程返回非零退出码。",
            "exitCode": exit_code,
            "stderr": stderr,
        }
    parsed["_contractValidated"] = True
    parsed["_adapterMeta"] = build_adapter_meta(adapter, exit_code, stdout, stderr)
    return parsed


def build_static_adapter_meta(adapter: AdapterTarget) -> dict[str, Any]:
    return {
        "adapter": adapter.name,
        "source": adapter.source,
        "workingDirectory": str(adapter.working_directory),
        "official": adapter.is_official,
    }


def build_adapter_meta(
    adapter: AdapterTarget,
    exit_code: int | None,
    stdout: str,
    stderr: str,
) -> dict[str, Any]:
    payload = build_static_adapter_meta(adapter)
    payload.update(
        {
            "exitCode": exit_code,
            "stdout": stdout,
            "stderr": stderr,
        }
    )
    return payload


def terminate_adapter_process(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return

    process.kill()
    try:
        process.communicate(timeout=5)
    except subprocess.TimeoutExpired:
        pass


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

    phase = result.get("phase")
    if phase in {"sdk-invocation", "sdk-events", "ws"}:
        comparable = {
            "phase": phase,
            "outcome": result.get("outcome"),
            "actual": result.get("actual"),
        }
        if "operation" in result:
            comparable["operation"] = result.get("operation")
        return comparable

    return result.get("actual")


def validate_adapter_result_contract(vector: dict[str, Any], result: dict[str, Any]) -> dict[str, Any] | None:
    contract = resolve_adapter_output_contract(vector)
    diffs: list[str] = []

    if result.get("vectorId") != vector["id"]:
        diffs.append("$contract.vectorId")

    if result.get("phase") != contract.phase:
        diffs.append("$contract.phase")

    if result.get("outcome") not in ALLOWED_ADAPTER_OUTCOMES:
        diffs.append("$contract.outcome")

    if "actual" not in result:
        diffs.append("$contract.actual")

    if contract.allowed_operations is not None and result.get("operation") not in contract.allowed_operations:
        diffs.append("$contract.operation")

    error = result.get("error")
    if error is not None:
        if not isinstance(error, dict):
            diffs.append("$contract.error")
        else:
            message = error.get("message")
            if not isinstance(message, str) or not message.strip():
                diffs.append("$contract.error.message")

    if not diffs:
        return None

    return {
        "expected": build_adapter_contract_expectation(vector, contract),
        "actual": build_adapter_contract_actual(result),
        "diffFields": diffs,
        "message": "适配器输出不符合 conformance 输出契约。",
    }


def resolve_adapter_output_contract(vector: dict[str, Any]) -> AdapterOutputContract:
    if "expectedDiscovery" in vector:
        return AdapterOutputContract(phase="discovery")

    request = vector.get("request")
    kind = request.get("kind") if isinstance(request, dict) else None
    if kind in {"sdk.notify", "sdk.request"}:
        return AdapterOutputContract(phase="sdk-invocation", allowed_operations=("notify", "request"))
    if kind == "sdk.events":
        return AdapterOutputContract(phase="sdk-events")
    if kind == "raw.ws" or vector.get("transport") == "ws":
        return AdapterOutputContract(phase="ws")
    return AdapterOutputContract(phase="rpc")


def build_adapter_contract_expectation(
    vector: dict[str, Any],
    contract: AdapterOutputContract,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "vectorId": vector["id"],
        "phase": contract.phase,
        "outcome": sorted(ALLOWED_ADAPTER_OUTCOMES),
        "actualPresent": True,
        "error": {
            "message": "required when error is not null",
        },
    }
    if contract.allowed_operations is not None:
        payload["operation"] = list(contract.allowed_operations)
    return payload


def build_adapter_contract_actual(result: dict[str, Any]) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "vectorId": result.get("vectorId"),
        "phase": result.get("phase"),
        "outcome": result.get("outcome"),
        "actualPresent": "actual" in result,
        "error": result.get("error"),
    }
    if "operation" in result:
        payload["operation"] = result.get("operation")
    return payload


def extract_caller_invocation_id(result: dict[str, Any]) -> str | None:
    actual = result.get("actual")
    if not isinstance(actual, dict):
        return None

    candidate = actual.get("invocationId")
    return candidate if isinstance(candidate, str) and candidate else None


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

    if expected == WILDCARD_ANY_NON_NEGATIVE_INT:
        return isinstance(actual, int) and actual >= 0

    return False


def emit_failures(failures: Iterable[dict[str, Any]]) -> None:
    for failure in failures:
        print(f"FAIL  {format_failure_label(failure)}  [{failure['sdk']}]")
        if failure.get("message"):
            print(f"      Message: {json.dumps(failure['message'], ensure_ascii=True)}")
        print(f"      Expected: {json.dumps(failure['expected'], ensure_ascii=True, sort_keys=True)}")
        print(f"      Actual: {json.dumps(failure['actual'], ensure_ascii=True, sort_keys=True)}")
        print(f"      Diff: {', '.join(failure['diffFields'])}")
        if failure.get("snapshotPath"):
            print(f"      Snapshot: {failure['snapshotPath']}")


def format_vector_label(vector: dict[str, Any]) -> str:
    case_id = vector.get("caseId")
    vector_id = vector["id"]
    if isinstance(case_id, str) and case_id:
        return f"{case_id} {vector_id}"
    return str(vector_id)


def get_failure_case_id(failure: dict[str, Any]) -> str | None:
    case_id = failure.get("caseId")
    if isinstance(case_id, str) and case_id:
        return case_id

    resolved_vector = failure.get("resolvedVector")
    if isinstance(resolved_vector, dict):
        resolved_case_id = resolved_vector.get("caseId")
        if isinstance(resolved_case_id, str) and resolved_case_id:
            return resolved_case_id

    return None


def format_failure_label(failure: dict[str, Any]) -> str:
    vector_id = str(failure.get("vectorId", "unknown-vector"))
    case_id = get_failure_case_id(failure)
    if case_id:
        return f"{case_id} {vector_id}"
    return vector_id


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


def get_python_adapter_script_path() -> Path:
    return REPO_ROOT / "sdks" / "python" / "tests" / "conformance" / "devhub_conformance_py.py"


def get_typescript_adapter_script_path() -> Path:
    return REPO_ROOT / "sdks" / "javascript" / "tests" / "conformance" / "devhub_conformance_js.mjs"


if __name__ == "__main__":
    raise SystemExit(main())
