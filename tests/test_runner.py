#!/usr/bin/env python3
"""
DevHub M2 测试运行器
"""

import os
import sys
import logging

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import TestReport, create_temp_directory
from tests.test_launch_discovery import TestLaunchDiscovery
from tests.test_auth_protocol import TestAuthProtocol
from tests.test_app_definitions import TestAppDefinitions
from tests.test_app_instances import TestAppInstances
from tests.test_invocation_notify import TestInvocationNotify
from tests.test_invocation_request import TestInvocationRequest
from tests.test_invocation_poll_respond import TestInvocationPollRespond
from tests.test_launch_invocation import TestLaunchInvocation
from tests.test_invalid_params import TestInvalidParams
from tests.test_internal_errors import TestInternalErrors


def setup_logging(log_file):
    """设置日志"""
    logging.basicConfig(
        level=logging.INFO,
        format='%(asctime)s - %(levelname)s - %(message)s',
        handlers=[
            logging.FileHandler(log_file),
            logging.StreamHandler(sys.stdout)
        ]
    )
    return logging.getLogger(__name__)


def run_all_tests(full=False, fast=False):
    """运行所有测试"""
    # 创建 temp 目录
    temp_dir = create_temp_directory()
    log_file = os.path.join(temp_dir, "test_log.txt")
    logger = setup_logging(log_file)

    if full:
        mode = "full"
        coverage = "M2 严格覆盖（含 lease(30s)/并发去重等扩展耗时场景）"
    elif fast:
        mode = "fast"
        coverage = "M2 快速回归（跳过 lease(30s) 与并发压力等长耗时场景）"
    else:
        mode = "default"
        coverage = "M2 默认回归（核心链路 + Spec MUST，离线判定等长耗时场景归入 full）"
    logger.info("开始 DevHub M2 功能测试，模式: %s", mode)

    # 创建测试报告
    report = TestReport(mode=mode, coverage=coverage)

    # 运行各个模块的测试
    logger.info("=== 运行启动与发现测试 ===")
    launch_discovery_tests = TestLaunchDiscovery()
    report.results.extend(launch_discovery_tests.run_all_tests(full=full))

    logger.info("=== 运行鉴权与协议版本测试 ===")
    auth_protocol_tests = TestAuthProtocol()
    report.results.extend(auth_protocol_tests.run_all_tests(full=full))

    logger.info("=== 运行 AppDefinition 测试 ===")
    app_definitions_tests = TestAppDefinitions()
    report.results.extend(app_definitions_tests.run_all_tests(full=full))

    logger.info("=== 运行 AppInstance 测试 ===")
    app_instances_tests = TestAppInstances()
    report.results.extend(app_instances_tests.run_all_tests(full=full, run_timeout_tests=full))

    logger.info("=== 运行 Invocation Notify 测试 ===")
    invocation_notify_tests = TestInvocationNotify()
    report.results.extend(invocation_notify_tests.run_all_tests(full=full))

    logger.info("=== 运行 Invocation Request 测试 ===")
    invocation_request_tests = TestInvocationRequest()
    report.results.extend(invocation_request_tests.run_all_tests(full=full))

    logger.info("=== 运行 Invocation Poll/Respond 测试 ===")
    invocation_poll_respond_tests = TestInvocationPollRespond()
    report.results.extend(invocation_poll_respond_tests.run_all_tests(full=full))

    logger.info("=== 运行 Launch + Invocation 测试 ===")
    launch_invocation_tests = TestLaunchInvocation()
    report.results.extend(launch_invocation_tests.run_all_tests(full=full))

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
    print("  --fast        Run fast suite (skip timeout/offline long tests)")
    print("  --full        Run full suite including timeout/offline long tests")


def main():
    """主函数"""
    import argparse

    parser = argparse.ArgumentParser(description="DevHub M2 功能测试运行器")
    parser.add_argument("--no-header", action="store_true", help="Don't print test header")

    mode_group = parser.add_mutually_exclusive_group()
    mode_group.add_argument("--fast", action="store_true", help="Run fast suite (skip timeout/offline long tests)")
    mode_group.add_argument("--full", action="store_true", help="Run full suite including timeout/offline long tests")

    args = parser.parse_args()

    if not args.no_header:
        print("=" * 60)
        print("DevHub M2 功能测试")
        print("=" * 60)
        print()

    # 运行测试
    return run_all_tests(full=args.full, fast=args.fast)


if __name__ == "__main__":
    exit_code = main()
    sys.exit(exit_code)
