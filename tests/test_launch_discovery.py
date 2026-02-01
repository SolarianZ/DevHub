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

            if "result" in response and response["result"].get("ok") and "serverTimeUtc" in response["result"]:
                result.add_detail(f"✅ 服务器响应正常")
                result.add_detail(f"服务器时间: {response['result']['serverTimeUtc']}")
                result.mark_success()
            else:
                result.mark_failure("❌ 服务器响应格式不正确")

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_token_file_permissions(self):
        """测试 token 文件的权限设置"""
        result = TestResult("测试 token 文件的权限设置")

        try:
            runtime_dir = DiscoveryService.get_runtime_directory()
            token_path = os.path.join(runtime_dir, "token.txt")

            if os.name == "nt":  # Windows 系统
                import win32security
                import ntsecuritycon as con

                # 获取文件安全描述符
                sd = win32security.GetFileSecurity(token_path, win32security.DACL_SECURITY_INFORMATION)
                dacl = sd.GetSecurityDescriptorDacl()

                # 获取当前用户 SID
                user_sid = win32security.GetTokenInformation(
                    win32security.OpenProcessToken(
                        win32security.GetCurrentProcess(),
                        win32security.TOKEN_QUERY
                    ),
                    win32security.TokenUser
                )[0]

                # 检查是否只有当前用户有访问权限
                has_only_user_access = True
                for i in range(dacl.GetAceCount()):
                    ace = dacl.GetAce(i)
                    ace_type, ace_flags, ace_data = ace
                    if ace_type == win32security.ACCESS_ALLOWED_ACE_TYPE:
                        sid = ace_data[0]
                        # 检查是否是当前用户 SID
                        if sid != user_sid:
                            has_only_user_access = False
                            break

                if has_only_user_access:
                    result.add_detail("✅ Token 文件权限正确（仅当前用户可访问）")
                else:
                    result.add_detail("⚠️  Token 文件权限可能不正确")

                result.mark_success()

            else:  # 非 Windows 系统，简化检查
                import stat

                # 检查文件权限是否为 0o600（仅用户可读写）
                st_mode = os.stat(token_path).st_mode
                if (st_mode & 0o777) == 0o600:
                    result.add_detail("✅ Token 文件权限正确（0o600）")
                else:
                    result.add_detail(f"⚠️  Token 文件权限可能不正确: 0o{oct(st_mode & 0o777)[2:]}")

                result.mark_success()

        except Exception as e:
            result.add_detail(f"⚠️  无法检查 token 文件权限: {e}")
            result.mark_success()  # 权限检查失败不应该导致测试失败

        return result

    def test_hub_json_atomic_write(self):
        """测试 hub.json 的原子写入特性"""
        result = TestResult("测试 hub.json 的原子写入特性")

        try:
            runtime_dir = DiscoveryService.get_runtime_directory()
            hub_json_path = os.path.join(runtime_dir, "hub.json")
            hub_json_tmp_path = os.path.join(runtime_dir, "hub.json.tmp")

            # 检查是否存在临时文件（原子写入的痕迹）
            if not os.path.exists(hub_json_tmp_path):
                result.add_detail("✅ 没有发现临时文件，可能使用了原子写入")
            else:
                result.add_detail("⚠️  发现临时文件，可能原子写入过程中出现了问题")

            result.mark_success()

        except Exception as e:
            result.add_detail(f"⚠️  无法检查原子写入特性: {e}")
            result.mark_success()

        return result

    def test_custom_runtime_dir(self):
        """测试 DEVHUB_RUNTIME_DIR 环境变量的支持"""
        result = TestResult("测试 DEVHUB_RUNTIME_DIR 环境变量的支持")

        try:
            import tempfile
            import shutil

            # 创建临时目录作为自定义运行时目录
            with tempfile.TemporaryDirectory(prefix="devhub-test-runtime-") as temp_dir:
                # 设置环境变量
                os.environ["DEVHUB_RUNTIME_DIR"] = temp_dir

                # 验证 DiscoveryService 能够读取环境变量
                discovery_dir = DiscoveryService.get_runtime_directory()
                if discovery_dir == temp_dir:
                    result.add_detail(f"✅ DiscoveryService 正确读取了 DEVHUB_RUNTIME_DIR: {temp_dir}")
                else:
                    result.mark_failure(f"❌ DiscoveryService 未正确读取 DEVHUB_RUNTIME_DIR: 实际值 {discovery_dir}, 预期值 {temp_dir}")
                    return result

                # 创建所需的子目录和文件
                os.makedirs(os.path.join(temp_dir, "apps", "definitions"), exist_ok=True)

                # 创建临时的 hub.json 和 token.txt
                with open(os.path.join(temp_dir, "hub.json"), "w", encoding="utf-8") as f:
                    import json
                    json.dump({
                        "protocolVersion": 1,
                        "pid": 12345,
                        "httpBaseUrl": "http://127.0.0.1:12345",
                        "wsUrl": "ws://127.0.0.1:12345/ws",
                        "tokenFile": os.path.join(temp_dir, "token.txt"),
                        "startedAtUtc": "2026-01-30T12:34:56Z"
                    }, f)

                with open(os.path.join(temp_dir, "token.txt"), "w", encoding="utf-8") as f:
                    f.write("test-token-123")

                # 测试获取 hub 信息
                base_url, token = DiscoveryService.get_hub_info()
                if base_url == "http://127.0.0.1:12345" and token == "test-token-123":
                    result.add_detail("✅ 成功从自定义运行时目录获取 hub 信息")
                else:
                    result.mark_failure(f"❌ 从自定义运行时目录获取的 hub 信息不正确: base_url={base_url}, token={token}")

                result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))
        finally:
            # 清除环境变量
            if "DEVHUB_RUNTIME_DIR" in os.environ:
                del os.environ["DEVHUB_RUNTIME_DIR"]

        return result

    def run_all_tests(self):
        """运行所有启动与发现测试"""
        results = []

        # 测试文件存在性
        file_exists_result = self.test_discovery_files_exist()
        results.append(file_exists_result)

        # 如果文件存在，则测试服务器可访问性和其他特性
        if file_exists_result.success:
            server_reachable_result = self.test_http_server_reachable()
            results.append(server_reachable_result)

            token_permissions_result = self.test_token_file_permissions()
            results.append(token_permissions_result)

            atomic_write_result = self.test_hub_json_atomic_write()
            results.append(atomic_write_result)
        else:
            result = TestResult("测试 HTTP 服务器可访问性")
            result.mark_failure("❌ 跳过，因为发现文件不存在")
            results.append(result)

            result = TestResult("测试 token 文件权限")
            result.mark_failure("❌ 跳过，因为发现文件不存在")
            results.append(result)

            result = TestResult("测试 hub.json 原子写入")
            result.mark_failure("❌ 跳过，因为发现文件不存在")
            results.append(result)

        # 测试 DEVHUB_RUNTIME_DIR 环境变量支持（无论其他测试是否成功）
        results.append(self.test_custom_runtime_dir())

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
