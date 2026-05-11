#!/usr/bin/env python3
"""
DevHub 仓库级黑盒测试运行器（default/smoke/fast/full）
"""

import logging
import os
import shutil
import sys
import time
from contextlib import contextmanager
from pathlib import Path

HOST_ROOT = Path(__file__).resolve().parents[2]
if str(HOST_ROOT) not in sys.path:
    sys.path.insert(0, str(HOST_ROOT))

from tests.blackbox.test_base import (
    TEST_BUILD_HOST_ENV_VAR,
    TEST_HUB_COMMAND_ENV_VAR,
    TEST_HUB_CWD_ENV_VAR,
    TEST_HUB_ENV_JSON_ENV_VAR,
    DiscoveryService,
    LongWaitStatus,
    PENDING_WAIT_STATUS,
    RpcClient,
    TestReport,
    create_temp_directory,
    describe_test_hub_command,
    poll_until_deadline_with_long_wait_status,
    start_isolated_hub_process,
    temporary_env_var,
)
from tests.blackbox.test_launch_discovery import TestLaunchDiscovery
from tests.blackbox.test_auth_protocol import TestAuthProtocol
from tests.blackbox.test_ws_events import TestWsEvents
from tests.blackbox.test_ws_transport_matrix import TestWsTransportMatrix
from tests.blackbox.test_app_definitions import TestAppDefinitions
from tests.blackbox.test_app_instances import TestAppInstances
from tests.blackbox.test_scope_routing import TestScopeRouting
from tests.blackbox.test_invocation_notify import TestInvocationNotify
from tests.blackbox.test_invocation_request import TestInvocationRequest
from tests.blackbox.test_invocation_poll_respond import TestInvocationPollRespond
from tests.blackbox.test_invoke_poll_respond_edges import TestInvokePollRespondEdges
from tests.blackbox.test_launch_invocation import TestLaunchInvocation
from tests.blackbox.test_launch_spec_edges import TestLaunchSpecEdges
from tests.blackbox.test_invalid_params import TestInvalidParams
from tests.blackbox.test_internal_errors import TestInternalErrors


DATA_DIR_ENV_VAR = "DEVHUB_DATA_DIR"
USE_EXISTING_HOST_ENV_VAR = "DEVHUB_TEST_USE_EXISTING_HOST"


def setup_logging(log_file):
    """设置日志"""
    # Windows 下默认控制台编码可能是 gbk，写入 emoji 会触发 UnicodeEncodeError。
    # 优先切换到 UTF-8；若失败则使用 backslashreplace 保证日志不中断。
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="backslashreplace")
            except OSError:
                reconfigure(errors="backslashreplace")

    logging.basicConfig(
        level=logging.INFO,
        format='%(asctime)s - %(levelname)s - %(message)s',
        handlers=[
            logging.FileHandler(log_file, encoding="utf-8"),
            logging.StreamHandler(sys.stdout)
        ],
        force=True
    )
    return logging.getLogger(__name__)


class StageSpinner:
    """测试阶段状态输出。"""

    def __init__(self, stage_name, logger, stream=None):
        self._status = LongWaitStatus(
            stage_name,
            emit_plain=lambda message: logger.info("%s", message),
            stream=stream or sys.stdout,
            enter_immediately=True,
            plain_message_factory=lambda: f"[状态] {stage_name} 开始",
            live_message_factory=self._build_live_message(stage_name),
        )

    def start(self):
        """启动阶段状态输出。"""
        self._status.start()

    def stop(self):
        """停止阶段状态输出。"""
        self._status.finish()

    @staticmethod
    def _build_live_message(stage_name):
        frames = ("|", "/", "-", "\\")

        def render(elapsed_seconds):
            frame = frames[elapsed_seconds % len(frames)]
            return f"{frame} {stage_name} 进行中... {elapsed_seconds}s"

        return render


