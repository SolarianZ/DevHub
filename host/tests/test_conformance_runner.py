#!/usr/bin/env python3
"""
DevHub conformance runner 回归测试。
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


HOST_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = Path(__file__).resolve().parents[2]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.conformance import vector_runner  # type: ignore  # noqa: E402


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
            self.assertIn("PASS  discovery.valid_runtime_layout_reads_token", completed.stdout)
            self.assertIn("SUMMARY  total=1 passed=1 failed=0", completed.stdout)


if __name__ == "__main__":
    unittest.main()
