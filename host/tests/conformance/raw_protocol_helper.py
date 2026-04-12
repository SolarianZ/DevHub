#!/usr/bin/env python3
"""
DevHub conformance raw-protocol helper。
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from tests.conformance.vector_setup import (  # type: ignore  # noqa: E402
    CleanupLedger,
    HostRuntimeContext,
    register_instance,
    require_mapping,
    require_optional_list,
    require_optional_string,
    require_string,
)


WILDCARD_ANY_ISO_UTC = "${ANY_ISO_UTC}"
WILDCARD_ANY_NON_EMPTY_STRING = "${ANY_NON_EMPTY_STRING}"
WILDCARD_ANY_NON_NEGATIVE_INT = "${ANY_NON_NEGATIVE_INT}"


class OrchestrationFailure(RuntimeError):
    """包装 helper 编排失败，便于 runner 输出统一错误。"""

    def __init__(
        self,
        *,
        phase: str,
        step_index: int,
        action: str,
        message: str,
        expected: Any,
        actual: Any,
        diff_fields: list[str],
    ) -> None:
        super().__init__(message)
        self.phase = phase
        self.step_index = step_index
        self.action = action
        self.message = message
        self.expected = expected
        self.actual = actual
        self.diff_fields = diff_fields


@dataclass
class RawProtocolState:
    """记录 helper 过程中的临时状态。"""

    last_invocation_id: str | None = None
    last_invocation: dict[str, Any] | None = None
    observations: list[dict[str, Any]] = field(default_factory=list)
    named_values: dict[str, Any] = field(default_factory=dict)


class RawProtocolHelper:
    """通过原始 HTTP JSON-RPC 模拟被调用方。"""

    def __init__(
        self,
        *,
        vector_id: str,
        host_context: HostRuntimeContext,
        cleanup_ledger: CleanupLedger,
    ) -> None:
        self._vector_id = vector_id
        self._host_context = host_context
        self._cleanup_ledger = cleanup_ledger
        self._client = host_context.create_rpc_client()
        self._state = RawProtocolState()

    @property
    def state(self) -> RawProtocolState:
        return self._state

    def run_phase(self, phase: str, steps: list[Any] | None) -> None:
        """执行单个 orchestration phase。"""

        for index, raw_step in enumerate(steps or []):
            step = require_mapping(raw_step, f"orchestration.{phase}[{index}]")
            action = require_string(step.get("action"), f"orchestration.{phase}[{index}].action")

            try:
                self._run_step(phase, index, action, step)
            except OrchestrationFailure:
                raise
            except Exception as exc:  # noqa: BLE001
                raise OrchestrationFailure(
                    phase=phase,
                    step_index=index,
                    action=action,
                    message=str(exc),
                    expected=None,
                    actual=None,
                    diff_fields=["$helper"],
                ) from exc

    def _run_step(self, phase: str, index: int, action: str, step: dict[str, Any]) -> None:
        if action == "register_instance":
            instance = require_mapping(step.get("instance"), f"orchestration.{phase}[{index}].instance")
            password = require_optional_string(step.get("password"), f"orchestration.{phase}[{index}].password")
            register_instance(
                self._host_context,
                self._cleanup_ledger,
                instance,
                password=password,
                request_id=f"{self._vector_id}-{phase}-{index}",
                error_path=f"orchestration.{phase}[{index}]",
            )
            return

        if action == "sleep":
            time.sleep(read_non_negative_int(step, "waitMs", f"orchestration.{phase}[{index}]") / 1000)
            return

        if action == "call_rpc":
            self._call_rpc(phase, index, step)
            return

        if action == "poll_expect_invocation":
            self._poll_expect_invocation(phase, index, step)
            return

        if action == "poll_expect_empty":
            self._poll_expect_empty(phase, index, step)
            return

        if action == "respond_value":
            self._respond(phase, index, step, is_error=False)
            return

        if action == "respond_error":
            self._respond(phase, index, step, is_error=True)
            return

        raise ValueError(f"orchestration.{phase}[{index}].action 不支持：{action}")

    def _call_rpc(self, phase: str, index: int, step: dict[str, Any]) -> None:
        method = require_string(step.get("method"), f"orchestration.{phase}[{index}].method")
        params = step.get("params")
        request_id = step.get("requestId")
        if request_id is None:
            request_id = f"{self._vector_id}-{phase}-{index}"
        elif not isinstance(request_id, str) or not request_id.strip():
            raise ValueError(f"orchestration.{phase}[{index}].requestId 必须为非空字符串。")

        response = self._client.call(method, params, request_id=request_id)

        expected_error = step.get("expectedError")
        if expected_error is not None:
            if not isinstance(expected_error, dict):
                raise ValueError(f"orchestration.{phase}[{index}].expectedError 必须为对象。")
            ensure_error_response(
                response,
                expected_error,
                invocation_id=None,
                phase=phase,
                step_index=index,
                action="call_rpc",
            )
        else:
            ensure_success_response(
                response,
                phase=phase,
                step_index=index,
                action="call_rpc",
            )
            expected_result = step.get("expectedResult")
            if expected_result is not None:
                if not isinstance(expected_result, dict):
                    raise ValueError(f"orchestration.{phase}[{index}].expectedResult 必须为对象。")
                result = response.get("result")
                diffs = collect_subset_differences(expected_result, result)
                if diffs:
                    raise OrchestrationFailure(
                        phase=phase,
                        step_index=index,
                        action="call_rpc",
                        message="helper 调用 RPC 成功，但 result 与预期不一致。",
                        expected=expected_result,
                        actual=result,
                        diff_fields=diffs,
                    )

        capture_as = step.get("captureAs")
        if capture_as is not None:
            capture_key = require_string(capture_as, f"orchestration.{phase}[{index}].captureAs")
            if "error" in response and isinstance(response["error"], dict):
                self._state.named_values[capture_key] = response["error"]
            else:
                self._state.named_values[capture_key] = response.get("result")

        self._state.observations.append(
            {
                "phase": phase,
                "action": "call_rpc",
                "method": method,
                "requestId": request_id,
            }
        )

    def _poll_expect_invocation(self, phase: str, index: int, step: dict[str, Any]) -> None:
        instance_id = require_string(step.get("instanceId"), f"orchestration.{phase}[{index}].instanceId")
        max_count = read_non_negative_int(step, "maxCount", f"orchestration.{phase}[{index}]", default=10)
        if max_count < 1 or max_count > 100:
            raise ValueError(f"orchestration.{phase}[{index}].maxCount 必须位于 1..100。")

        wait_ms = max(read_non_negative_int(step, "waitMs", f"orchestration.{phase}[{index}]", default=1000), 4000)
        response = self._client.poll_once(
            instance_id,
            max_count=max_count,
            wait_ms=wait_ms,
            timeout_sec=max(30, wait_ms / 1000 + 5),
        )

        ensure_success_response(
            response,
            phase=phase,
            step_index=index,
            action="poll_expect_invocation",
        )

        items = response.get("result", {}).get("items", [])
        expected = step.get("expected")
        if expected is not None and not isinstance(expected, dict):
            raise ValueError(f"orchestration.{phase}[{index}].expected 必须为对象。")

        matched = find_matching_item(items, expected)
        if matched is None:
            raise OrchestrationFailure(
                phase=phase,
                step_index=index,
                action="poll_expect_invocation",
                message="poll 未拉取到符合预期的 invocation。",
                expected=expected,
                actual=items,
                diff_fields=["$.items"],
            )

        if expected:
            diffs = collect_subset_differences(expected, matched)
            if diffs:
                raise OrchestrationFailure(
                    phase=phase,
                    step_index=index,
                    action="poll_expect_invocation",
                    message="poll 返回的 invocation 与预期不一致。",
                    expected=expected,
                    actual=matched,
                    diff_fields=diffs,
                )

        invocation_id = matched.get("invocationId")
        if not isinstance(invocation_id, str) or not invocation_id:
            raise OrchestrationFailure(
                phase=phase,
                step_index=index,
                action="poll_expect_invocation",
                message="poll 返回的 invocation 缺少 invocationId。",
                expected={"invocationId": WILDCARD_ANY_NON_EMPTY_STRING},
                actual=matched,
                diff_fields=["$.invocationId"],
            )

        self._state.last_invocation = matched
        self._state.last_invocation_id = invocation_id
        self._state.observations.append(
            {
                "phase": phase,
                "action": "poll_expect_invocation",
                "instanceId": instance_id,
                "invocationId": invocation_id,
            }
        )

    def _poll_expect_empty(self, phase: str, index: int, step: dict[str, Any]) -> None:
        instance_id = require_string(step.get("instanceId"), f"orchestration.{phase}[{index}].instanceId")
        wait_ms = read_non_negative_int(step, "waitMs", f"orchestration.{phase}[{index}]", default=0)
        response = self._client.poll_once(
            instance_id,
            max_count=read_non_negative_int(step, "maxCount", f"orchestration.{phase}[{index}]", default=10),
            wait_ms=wait_ms,
            timeout_sec=max(30, wait_ms / 1000 + 5),
        )

        ensure_success_response(
            response,
            phase=phase,
            step_index=index,
            action="poll_expect_empty",
        )

        items = response.get("result", {}).get("items", [])
        if items:
            raise OrchestrationFailure(
                phase=phase,
                step_index=index,
                action="poll_expect_empty",
                message="poll 期望为空，但实际返回了 invocation。",
                expected=[],
                actual=items,
                diff_fields=["$.items"],
            )

    def _respond(self, phase: str, index: int, step: dict[str, Any], *, is_error: bool) -> None:
        instance_id = require_string(step.get("instanceId"), f"orchestration.{phase}[{index}].instanceId")
        invocation_id = step.get("invocationId") or self._state.last_invocation_id
        if not isinstance(invocation_id, str) or not invocation_id:
            raise ValueError(f"orchestration.{phase}[{index}] 缺少可用 invocationId。")

        params = {
            "instanceId": instance_id,
            "invocationId": invocation_id,
        }
        if is_error:
            params["error"] = require_mapping(step.get("error"), f"orchestration.{phase}[{index}].error")
        else:
            params["value"] = step.get("value")

        response = self._client.call(
            "hub.invoke.respond",
            params,
            request_id=f"{self._vector_id}-{phase}-{index}",
        )

        expected_error = step.get("expectedError")
        if expected_error is not None:
            if not isinstance(expected_error, dict):
                raise ValueError(f"orchestration.{phase}[{index}].expectedError 必须为对象。")
            ensure_error_response(
                response,
                expected_error,
                invocation_id=invocation_id,
                phase=phase,
                step_index=index,
                action="respond_error" if is_error else "respond_value",
            )
            return

        ensure_success_response(
            response,
            phase=phase,
            step_index=index,
            action="respond_error" if is_error else "respond_value",
        )


def load_orchestration_phase(vector: dict[str, Any], phase: str) -> list[Any]:
    """读取向量中指定阶段的 orchestration。"""

    orchestration = vector.get("orchestration")
    if orchestration is None:
        return []

    orchestration_mapping = require_mapping(orchestration, "orchestration")
    return require_optional_list(orchestration_mapping.get(phase), f"orchestration.{phase}")


def read_non_negative_int(step: dict[str, Any], key: str, path: str, default: int | None = None) -> int:
    """读取非负整数。"""

    if key not in step:
        if default is None:
            raise ValueError(f"{path}.{key} 必须存在。")
        return default

    value = step[key]
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValueError(f"{path}.{key} 必须为大于等于 0 的整数。")
    return value


def ensure_success_response(response: dict[str, Any], *, phase: str, step_index: int, action: str) -> None:
    """断言 RPC 响应为成功结果。"""

    if isinstance(response.get("error"), dict):
        raise OrchestrationFailure(
            phase=phase,
            step_index=step_index,
            action=action,
            message="helper 收到错误响应。",
            expected={"result": {"ok": True}},
            actual=response,
            diff_fields=["$.error"],
        )

    result = response.get("result")
    if not isinstance(result, dict) or result.get("ok") is not True:
        raise OrchestrationFailure(
            phase=phase,
            step_index=step_index,
            action=action,
            message="helper 响应缺少 result.ok=true。",
            expected={"result": {"ok": True}},
            actual=response,
            diff_fields=["$.result"],
        )


def ensure_error_response(
    response: dict[str, Any],
    expected_error: dict[str, Any],
    *,
    invocation_id: str | None,
    phase: str,
    step_index: int,
    action: str,
) -> None:
    """断言 RPC 响应为预期错误。"""

    error = response.get("error")
    if not isinstance(error, dict):
        raise OrchestrationFailure(
            phase=phase,
            step_index=step_index,
            action=action,
            message="helper 期望错误响应，但实际为成功结果。",
            expected={"error": expected_error},
            actual=response,
            diff_fields=["$.error"],
        )

    comparable = {
        "code": error.get("code"),
        "message": error.get("message"),
        "data": error.get("data"),
    }
    diffs = collect_subset_differences(expected_error, comparable)
    if diffs:
        raise OrchestrationFailure(
            phase=phase,
            step_index=step_index,
            action=action,
            message="helper 错误响应与预期不一致。",
            expected=expected_error,
            actual=comparable,
            diff_fields=diffs,
        )

    actual_invocation_id = None
    if isinstance(error.get("data"), dict):
        candidate = error["data"].get("invocationId")
        actual_invocation_id = candidate if isinstance(candidate, str) else None
    if invocation_id and actual_invocation_id and actual_invocation_id != invocation_id:
        raise OrchestrationFailure(
            phase=phase,
            step_index=step_index,
            action=action,
            message="helper 错误响应中的 invocationId 与当前 invocation 不一致。",
            expected={"error": {"data": {"invocationId": invocation_id}}},
            actual=response,
            diff_fields=["$.error.data.invocationId"],
        )


def find_matching_item(items: Any, expected: dict[str, Any] | None) -> dict[str, Any] | None:
    """从 poll items 中挑选符合预期的 invocation。"""

    if not isinstance(items, list):
        return None

    if expected is None:
        if len(items) == 1 and isinstance(items[0], dict):
            return items[0]
        for item in items:
            if isinstance(item, dict):
                return item
        return None

    for item in items:
        if isinstance(item, dict) and not collect_subset_differences(expected, item):
            return item
    return None


def collect_subset_differences(expected: Any, actual: Any, path: str = "$") -> list[str]:
    """只校验 expected 子集，不要求 actual 无多余字段。"""

    if is_wildcard(expected, actual):
        return []

    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return [path]

        diffs: list[str] = []
        for key, expected_value in expected.items():
            child_path = f"{path}.{key}"
            if key not in actual:
                diffs.append(child_path)
                continue
            diffs.extend(collect_subset_differences(expected_value, actual[key], child_path))
        return diffs

    if isinstance(expected, list):
        if not isinstance(actual, list):
            return [path]
        if len(expected) != len(actual):
            return [path]
        diffs: list[str] = []
        for index, expected_item in enumerate(expected):
            diffs.extend(collect_subset_differences(expected_item, actual[index], f"{path}[{index}]"))
        return diffs

    if expected != actual:
        return [path]
    return []


def is_wildcard(expected: Any, actual: Any) -> bool:
    """支持与 runner 一致的通配符。"""

    if expected == WILDCARD_ANY_NON_EMPTY_STRING:
        return isinstance(actual, str) and actual.strip() != ""

    if expected == WILDCARD_ANY_NON_NEGATIVE_INT:
        return isinstance(actual, int) and actual >= 0

    if expected == WILDCARD_ANY_ISO_UTC:
        if not isinstance(actual, str):
            return False
        try:
            datetime.fromisoformat(actual.replace("Z", "+00:00"))
            return True
        except ValueError:
            return False

    return False