def run_suite_with_spinner(logger, stage_name, runner):
    """在执行长耗时测试套件时显示活动状态。"""
    spinner = StageSpinner(stage_name, logger)
    started_at = time.monotonic()
    spinner.start()
    try:
        return runner()
    finally:
        spinner.stop()
        elapsed_seconds = time.monotonic() - started_at
        logger.info("=== %s 完成，用时 %.1fs ===", stage_name, elapsed_seconds)


def emit_failure_summary(text_report_path, json_report_path, failed_results):
    """向标准输出打印失败摘要，确保 CI 日志中可直接看到失败原因。"""
    print("=== FAILURE SUMMARY BEGIN ===")
    print(f"文本报告: {text_report_path}")
    print(f"JSON 报告: {json_report_path}")

    for index, result in enumerate(failed_results, start=1):
        print(f"[{index}] {result.test_name}")
        if result.error_message:
            print(f"  错误: {result.error_message}")
        for detail in result.details:
            print(f"  详情: {detail}")

    print("=== FAILURE SUMMARY END ===")
    sys.stdout.flush()


def _read_boolean_env(name, default):
    """读取布尔环境变量。"""
    raw_value = os.environ.get(name)
    if raw_value is None:
        return default

    candidate = raw_value.strip().lower()
    if candidate in ("", "1", "true", "yes", "on"):
        return True
    if candidate in ("0", "false", "no", "off"):
        return False

    raise ValueError(f"{name} 必须是布尔值（true/false/1/0）")


def _read_log_tail(log_path, max_chars=4000):
    """读取 Host 日志尾部，便于拼接错误信息。"""
    try:
        content = Path(log_path).read_text(encoding="utf-8", errors="replace")
    except FileNotFoundError:
        return ""
    except OSError:
        return ""

    content = content.strip()
    if len(content) > max_chars:
        return content[-max_chars:]
    return content


def _wait_for_suite_host_ready(data_dir, process, log_path):
    """等待 runner 自启的隔离 Host 可用。"""
    last_error = "unknown"

    def poll_once():
        nonlocal last_error

        if process.poll() is not None:
            raise RuntimeError(f"隔离 Host 提前退出，日志片段：{_read_log_tail(log_path)}")

        with temporary_env_var(DATA_DIR_ENV_VAR, data_dir):
            try:
                base_url, token = DiscoveryService.get_hub_info()
                response = RpcClient(base_url, token).call("hub.ping")
                if response.get("result", {}).get("ok") is True:
                    return None
                last_error = str(response)
            except Exception as exc:  # noqa: BLE001
                last_error = str(exc)

        return PENDING_WAIT_STATUS

    poll_until_deadline_with_long_wait_status(
        label="等待 runner 级隔离 Host 就绪",
        timeout_seconds=45,
        poll_interval_seconds=0.25,
        poll_once=poll_once,
        on_timeout=lambda: PENDING_WAIT_STATUS,
    )

    if process.poll() is not None:
        raise RuntimeError(f"隔离 Host 提前退出，日志片段：{_read_log_tail(log_path)}")

    with temporary_env_var(DATA_DIR_ENV_VAR, data_dir):
        try:
            base_url, token = DiscoveryService.get_hub_info()
            response = RpcClient(base_url, token).call("hub.ping")
            if response.get("result", {}).get("ok") is True:
                return
            last_error = str(response)
        except Exception as exc:  # noqa: BLE001
            last_error = str(exc)

    raise RuntimeError(f"等待隔离 Host 就绪超时：{last_error}；日志片段：{_read_log_tail(log_path)}")


