#!/usr/bin/env python3
"""
DevHub M1 测试运行器
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


def run_all_tests():
    """运行所有测试"""
    # 创建 temp 目录
    temp_dir = create_temp_directory()
    log_file = os.path.join(temp_dir, "test_log.txt")
    logger = setup_logging(log_file)

    logger.info("开始 DevHub M1 功能测试")

    # 创建测试报告
    report = TestReport()

    # 运行各个模块的测试
    logger.info("=== 运行启动与发现测试 ===")
    launch_discovery_tests = TestLaunchDiscovery()
    report.results.extend(launch_discovery_tests.run_all_tests())

    logger.info("=== 运行鉴权与协议版本测试 ===")
    auth_protocol_tests = TestAuthProtocol()
    report.results.extend(auth_protocol_tests.run_all_tests())

    logger.info("=== 运行 AppDefinition 测试 ===")
    app_definitions_tests = TestAppDefinitions()
    report.results.extend(app_definitions_tests.run_all_tests())

    logger.info("=== 运行 AppInstance 测试 ===")
    app_instances_tests = TestAppInstances()
    report.results.extend(app_instances_tests.run_all_tests())

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


def main():
    """主函数"""
    import argparse

    parser = argparse.ArgumentParser(description="DevHub M1 功能测试运行器")
    parser.add_argument("--no-header", action="store_true", help="Don't print test header")

    args = parser.parse_args()

    if not args.no_header:
        print("=" * 60)
        print("DevHub M1 功能测试")
        print("=" * 60)
        print()

    # 运行测试
    return run_all_tests()


if __name__ == "__main__":
    exit_code = main()
    sys.exit(exit_code)
