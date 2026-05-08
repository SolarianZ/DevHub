#!/usr/bin/env python3
"""
DevHub conformance runner 回归测试。
"""

from __future__ import annotations

import io
import json
import subprocess
import sys
import tempfile
import textwrap
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


HOST_ROOT = Path(__file__).resolve().parents[2]
REPO_ROOT = Path(__file__).resolve().parents[3]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.conformance import vector_runner  # type: ignore  # noqa: E402


SUITE_DIRECTORY = REPO_ROOT / "host" / "tests" / "conformance" / "v1.0.1"


class TestConformanceRunner(unittest.TestCase):
    """验证 conformance runner 的外部适配器挂接能力。"""

    def test_resolve_adapter_targets_with_manifest_should_skip_official_by_default(self) -> None:
        with tempfile.TemporaryDirectory(prefix="devhub-conformance-manifest-") as temp_root_str:
            temp_root = Path(temp_root_str)
            manifest_path = temp_root / "external-adapter.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "manifestVersion": 1,
                        "adapters": [
                            {
                                "name": "external-python",
                                "command": [sys.executable, "dummy_adapter.py"],
                                "cwd": ".",
                                "env": {"DEVHUB_CONFORMANCE_TEST": "enabled"},
                            }
                        ],
                    },
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8-sig",
            )

            args = SimpleNamespace(
                official_sdk=None,
                adapter_manifest=[str(manifest_path)],
                include_official_adapters=False,
            )

            targets = vector_runner.resolve_adapter_targets(args)

            self.assertEqual(["external-python"], [target.name for target in targets])
            self.assertFalse(targets[0].is_official)
            self.assertEqual(temp_root.resolve(), targets[0].working_directory)
            self.assertEqual({"DEVHUB_CONFORMANCE_TEST": "enabled"}, targets[0].env_overrides)

    def test_ensure_prerequisites_with_external_only_should_not_require_official_outputs(self) -> None:
        external_target = vector_runner.AdapterTarget(
            name="external-python",
            command_prefix=(sys.executable, "dummy_adapter.py"),
            working_directory=REPO_ROOT,
            env_overrides={},
            source="manifest",
            is_official=False,
        )

        with mock.patch.object(
            vector_runner,
            "get_dotnet_adapter_dll_path",
            side_effect=AssertionError("不应访问官方 .NET 适配器产物。"),
        ):
            vector_runner.ensure_prerequisites([external_target])

    def test_vector_runner_with_external_manifest_should_pass_single_vector(self) -> None:
        with tempfile.TemporaryDirectory(prefix="devhub-conformance-external-") as temp_root_str:
            temp_root = Path(temp_root_str)
            adapter_path = temp_root / "dummy_adapter.py"
            adapter_path.write_text(
                textwrap.dedent(
                    """
                    import json
                    import os
                    import sys
                    from pathlib import Path

                    def main() -> int:
                        if len(sys.argv) != 2:
                            raise SystemExit("需要 execution-context.json 参数。")
                        if os.environ.get("DEVHUB_CONFORMANCE_TEST") != "enabled":
                            raise SystemExit("缺少 manifest env。")
                        if os.environ.get("DEVHUB_CONFORMANCE_CONTEXT") != sys.argv[1]:
                            raise SystemExit("context 环境变量与参数不一致。")

                        context_path = Path(sys.argv[1])
                        context = json.loads(context_path.read_text(encoding="utf-8"))
                        payload = dict(context["vector"]["expectedDiscovery"])
                        payload["vectorId"] = context["vector"]["id"]
                        print(json.dumps(payload, ensure_ascii=False))
                        return 0

                    if __name__ == "__main__":
                        raise SystemExit(main())
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )

            manifest_path = temp_root / "external-adapter.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "manifestVersion": 1,
                        "adapters": [
                            {
                                "name": "external-python",
                                "command": [sys.executable, "dummy_adapter.py"],
                                "cwd": ".",
                                "env": {"DEVHUB_CONFORMANCE_TEST": "enabled"},
                            }
                        ],
                    },
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8",
            )

            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "host" / "tests" / "conformance" / "vector_runner.py"),
                    "--adapter-manifest",
                    str(manifest_path),
                    "--vector-id",
                    "discovery.valid_runtime_layout_reads_token",
                ],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )

            self.assertEqual(
                0,
                completed.returncode,
                msg=f"stdout:\n{completed.stdout}\n\nstderr:\n{completed.stderr}",
            )
            self.assertIn(
                "PASS  CONF-001 discovery.valid_runtime_layout_reads_token",
                completed.stdout,
            )
            self.assertIn("SUMMARY  total=1 passed=1 failed=0", completed.stdout)

    def test_vector_runner_with_external_manifest_when_contract_invalid_should_report_fail_and_snapshot(self) -> None:
        with tempfile.TemporaryDirectory(prefix="devhub-conformance-contract-invalid-") as temp_root_str:
            temp_root = Path(temp_root_str)
            adapter_path = temp_root / "invalid_adapter.py"
            adapter_path.write_text(
                textwrap.dedent(
                    """
                    import json
                    import sys
                    from pathlib import Path

                    def main() -> int:
                        if len(sys.argv) != 2:
                            raise SystemExit("需要 execution-context.json 参数。")
                        context_path = Path(sys.argv[1])
                        context = json.loads(context_path.read_text(encoding="utf-8"))
                        print(json.dumps(
                            {
                                "vectorId": context["vector"]["id"],
                                "outcome": "success",
                                "actual": {},
                                "error": None,
                            },
                            ensure_ascii=False,
                        ))
                        return 0

                    if __name__ == "__main__":
                        raise SystemExit(main())
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )

            manifest_path = temp_root / "external-adapter.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "manifestVersion": 1,
                        "adapters": [
                            {
                                "name": "external-python",
                                "command": [sys.executable, "invalid_adapter.py"],
                                "cwd": ".",
                            }
                        ],
                    },
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8",
            )

            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "host" / "tests" / "conformance" / "vector_runner.py"),
                    "--adapter-manifest",
                    str(manifest_path),
                    "--vector-id",
                    "discovery.valid_runtime_layout_reads_token",
                ],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )

            self.assertEqual(
                1,
                completed.returncode,
                msg=f"stdout:\n{completed.stdout}\n\nstderr:\n{completed.stderr}",
            )
            self.assertIn(
                "FAIL  CONF-001 discovery.valid_runtime_layout_reads_token  [external-python]",
                completed.stdout,
            )
            self.assertIn('Message: "\\u9002\\u914d\\u5668\\u8f93\\u51fa\\u4e0d\\u7b26\\u5408 conformance \\u8f93\\u51fa\\u5951\\u7ea6\\u3002"', completed.stdout)
            self.assertIn("Diff: $contract.phase", completed.stdout)
            self.assertIn("Snapshot:", completed.stdout)
            self.assertIn("SUMMARY  total=1 passed=0 failed=1", completed.stdout)

    def test_CONF_005_vector_runner_should_support_case_id_filter(self) -> None:
        with tempfile.TemporaryDirectory(prefix="devhub-conformance-case-id-") as temp_root_str:
            temp_root = Path(temp_root_str)
            adapter_path = temp_root / "dummy_adapter.py"
            adapter_path.write_text(
                textwrap.dedent(
                    """
                    import json
                    import os
                    import sys
                    from pathlib import Path

                    def main() -> int:
                        if len(sys.argv) != 2:
                            raise SystemExit("需要 execution-context.json 参数。")
                        if os.environ.get("DEVHUB_CONFORMANCE_TEST") != "enabled":
                            raise SystemExit("缺少 manifest env。")

                        context_path = Path(sys.argv[1])
                        context = json.loads(context_path.read_text(encoding="utf-8"))
                        payload = dict(context["vector"]["expectedDiscovery"])
                        payload["vectorId"] = context["vector"]["id"]
                        print(json.dumps(payload, ensure_ascii=False))
                        return 0

                    if __name__ == "__main__":
                        raise SystemExit(main())
                    """
                ).strip()
                + "\n",
                encoding="utf-8",
            )

            manifest_path = temp_root / "external-adapter.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "manifestVersion": 1,
                        "adapters": [
                            {
                                "name": "external-python",
                                "command": [sys.executable, "dummy_adapter.py"],
                                "cwd": ".",
                                "env": {"DEVHUB_CONFORMANCE_TEST": "enabled"},
                            }
                        ],
                    },
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8",
            )

            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "host" / "tests" / "conformance" / "vector_runner.py"),
                    "--adapter-manifest",
                    str(manifest_path),
                    "--case-id",
                    "CONF-001",
                ],
                cwd=REPO_ROOT,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )

            self.assertEqual(
                0,
                completed.returncode,
                msg=f"stdout:\n{completed.stdout}\n\nstderr:\n{completed.stderr}",
            )
            self.assertIn("PASS  CONF-001 discovery.valid_runtime_layout_reads_token", completed.stdout)
            self.assertIn("PASS  CONF-001 discovery.env_override_reads_runtime", completed.stdout)
            self.assertIn("PASS  CONF-001 discovery.runtime_dir_as_data_dir_rejected", completed.stdout)
            self.assertIn("PASS  CONF-001 discovery.invalid_wsurl_custom_path_rejected", completed.stdout)
            self.assertIn("PASS  CONF-001 discovery.invalid_wsurl_query_rejected", completed.stdout)
            self.assertIn("SUMMARY  total=5 passed=5 failed=0", completed.stdout)

    def test_CONF_006_emit_failures_should_include_case_id_and_diff_fields(self) -> None:
        buffer = io.StringIO()
        failure = {
            "vectorId": "discovery.valid_runtime_layout_reads_token",
            "sdk": "typescript",
            "expected": {"phase": "discovery"},
            "actual": {"phase": "error"},
            "diffFields": ["$.phase"],
            "resolvedVector": {
                "id": "discovery.valid_runtime_layout_reads_token",
                "caseId": "CONF-001",
            },
        }

        with redirect_stdout(buffer):
            vector_runner.emit_failures([failure])

        output = buffer.getvalue()
        self.assertIn("FAIL  CONF-001 discovery.valid_runtime_layout_reads_token  [typescript]", output)
        self.assertIn("Diff: $.phase", output)

    def test_validate_adapter_result_contract_when_phase_missing_should_report_contract_phase(self) -> None:
        vector = load_vector("discovery.valid_runtime_layout_reads_token")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": vector["id"],
                "outcome": "success",
                "actual": {},
                "error": None,
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.phase", failure["diffFields"])

    def test_validate_adapter_result_contract_when_phase_mismatched_should_report_contract_phase(self) -> None:
        vector = load_vector("auth.valid_credentials_ping_success")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": vector["id"],
                "phase": "ws",
                "outcome": "success",
                "actual": {},
                "error": None,
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.phase", failure["diffFields"])

    def test_validate_adapter_result_contract_when_sdk_invocation_operation_missing_should_report_contract_operation(self) -> None:
        vector = load_vector("invocation.request.default_global_roundtrip_uses_spec_defaults")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": vector["id"],
                "phase": "sdk-invocation",
                "outcome": "success",
                "actual": {},
                "error": None,
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.operation", failure["diffFields"])

    def test_validate_adapter_result_contract_when_sdk_invocation_operation_invalid_should_report_contract_operation(self) -> None:
        vector = load_vector("invocation.request.default_global_roundtrip_uses_spec_defaults")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": vector["id"],
                "phase": "sdk-invocation",
                "operation": "launch",
                "outcome": "success",
                "actual": {},
                "error": None,
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.operation", failure["diffFields"])

    def test_validate_adapter_result_contract_when_vector_id_mismatch_should_report_contract_vector_id(self) -> None:
        vector = load_vector("discovery.valid_runtime_layout_reads_token")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": "other.vector",
                "phase": "discovery",
                "outcome": "success",
                "actual": {},
                "error": None,
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.vectorId", failure["diffFields"])

    def test_validate_adapter_result_contract_when_error_message_missing_should_report_contract_error_message(self) -> None:
        vector = load_vector("discovery.valid_runtime_layout_reads_token")

        failure = vector_runner.validate_adapter_result_contract(
            vector,
            {
                "vectorId": vector["id"],
                "phase": "discovery",
                "outcome": "error",
                "actual": None,
                "error": {},
            },
        )

        self.assertIsNotNone(failure)
        self.assertIn("$contract.error.message", failure["diffFields"])

    def test_collect_differences_when_instance_event_scope_omitted_in_expectation_should_report_extra_scope(self) -> None:
        expected = {
            "phase": "sdk-events",
            "outcome": "success",
            "actual": {
                "event": {
                    "type": "app.instance.registered",
                    "payload": {
                        "appId": "events.subscribe.registered",
                        "instanceId": "events-register-inst-1",
                    },
                }
            },
        }
        actual = {
            "phase": "sdk-events",
            "outcome": "success",
            "actual": {
                "event": {
                    "type": "app.instance.registered",
                    "payload": {
                        "appId": "events.subscribe.registered",
                        "instanceId": "events-register-inst-1",
                        "scope": "",
                    },
                }
            },
        }

        diffs = vector_runner.collect_differences(expected, actual)

        self.assertEqual(["$.actual.event.payload.scope"], diffs)

    def test_collect_differences_should_preserve_unexpected_fields_in_instance_event_payload(self) -> None:
        expected = {
            "phase": "sdk-events",
            "outcome": "success",
            "actual": {
                "event": {
                    "type": "app.instance.registered",
                    "payload": {
                        "appId": "events.subscribe.registered",
                        "instanceId": "events-register-inst-1",
                    },
                }
            },
        }
        actual = {
            "phase": "sdk-events",
            "outcome": "success",
            "actual": {
                "event": {
                    "type": "app.instance.registered",
                    "payload": {
                        "appId": "events.subscribe.registered",
                        "instanceId": "events-register-inst-1",
                        "scope": "",
                        "extra": True,
                    },
                }
            },
        }

        diffs = vector_runner.collect_differences(expected, actual)

        self.assertEqual(["$.actual.event.payload.scope", "$.actual.event.payload.extra"], diffs)

    def test_collect_subset_differences_should_allow_extra_fields(self) -> None:
        expected = {
            "phase": "ws",
            "outcome": "success",
            "actual": {
                "event": {
                    "payload": {
                        "invocationId": "${ANY_NON_EMPTY_STRING}",
                        "delivery": {
                            "attempt": 1,
                        },
                    }
                }
            },
        }
        actual = {
            "phase": "ws",
            "outcome": "success",
            "actual": {
                "event": {
                    "payload": {
                        "invocationId": "invk-123",
                        "appId": "events.sample",
                        "delivery": {
                            "attempt": 1,
                            "leaseToken": "secret",
                        },
                    }
                }
            },
        }

        diffs = vector_runner.collect_subset_differences(expected, actual)

        self.assertEqual([], diffs)

    def test_collect_forbidden_path_differences_should_report_present_paths(self) -> None:
        actual = {
            "phase": "ws",
            "outcome": "success",
            "actual": {
                "event": {
                    "payload": {
                        "delivery": {
                            "attempt": 1,
                            "leaseToken": "secret",
                        }
                    }
                }
            },
        }

        diffs = vector_runner.collect_forbidden_path_differences(
            actual,
            ["$.actual.event.payload.delivery.leaseToken", "$.actual.event.payload.reason"],
        )

        self.assertEqual(["$.actual.event.payload.delivery.leaseToken"], diffs)

    def test_parse_expected_spec_should_extract_match_mode_and_forbid_paths(self) -> None:
        expected_spec = {
            "matchMode": "subset",
            "forbidPaths": ["$.actual.event.payload.delivery.leaseToken"],
            "phase": "ws",
            "actual": {
                "event": {
                    "type": "invocation.delivered",
                }
            },
        }

        expected, match_mode, forbid_paths = vector_runner.parse_expected_spec(expected_spec)

        self.assertEqual("subset", match_mode)
        self.assertEqual(("$.actual.event.payload.delivery.leaseToken",), forbid_paths)
        self.assertNotIn("matchMode", expected)
        self.assertNotIn("forbidPaths", expected)

    def test_run_adapter_when_process_times_out_should_return_process_error(self) -> None:
        adapter = vector_runner.AdapterTarget(
            name="timeout-adapter",
            command_prefix=(sys.executable, "dummy.py"),
            working_directory=REPO_ROOT,
            env_overrides={},
            source="manifest",
            is_official=False,
        )

        timeout = subprocess.TimeoutExpired(
            cmd=["python", "dummy.py"],
            timeout=vector_runner.ADAPTER_TIMEOUT_SECONDS,
            output="partial stdout\n",
            stderr="partial stderr\n",
        )

        with mock.patch.object(vector_runner.subprocess, "run", side_effect=timeout):
            result = vector_runner.run_adapter(adapter, ["python", "dummy.py"], REPO_ROOT / "execution-context.json")

        self.assertEqual("timeout-adapter", result["sdk"])
        self.assertIsNone(result["actual"])
        self.assertFalse(result["_contractValidated"])
        self.assertEqual("适配器进程执行超时。", result["error"]["message"])
        self.assertEqual("partial stdout", result["error"]["stdout"])
        self.assertEqual("partial stderr", result["error"]["stderr"])
        self.assertIsNone(result["error"]["exitCode"])


def load_vector(vector_id: str) -> dict[str, object]:
    path = SUITE_DIRECTORY / f"{vector_id}.json"
    return json.loads(path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