@contextmanager
def managed_suite_host(logger, temp_dir):
    """按需启动整个黑盒 runner 复用的隔离 Host。"""
    if _read_boolean_env(USE_EXISTING_HOST_ENV_VAR, default=False):
        logger.info("使用外部已启动 Host，数据根目录: %s", os.environ.get(DATA_DIR_ENV_VAR, "<平台默认目录>"))
        yield None
        return

    suite_root = Path(temp_dir) / "suite-host"
    data_dir = suite_root / "data"
    log_path = suite_root / "host.log"

    shutil.rmtree(suite_root, ignore_errors=True)
    suite_root.mkdir(parents=True, exist_ok=True)

    logger.info("启动 runner 级隔离 Host，命令: %s", describe_test_hub_command())
    with open(log_path, "w+", encoding="utf-8") as log_file:
        process = start_isolated_hub_process(str(data_dir), log_file)
        try:
            _wait_for_suite_host_ready(str(data_dir), process, str(log_path))
            logger.info("runner 级隔离 Host 已就绪，数据根目录: %s", data_dir)
            with temporary_env_var(DATA_DIR_ENV_VAR, str(data_dir)):
                yield {"data_dir": str(data_dir), "log_path": str(log_path)}
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except Exception:  # noqa: BLE001
                    process.kill()
                    process.wait(timeout=10)


