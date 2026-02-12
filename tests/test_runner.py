#!/usr/bin/env python3
"""
DevHub M1~M4 测试运行器（default/smoke/fast/full）
"""

import os
import sys
import logging
import threading
import time

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import TestReport, create_temp_directory
from tests.test_launch_discovery import TestLaunchDiscovery
from tests.test_auth_protocol import TestAuthProtocol
from tests.test_ws_events import TestWsEvents
from tests.test_ws_transport_matrix import TestWsTransportMatrix
from tests.test_app_definitions import TestAppDefinitions
from tests.test_app_instances import TestAppInstances
from tests.test_scope_routing import TestScopeRouting
from tests.test_invocation_notify import TestInvocationNotify
from tests.test_invocation_request import TestInvocationRequest
from tests.test_invocation_poll_respond import TestInvocationPollRespond
from tests.test_invoke_poll_respond_edges import TestInvokePollRespondEdges
from tests.test_launch_invocation import TestLaunchInvocation
from tests.test_launch_spec_edges import TestLaunchSpecEdges
from tests.test_invalid_params import TestInvalidParams
from tests.test_internal_errors import TestInternalErrors


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
    """命令行原地旋转进度指示器。"""

    def __init__(self, stage_name, interval_seconds=0.2, stream=None):
        self.stage_name = stage_name
        self.interval_seconds = interval_seconds
        self.stream = stream or sys.stdout
        self._stop_event = threading.Event()
        self._thread = None
        self._start_monotonic = 0.0
        self._last_render_length = 0

    def start(self):
        """启动 spinner 线程。"""
        self._stop_event.clear()
        self._start_monotonic = time.monotonic()
        self._thread = threading.Thread(target=self._render_loop, name="devhub-test-spinner", daemon=True)
        self._thread.start()

    def stop(self):
        """停止 spinner 并清理当前行。"""
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
            self._thread = None

        if self._last_render_length > 0:
            clear_width = max(self._last_render_length, 120)
            self.stream.write("\r" + (" " * clear_width) + "\r")
            self.stream.flush()
            self._last_render_length = 0

    def _render_loop(self):
        frames = ("|", "/", "-", "\\")
        frame_index = 0
        while not self._stop_event.is_set():
            elapsed_seconds = int(time.monotonic() - self._start_monotonic)
            content = f"{frames[frame_index]} {self.stage_name} 进行中... {elapsed_seconds}s"
            self._last_render_length = max(self._last_render_length, len(content))
            self.stream.write("\r" + content)
            self.stream.flush()
            frame_index = (frame_index + 1) % len(frames)
            self._stop_event.wait(self.interval_seconds)


def run_suite_with_spinner(logger, stage_name, runner):
    """在执行长耗时测试套件时显示活动状态。"""
    logger.info("=== %s ===", stage_name)
    spinner = StageSpinner(stage_name)
    started_at = time.monotonic()
    spinner.start()
    try:
        return runner()
    finally:
        spinner.stop()
        elapsed_seconds = time.monotonic() - started_at
        logger.info("=== %s 完成，用时 %.1fs ===", stage_name, elapsed_seconds)


def run_all_tests(full=False, fast=False, smoke=False):
    """运行所有测试"""
    # 创建 temp 目录
    temp_dir = create_temp_directory()
    log_file = os.path.join(temp_dir, "test_log.txt")
    logger = setup_logging(log_file)

    if smoke:
        mode = "smoke"
        coverage = "跨平台最小冒烟回归（发现/鉴权/WS 传输矩阵/request 主链路）"
    elif full:
        mode = "full"
        coverage = "M1~M4 严格覆盖（含 lease(30s)/并发去重/scope 扩展与 WS failed 事件场景）"
    elif fast:
        mode = "fast"
        coverage = "M1~M4 快速回归（跳过 lease(30s) 与并发压力等长耗时场景）"
    else:
        mode = "default"
        coverage = "M1~M4 默认回归（核心链路 + Spec MUST，长耗时场景归入 full）"
    logger.info("开始 DevHub M1~M4 功能测试，模式: %s", mode)

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
        ])

        logger.info("=== 运行 WebSocket 事件 smoke 测试 ===")
        ws_events_tests = TestWsEvents()
        report.results.extend([
            ws_events_tests.test_m4_ws_001_first_message_must_authenticate(),
            ws_events_tests.test_m4_ws_005_subscribe_unsubscribe_should_work_after_auth(),
        ])

        logger.info("=== 运行 WebSocket 传输矩阵 smoke 测试 ===")
        ws_transport_matrix_tests = TestWsTransportMatrix()
        report.results.extend([
            ws_transport_matrix_tests.test_m4_ws_matrix_001_ping_should_work_after_auth(),
            ws_transport_matrix_tests.test_m4_ws_matrix_006_http_only_methods_should_be_rejected_over_ws(),
            ws_transport_matrix_tests.test_m4_ws_matrix_007_ws_only_methods_should_be_rejected_over_http(),
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
                lambda: invocation_poll_respond_tests.run_all_tests(full=full)))

        invoke_poll_respond_edges_tests = TestInvokePollRespondEdges()
        report.results.extend(
            run_suite_with_spinner(
                logger,
                "运行 Invocation Poll/Respond 规范边界测试",
                lambda: invoke_poll_respond_edges_tests.run_all_tests(full=full)))

        logger.info("=== 运行 Launch + Invocation 测试 ===")
        launch_invocation_tests = TestLaunchInvocation()
        report.results.extend(launch_invocation_tests.run_all_tests(full=full))

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
        logger.warning("⚠️  测试中有失败的用例")
        return 1

    logger.info("✅ 所有测试通过")
    return 0


def print_usage():
    """打印使用说明"""
    print("Usage: python test_runner.py [options]")
    print()
    print("Options:")
    print("  -h, --help    Show this help message and exit")
    print("  --no-header   Don't print test header")
    print("  --smoke       Run minimal cross-platform smoke suite")
    print("  --fast        Run fast suite (skip timeout/offline long tests)")
    print("  --full        Run full suite including timeout/offline long tests")


def main():
    """主函数"""
    import argparse

    parser = argparse.ArgumentParser(description="DevHub M1~M4 功能测试运行器")
    parser.add_argument("--no-header", action="store_true", help="Don't print test header")

    mode_group = parser.add_mutually_exclusive_group()
    mode_group.add_argument("--smoke", action="store_true", help="Run minimal cross-platform smoke suite")
    mode_group.add_argument("--fast", action="store_true", help="Run fast suite (skip timeout/offline long tests)")
    mode_group.add_argument("--full", action="store_true", help="Run full suite including timeout/offline long tests")

    args = parser.parse_args()

    if not args.no_header:
        print("=" * 60)
        print("DevHub M1~M4 功能测试")
        print("=" * 60)
        print()

    # 运行测试
    return run_all_tests(full=args.full, fast=args.fast, smoke=args.smoke)


if __name__ == "__main__":
    exit_code = main()
    sys.exit(exit_code)
