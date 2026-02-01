#!/usr/bin/env python3
"""
DevHub M1 启动与发现测试
"""

import os
import sys
import unittest

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, TestResult


class TestLaunchDiscovery(unittest.TestCase):
    """启动与发现测试类"""

    def test_discovery_files_exist(self):
        """测试 hub.json 和 token.txt 文件是否存在"""
        result = TestResult("测试发现文件是否存在")

        try:
            runtime_dir = DiscoveryService.get_runtime_directory()
            result.add_detail(f"运行时目录: {runtime_dir}")

            # 检查 hub.json 是否存在
            hub_json_path = os.path.join(runtime_dir, "hub.json")
            if os.path.exists(hub_json_path):
                result.add_detail("✅ hub.json 文件存在")
            else:
                result.mark_failure(f"❌ hub.json 文件不存在: {hub_json_path}")
                return result

            # 检查 token.txt 是否存在
            token_path = os.path.join(runtime_dir, "token.txt")
            if os.path.exists(token_path):
                result.add_detail("✅ token.txt 文件存在")
            else:
                result.mark_failure(f"❌ token.txt 文件不存在: {token_path}")
                return result

            # 验证 hub.json 格式
            with open(hub_json_path, "r", encoding="utf-8") as f:
                import json
                hub_info = json.load(f)

            required_fields = ["protocolVersion", "pid", "httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc"]
            for field in required_fields:
                if field in hub_info:
                    result.add_detail(f"✅ hub.json 包含 {field} 字段")
                else:
                    result.mark_failure(f"❌ hub.json 缺少 {field} 字段")
                    return result

            # 验证协议版本
            if hub_info.get("protocolVersion") == 1:
                result.add_detail("✅ 协议版本正确 (1)")
            else:
                result.mark_failure(f"❌ 协议版本不正确: {hub_info.get('protocolVersion')}")
                return result

            # 验证 httpBaseUrl 是否可访问
            result.add_detail(f"HTTP 地址: {hub_info['httpBaseUrl']}")

            # 检查 token 文件内容
            with open(token_path, "r", encoding="utf-8") as f:
                token = f.read().strip()

            if token:
                result.add_detail("✅ Token 文件包含有效内容")
            else:
                result.mark_failure("❌ Token 文件内容为空")
                return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_http_server_reachable(self):
        """测试 HTTP 服务器是否可访问"""
        result = TestResult("测试 HTTP 服务器可访问性")

        try:
            from tests.test_base import RpcClient

            # 从 hub.json 获取服务信息
            base_url, token = DiscoveryService.get_hub_info()
            result.add_detail(f"服务器地址: {base_url}")

            # 尝试建立连接
            client = RpcClient(base_url, token)
            response = client.call("hub.ping")

            if "result" in response and "ok" in response["result"] and response["result"]["ok"] == True and "serverTimeUtc" in response["result"]:
                result.add_detail(f"✅ 服务器响应正常")
                result.add_detail(f"服务器时间: {response['result']['serverTimeUtc']}")
                result.mark_success()
            else:
                result.mark_failure("❌ 服务器响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self):
        """运行所有启动与发现测试"""
        results = []

        # 测试文件存在性
        file_exists_result = self.test_discovery_files_exist()
        results.append(file_exists_result)

        # 如果文件存在，则测试服务器可访问性
        if file_exists_result.success:
            server_reachable_result = self.test_http_server_reachable()
            results.append(server_reachable_result)
        else:
            result = TestResult("测试 HTTP 服务器可访问性")
            result.mark_failure("❌ 跳过，因为发现文件不存在")
            results.append(result)

        return results


if __name__ == "__main__":
    # 运行测试
    test = TestLaunchDiscovery()
    results = test.run_all_tests()

    # 输出结果
    for result in results:
        status = "✅ 通过" if result.success else "❌ 失败"
        print(f"{status}: {result.test_name}")

        if result.details:
            for detail in result.details:
                print(f"  - {detail}")

        if result.error_message:
            print(f"  错误: {result.error_message}")

        print()