def execute_all_tests(temp_dir, logger, full=False, fast=False, smoke=False):
    """执行黑盒测试主体。"""

    if smoke:
        mode = "smoke"
        coverage = "跨平台最小冒烟回归（发现/鉴权/WS 传输矩阵/request 主链路）"
    elif full:
        mode = "full"
        coverage = "仓库级黑盒严格回归（含 lease 重投递 full 严格断言/并发去重 full 场景/scope 扩展与 WS failed 事件场景）"
    elif fast:
        mode = "fast"
        coverage = "仓库级黑盒快速回归（跳过 lease 重投递与并发 dedupe 等长耗时场景）"
    else:
        mode = "default"
        coverage = "仓库级黑盒默认回归（核心链路 + Spec MUST，含轻量 lease 重投递与轻量并发 dedupe）"
    logger.info("开始 DevHub 仓库级黑盒测试，模式: %s", mode)

    # 创建测试报告
    report = TestReport(mode=mode, coverage=coverage)

    if smoke:
        logger.info("=== 运行启动与发现 smoke 测试 ===")
        launch_discovery_tests = TestLaunchDiscovery()
        report.results.extend(launch_discovery_tests.run_all_tests(full=False))

        logger.info("=== 运行鉴权 smoke 测试 ===")
        auth_protocol_tests = TestAuthProtocol()
        report.results.extend([
            auth_protocol_tests.test_ping_with_valid_credentials(),
            auth_protocol_tests.test_ping_with_invalid_token(),
            auth_protocol_tests.test_ping_without_protocol_header(),
            auth_protocol_tests.test_rpc_options_preflight_returns_cors_headers(),
            auth_protocol_tests.test_post_with_origin_returns_cors_headers_on_success(),
            auth_protocol_tests.test_post_with_origin_returns_cors_headers_on_jsonrpc_error(),
        ])

        logger.info("=== 运行 WebSocket 事件 smoke 测试 ===")
        ws_events_tests = TestWsEvents()
        report.results.extend([
            ws_events_tests.test_ws_001_first_message_must_authenticate(),
            ws_events_tests.test_ws_005_subscribe_unsubscribe_should_work_after_auth(),
        ])

        logger.info("=== 运行 WebSocket 传输矩阵 smoke 测试 ===")
        ws_transport_matrix_tests = TestWsTransportMatrix()
        report.results.extend([
            ws_transport_matrix_tests.test_ws_matrix_001_ping_should_work_after_auth(),
            ws_transport_matrix_tests.test_ws_matrix_006_http_only_methods_should_be_rejected_over_ws(),
            ws_transport_matrix_tests.test_ws_matrix_007_ws_only_methods_should_be_rejected_over_http(),
        ])

        logger.info("=== 运行 Invocation Request smoke 测试 ===")
        invocation_request_tests = TestInvocationRequest()
        report.results.extend([
            invocation_request_tests.test_request_roundtrip_success(),
        ])
    else:
        # 运行各个模块的测试
        logger.info("=== 运行启动与发现测试 ===")
        launch_discovery_tests = TestLaunchDiscovery()
        report.results.extend(launch_discovery_tests.run_all_tests(full=full))

        logger.info("=== 运行鉴权与协议版本测试 ===")
        auth_protocol_tests = TestAuthProtocol()
        report.results.extend(auth_protocol_tests.run_all_tests(full=full))

        logger.info("=== 运行 WebSocket 事件测试 ===")
        ws_events_tests = TestWsEvents()
        report.results.extend(ws_events_tests.run_all_tests(full=full))

        logger.info("=== 运行 WebSocket 传输矩阵测试 ===")
        ws_transport_matrix_tests = TestWsTransportMatrix()
        report.results.extend(ws_transport_matrix_tests.run_all_tests(full=full))

        logger.info("=== 运行 AppDefinition 测试 ===")
        app_definitions_tests = TestAppDefinitions()
        report.results.extend(app_definitions_tests.run_all_tests(full=full))

        logger.info("=== 运行 AppInstance 测试 ===")
        app_instances_tests = TestAppInstances()
        report.results.extend(app_instances_tests.run_all_tests(full=full, run_timeout_tests=full))

        scope_routing_tests = TestScopeRouting()
        report.results.extend(
            run_suite_with_spinner(
                logger,
                "运行 Scope 路由测试",
                lambda: scope_routing_tests.run_all_tests(full=full)))

        logger.info("=== 运行 Invocation Notify 测试 ===")
        invocation_notify_tests = TestInvocationNotify()
        report.results.extend(invocation_notify_tests.run_all_tests(full=full))

        logger.info("=== 运行 Invocation Request 测试 ===")
        invocation_request_tests = TestInvocationRequest()
        report.results.extend(invocation_request_tests.run_all_tests(full=full))

        invocation_poll_respond_tests = TestInvocationPollRespond()
        report.results.extend(
            run_suite_with_spinner(
                logger,
                "运行 Invocation Poll/Respond 测试",
                lambda: invocation_poll_respond_tests.run_all_tests(full=full, fast=fast)))

        invoke_poll_respond_edges_tests = TestInvokePollRespondEdges()
        report.results.extend(
            run_suite_with_spinner(
                logger,
                "运行 Invocation Poll/Respond 规范边界测试",
                lambda: invoke_poll_respond_edges_tests.run_all_tests(full=full)))

        logger.info("=== 运行 Launch + Invocation 测试 ===")
        launch_invocation_tests = TestLaunchInvocation()
        report.results.extend(launch_invocation_tests.run_all_tests(full=full, fast=fast))

        logger.info("=== 运行 Launch 规范边界测试 ===")
        launch_spec_edges_tests = TestLaunchSpecEdges()
        report.results.extend(launch_spec_edges_tests.run_all_tests(full=full))

        logger.info("=== 运行 invalid_params 参数验证测试 ===")
        invalid_params_tests = TestInvalidParams()
        report.results.extend(invalid_params_tests.run_all_tests(full=full))

        logger.info("=== 运行 internal_error 内部服务器错误测试 ===")
        internal_errors_tests = TestInternalErrors()
        report.results.extend(internal_errors_tests.run_all_tests(full=full))

    # 保存报告
    json_report_path = os.path.join(temp_dir, "test_results.json")
    text_report_path = os.path.join(temp_dir, "test_results.txt")

    report.save_to_file(json_report_path)
    report.save_text_report(text_report_path)

    logger.info(f"测试完成")
    logger.info(f"JSON 报告: {json_report_path}")
    logger.info(f"文本报告: {text_report_path}")

    # 打印测试摘要
    summary = report.get_summary()
    logger.info(f"测试摘要:")
    logger.info(f"  总测试数: {summary['total']}")
    logger.info(f"  通过数: {summary['passed']}")
    logger.info(f"  失败数: {summary['failed']}")
    logger.info(f"  成功率: {summary['success_rate']:.1f}%")

    # 如果有失败的测试，返回 1
    if summary['failed'] > 0:
        failed_results = [item for item in report.results if not item.success]
        logger.warning("⚠️  测试中有失败的用例")
        emit_failure_summary(text_report_path, json_report_path, failed_results)
        return 1

    logger.info("✅ 所有测试通过")
    return 0


