#!/usr/bin/env python3
"""
DevHub M1 启动与发现测试
"""

import os
import sys
import json
import time
import uuid
import tempfile
import subprocess
import unittest

# 添加项目根目录到 Python 模块搜索路径
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from tests.test_base import DiscoveryService, RpcClient, TestResult, temporary_env_var


class TestLaunchDiscovery(unittest.TestCase):
    """启动与发现测试类"""

    @staticmethod
    def _read_open_log_tail(log_file, max_chars=4000):
        """读取已打开日志句柄的尾部内容，避免 Windows 下文件占用冲突。"""
        if log_file is None:
            return ""

        try:
            log_file.flush()
            log_file.seek(0)
            output = log_file.read()
        except Exception:
            return ""

        if not output:
            return ""
        output = output.strip()
        if len(output) > max_chars:
            output = output[-max_chars:]
        return output

    @staticmethod
    def _stop_process(process):
        """安全停止子进程。"""
        if process is None or process.poll() is not None:
            return

        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)

    @staticmethod
    def _wait_for_hub_runtime_files(process, runtime_dir, timeout_seconds):
        """等待 Hub 在目标运行时目录写出发现文件。"""
        hub_json_path = os.path.join(runtime_dir, "hub.json")
        deadline = time.time() + timeout_seconds
        while time.time() < deadline:
            if process.poll() is not None:
                return False
            if os.path.exists(hub_json_path):
                return True
            time.sleep(0.2)
        return False

    @staticmethod
    def _wait_for_hub_ping(process, base_url, token, timeout_seconds):
        """等待 Hub 对 hub.ping 可达。"""
        deadline = time.time() + timeout_seconds
        last_error = "unknown"
        client = RpcClient(base_url, token)
        while time.time() < deadline:
            if process.poll() is not None:
                return False, "Hub 进程已退出"

            try:
                response = client.call("hub.ping")
                if "result" in response and response["result"].get("ok") is True:
                    return True, None
                last_error = f"响应异常: {response}"
            except Exception as e:
                last_error = str(e)

            time.sleep(0.3)

        return False, last_error

    def test_discovery_files_exist(self):
        """测试 hub.json 与 tokenFile 发现链路是否符合 Spec"""
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

            # 验证 hub.json 格式
            with open(hub_json_path, "r", encoding="utf-8") as f:
                hub_info = json.load(f)

            required_fields = ["protocolVersion", "pid", "httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc", "runtimeTuning"]
            for field in required_fields:
                if field in hub_info:
                    result.add_detail(f"✅ hub.json 包含 {field} 字段")
                else:
                    result.mark_failure(f"❌ hub.json 缺少 {field} 字段")
                    return result

            runtime_tuning = hub_info.get("runtimeTuning")
            if not isinstance(runtime_tuning, dict):
                result.mark_failure(f"❌ runtimeTuning 必须是对象: {runtime_tuning}")
                return result

            runtime_tuning_fields = [
                "leaseSeconds",
                "onlineThresholdSeconds",
                "launchDedupeWindowSeconds",
            ]
            for field in runtime_tuning_fields:
                value = runtime_tuning.get(field)
                if not isinstance(value, int) or value < 1:
                    result.mark_failure(f"❌ runtimeTuning.{field} 必须是 >=1 的整数: {runtime_tuning}")
                    return result

            # 验证协议版本
            if hub_info.get("protocolVersion") == 1:
                result.add_detail("✅ 协议版本正确 (1)")
            else:
                result.mark_failure(f"❌ 协议版本不正确: {hub_info.get('protocolVersion')}")
                return result

            # 验证 httpBaseUrl 规范
            http_base_url = hub_info["httpBaseUrl"]
            result.add_detail(f"HTTP 地址: {http_base_url}")
            # 检查是否指向 loopback 地址
            if not any(addr in http_base_url for addr in ["127.0.0.1", "localhost", "::1"]):
                result.mark_failure(f"❌ httpBaseUrl 必须指向 loopback 地址: {http_base_url}")
                return result
            # 检查是否有尾随斜杠
            if http_base_url.endswith("/"):
                result.mark_failure(f"❌ httpBaseUrl 不得有尾随斜杠: {http_base_url}")
                return result

            # 验证 wsUrl 规范
            ws_url = hub_info["wsUrl"]
            result.add_detail(f"WebSocket 地址: {ws_url}")
            # 检查是否为有效的 WebSocket URL
            if not ws_url.startswith("ws://") and not ws_url.startswith("wss://"):
                result.mark_failure(f"❌ wsUrl 必须是 ws:// 或 wss:// 开头的绝对 URL: {ws_url}")
                return result
            # 检查是否指向 loopback 地址
            if not any(addr in ws_url for addr in ["127.0.0.1", "localhost", "::1"]):
                result.mark_failure(f"❌ wsUrl 必须指向 loopback 地址: {ws_url}")
                return result
            # 检查是否有尾随斜杠
            if ws_url.endswith("/"):
                result.mark_failure(f"❌ wsUrl 不得有尾随斜杠: {ws_url}")
                return result

            # 验证 tokenFile 规范
            token_file = hub_info["tokenFile"]
            result.add_detail(f"Token 文件路径: {token_file}")
            # 检查是否为绝对路径
            if not os.path.isabs(token_file):
                result.mark_failure(f"❌ tokenFile 必须是绝对路径: {token_file}")
                return result
            # 检查文件是否存在
            if not os.path.exists(token_file):
                result.mark_failure(f"❌ tokenFile 指向的文件不存在: {token_file}")
                return result
            result.add_detail("✅ tokenFile 指向文件存在")

            # 检查 token 文件内容
            with open(token_file, "r", encoding="utf-8") as f:
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
            hub_json_path = os.path.join(runtime_dir, "hub.json")
            with open(hub_json_path, "r", encoding="utf-8") as f:
                hub_info = json.load(f)
            token_path = hub_info.get("tokenFile")
            if not token_path or not os.path.exists(token_path):
                result.mark_failure(f"❌ tokenFile 指向路径不存在: {token_path}")
                return result

            # 检查 token.txt 权限
            if os.name == "nt":  # Windows 系统
                try:
                    import win32api
                    import win32security
                    import ntsecuritycon as con

                    # 获取文件安全描述符
                    sd_token = win32security.GetFileSecurity(token_path, win32security.DACL_SECURITY_INFORMATION)
                    dacl_token = sd_token.GetSecurityDescriptorDacl()
                    sd_hub = win32security.GetFileSecurity(hub_json_path, win32security.DACL_SECURITY_INFORMATION)
                    dacl_hub = sd_hub.GetSecurityDescriptorDacl()

                    # 获取当前用户 SID
                    user_sid = win32security.GetTokenInformation(
                        win32security.OpenProcessToken(
                            win32api.GetCurrentProcess(),
                            win32security.TOKEN_QUERY
                        ),
                        win32security.TokenUser
                    )[0]

                    # 检查 token.txt 是否只有当前用户有访问权限
                    has_only_user_access_token = True
                    for i in range(dacl_token.GetAceCount()):
                        ace = dacl_token.GetAce(i)
                        ace_type, ace_flags, ace_data = ace
                        if ace_type == win32security.ACCESS_ALLOWED_ACE_TYPE:
                            sid = ace_data[0]
                            if sid != user_sid:
                                has_only_user_access_token = False
                                break

                    if has_only_user_access_token:
                        result.add_detail("✅ Token 文件权限正确（仅当前用户可访问）")
                    else:
                        result.mark_failure("❌ Token 文件权限不正确")
                        return result

                    # 检查 hub.json 是否只有当前用户有访问权限
                    has_only_user_access_hub = True
                    for i in range(dacl_hub.GetAceCount()):
                        ace = dacl_hub.GetAce(i)
                        ace_type, ace_flags, ace_data = ace
                        if ace_type == win32security.ACCESS_ALLOWED_ACE_TYPE:
                            sid = ace_data[0]
                            if sid != user_sid:
                                has_only_user_access_hub = False
                                break

                    if has_only_user_access_hub:
                        result.add_detail("✅ hub.json 文件权限正确（仅当前用户可访问）")
                    else:
                        result.mark_failure("❌ hub.json 文件权限不正确")
                        return result

                except ImportError:
                    result.mark_failure("❌ 无法检查 Windows 文件权限：缺少 pywin32 库，请运行 'pip install pywin32'")
                    return result
                except Exception as e:
                    result.mark_failure(f"❌ 检查 Windows 文件权限时出错：{e}")
                    return result

            else:  # 非 Windows 系统，简化检查
                try:
                    # Spec 要求“仅当前用户可访问”，因此只要求 group/other 位为 0。
                    st_mode_token = os.stat(token_path).st_mode
                    token_perm = st_mode_token & 0o777
                    if (token_perm & 0o077) != 0 or (token_perm & 0o400) == 0:
                        result.mark_failure(f"❌ Token 文件权限不正确: 0o{oct(st_mode_token & 0o777)[2:]}")
                        return result
                    result.add_detail(f"✅ Token 文件权限符合仅当前用户可访问约束: 0o{oct(token_perm)[2:]}")

                    # hub.json 同样要求仅当前用户可访问。
                    st_mode_hub = os.stat(hub_json_path).st_mode
                    hub_perm = st_mode_hub & 0o777
                    if (hub_perm & 0o077) != 0 or (hub_perm & 0o400) == 0:
                        result.mark_failure(f"❌ hub.json 文件权限不正确: 0o{oct(st_mode_hub & 0o777)[2:]}")
                        return result
                    result.add_detail(f"✅ hub.json 文件权限符合仅当前用户可访问约束: 0o{oct(hub_perm)[2:]}")

                except Exception as e:
                    result.mark_failure(f"❌ 检查文件权限时出错：{e}")
                    return result

            result.mark_success()

        except Exception as e:
            result.mark_failure(f"❌ 无法检查文件权限: {e}")

        return result

    def test_hub_json_atomic_write(self):
        """测试 hub.json 的原子写入特性"""
        result = TestResult("测试 hub.json 的原子写入特性")

        try:
            runtime_dir = DiscoveryService.get_runtime_directory()
            hub_json_path = os.path.join(runtime_dir, "hub.json")

            # 检查 hub.json 是否存在
            if not os.path.exists(hub_json_path):
                result.mark_failure("❌ hub.json 文件不存在")
                return result

            required_fields = ["protocolVersion", "pid", "httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc", "runtimeTuning"]
            # 不依赖实现细节（例如临时文件命名），只验证可观察到的原子性：
            # 在多次快速读取期间，hub.json 始终可解析且字段完整。
            for i in range(20):
                with open(hub_json_path, "r", encoding="utf-8") as f:
                    hub_info = json.load(f)

                for field in required_fields:
                    if field not in hub_info:
                        result.mark_failure(f"❌ 第{i + 1}次读取时 hub.json 缺少 {field} 字段")
                        return result
                time.sleep(0.01)

            result.add_detail("✅ 连续读取均可完整解析 hub.json，符合原子更新可观察行为")
            result.mark_success()

        except Exception as e:
            result.mark_failure(f"❌ 无法检查原子写入特性: {e}")

        return result

    def test_custom_runtime_dir_discovery_helper(self):
        """测试 Discovery helper 可读取 DEVHUB_RUNTIME_DIR（辅助逻辑层）"""
        result = TestResult("测试 DEVHUB_RUNTIME_DIR Discovery helper")

        try:
            with tempfile.TemporaryDirectory(prefix="devhub-test-runtime-helper-") as temp_dir:
                with temporary_env_var("DEVHUB_RUNTIME_DIR", temp_dir):
                    discovery_dir = DiscoveryService.get_runtime_directory()
                    if discovery_dir == temp_dir:
                        result.add_detail(f"✅ DiscoveryService 正确读取 DEVHUB_RUNTIME_DIR: {temp_dir}")
                    else:
                        result.mark_failure(f"❌ DiscoveryService 读取 DEVHUB_RUNTIME_DIR 错误: 实际值 {discovery_dir}, 预期值 {temp_dir}")
                        return result

                    with open(os.path.join(temp_dir, "hub.json"), "w", encoding="utf-8") as f:
                        json.dump({
                            "protocolVersion": 1,
                            "pid": 12345,
                            "httpBaseUrl": "http://127.0.0.1:12345",
                            "wsUrl": "ws://127.0.0.1:12345/ws",
                            "tokenFile": os.path.join(temp_dir, "token.txt"),
                            "startedAtUtc": "2026-01-30T12:34:56Z",
                            "runtimeTuning": {
                                "leaseSeconds": 30,
                                "onlineThresholdSeconds": 30,
                                "launchDedupeWindowSeconds": 30
                            }
                        }, f)

                    with open(os.path.join(temp_dir, "token.txt"), "w", encoding="utf-8") as f:
                        f.write("test-token-123")

                    base_url, token = DiscoveryService.get_hub_info()
                    if base_url != "http://127.0.0.1:12345" or token != "test-token-123":
                        result.mark_failure(f"❌ Discovery helper 返回值不正确: base_url={base_url}, token={token}")
                        return result

                    result.mark_success()

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def test_custom_runtime_dir_real_hub_files(self):
        """测试 DEVHUB_RUNTIME_DIR 下 Hub 实际生成发现文件并可访问"""
        result = TestResult("测试 DEVHUB_RUNTIME_DIR Hub 实际行为")
        process = None

        try:
            project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
            host_project = os.path.join(project_root, "src", "DevHub.Host", "DevHub.Host.csproj")
            if not os.path.exists(host_project):
                result.mark_failure(f"❌ 未找到 DevHub.Host.csproj: {host_project}")
                return result

            with tempfile.TemporaryDirectory(prefix="devhub-test-runtime-real-") as temp_root:
                runtime_dir = os.path.join(temp_root, "runtime")
                definitions_dir = os.path.join(temp_root, "apps", "definitions")
                os.makedirs(definitions_dir, exist_ok=True)

                single_instance_slot = f"test-runtime-{uuid.uuid4().hex[:8]}"
                try:
                    with temporary_env_var("DEVHUB_RUNTIME_DIR", runtime_dir):
                        with temporary_env_var("DEVHUB_APPDEFS_DIR", definitions_dir):
                            with temporary_env_var("DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS", single_instance_slot):
                                host_log_path = os.path.join(temp_root, "host-runtime-dir.log")
                                with open(host_log_path, "w+", encoding="utf-8", errors="backslashreplace") as log_file:
                                    process = subprocess.Popen(
                                        [
                                            "dotnet",
                                            "run",
                                            "--project",
                                            host_project,
                                            "-c",
                                            "Release",
                                            "--no-build",
                                            "--no-launch-profile"
                                        ],
                                        cwd=project_root,
                                        stdout=log_file,
                                        stderr=subprocess.STDOUT,
                                        text=True)

                                    if not self._wait_for_hub_runtime_files(process, runtime_dir, timeout_seconds=45):
                                        process_output = self._read_open_log_tail(log_file)
                                        if process.poll() is None:
                                            result.mark_failure(
                                                f"❌ 等待超时：Hub 未在自定义运行时目录生成 hub.json。日志片段: {process_output}")
                                        else:
                                            result.mark_failure(
                                                f"❌ Hub 提前退出，未生成 hub.json。exit={process.returncode}, output={process_output}")
                                        return result

                                    hub_json_path = os.path.join(runtime_dir, "hub.json")
                                    with open(hub_json_path, "r", encoding="utf-8") as f:
                                        hub_info = json.load(f)

                                    token_file = hub_info.get("tokenFile")
                                    if not token_file or not os.path.exists(token_file):
                                        result.mark_failure(f"❌ tokenFile 未正确生成: {token_file}")
                                        return result
                                    result.add_detail(f"✅ Hub 真实生成 hub.json 与 tokenFile: {hub_json_path}, {token_file}")

                                    runtime_token = os.path.join(runtime_dir, "token.txt")
                                    if os.path.abspath(token_file) != os.path.abspath(runtime_token):
                                        result.mark_failure(f"❌ tokenFile 路径不在自定义运行时目录: {token_file}")
                                        return result

                                    base_url, token = DiscoveryService.get_hub_info()
                                    ok, error = self._wait_for_hub_ping(process, base_url, token, timeout_seconds=20)
                                    if not ok:
                                        process_output = self._read_open_log_tail(log_file)
                                        result.mark_failure(
                                            f"❌ Hub 在自定义运行时目录下不可访问: {error}; output={process_output}")
                                        return result

                                    result.add_detail(f"✅ 通过自定义运行时目录发现并访问 Hub 成功: {base_url}")
                                    result.mark_success()
                finally:
                    # 在临时目录回收前停止子进程，避免 Windows 文件句柄占用导致删除失败。
                    self._stop_process(process)
                    process = None

        except Exception as e:
            result.mark_failure(str(e))

        return result

    def run_all_tests(self, full=False):
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
        results.append(self.test_custom_runtime_dir_discovery_helper())
        results.append(self.test_custom_runtime_dir_real_hub_files())

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