def run_all_tests(full=False, fast=False, smoke=False):
    """运行所有测试"""
    temp_dir = create_temp_directory()
    log_file = os.path.join(temp_dir, "test_log.txt")
    logger = setup_logging(log_file)

    with managed_suite_host(logger, temp_dir):
        return execute_all_tests(temp_dir, logger, full=full, fast=fast, smoke=smoke)


def print_usage():
    """打印使用说明"""
    print("Usage: python host/tests/blackbox/test_runner.py [options]")
    print()
    print("Options:")
    print("  -h, --help    Show this help message and exit")
    print("  --no-header   Don't print test header")
    print("  --smoke       Run minimal cross-platform smoke suite")
    print("  --fast        Run fast suite (skip timeout/offline long tests)")
    print("  --full        Run full suite including timeout/offline long tests")
    print("  --use-existing-host   Reuse an already running Host instead of auto-starting an isolated Host")
    print("  --no-build-host       Skip the default pre-build step before auto-starting an isolated Host")
    print("  --isolated-hub-command   Override the launcher used by isolated hub tests")
    print("  --isolated-hub-cwd       Override the working directory used by isolated hub tests")
    print("  --isolated-hub-env-json  Extra JSON env overrides for isolated hub tests")


def main():
    """主函数"""
    import argparse

    parser = argparse.ArgumentParser(description="DevHub 仓库级黑盒测试运行器")
    parser.add_argument("--no-header", action="store_true", help="Don't print test header")

    mode_group = parser.add_mutually_exclusive_group()
    mode_group.add_argument("--smoke", action="store_true", help="Run minimal cross-platform smoke suite")
    mode_group.add_argument("--fast", action="store_true", help="Run fast suite (skip timeout/offline long tests)")
    mode_group.add_argument("--full", action="store_true", help="Run full suite including timeout/offline long tests")
    parser.add_argument("--use-existing-host", action="store_true", help="Reuse an already running Host")
    parser.add_argument("--no-build-host", action="store_true", help="Skip the default Host pre-build step")
    parser.add_argument("--isolated-hub-command", help="Override the launcher used by isolated hub tests")
    parser.add_argument("--isolated-hub-cwd", help="Override the working directory used by isolated hub tests")
    parser.add_argument("--isolated-hub-env-json", help="Extra JSON env overrides for isolated hub tests")

    args = parser.parse_args()

    if args.isolated_hub_command:
        os.environ[TEST_HUB_COMMAND_ENV_VAR] = args.isolated_hub_command
    if args.isolated_hub_cwd:
        os.environ[TEST_HUB_CWD_ENV_VAR] = args.isolated_hub_cwd
    if args.isolated_hub_env_json:
        os.environ[TEST_HUB_ENV_JSON_ENV_VAR] = args.isolated_hub_env_json
    if args.use_existing_host:
        os.environ[USE_EXISTING_HOST_ENV_VAR] = "1"
    if args.no_build_host:
        os.environ[TEST_BUILD_HOST_ENV_VAR] = "0"

    if not args.no_header:
        print("=" * 60)
        print("DevHub 仓库级黑盒测试")
        print("=" * 60)
        print()

    # 运行测试
    return run_all_tests(full=args.full, fast=args.fast, smoke=args.smoke)


if __name__ == "__main__":
    exit_code = main()
    sys.exit(exit_code)
