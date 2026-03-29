#!/usr/bin/env python3
"""
DevHub M1 启动与发现测试
"""

import os
import json
import time
import tempfile
import subprocess
import threading
import unittest


from tests.blackbox.test_base import (
    DiscoveryService,
    RpcClient,
    TestResult,
    describe_test_hub_command,
    paths_refer_to_same_location,
    start_isolated_hub_process,
    temporary_env_var,
    validate_current_user_only_file_access,
)


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

            for display_name, file_path in (("Token 文件", token_path), ("hub.json 文件", hub_json_path)):
                is_secure, detail = validate_current_user_only_file_access(file_path)
                if not is_secure:
                    result.mark_failure(f"❌ {display_name} 权限不正确：{detail}")
                    return result
                result.add_detail(f"✅ {display_name} 权限正确（仅当前用户可访问）：{detail}")

            result.mark_success()

        except Exception as e:
            result.mark_failure(f"❌ 无法检查文件权限: {e}")

        return result

    def test_hub_json_atomic_write(self):
        """测试 Spec 要求的 hub.json 原子更新行为。"""
        result = TestResult("测试 hub.json 原子更新行为")

        try:
            required_fields = ["protocolVersion", "pid", "httpBaseUrl", "wsUrl", "tokenFile", "startedAtUtc", "runtimeTuning"]
            update_rounds = 6
            read_errors = []
            update_errors = []
            read_count = 0
            has_observed_snapshot = False
            rewrite_observations = []
            restart_in_progress = threading.Event()
            stop_event = threading.Event()
            result.add_detail(f"隔离 Hub 启动命令: {describe_test_hub_command()}")

            with tempfile.TemporaryDirectory(prefix="devhub-test-data-atomic-") as data_dir:
                runtime_dir = os.path.join(data_dir, "runtime")
                definitions_dir = os.path.join(data_dir, "apps", "definitions")
                hub_json_path = os.path.join(runtime_dir, "hub.json")
                host_log_path = os.path.join(data_dir, "host-atomic.log")
                os.makedirs(definitions_dir, exist_ok=True)

                def validate_hub_runtime_snapshot(hub_info):
                    missing_fields = [field for field in required_fields if field not in hub_info]
                    if missing_fields:
                        raise ValueError(f"hub.json missing fields: {missing_fields}")

                    if hub_info.get("protocolVersion") != 1:
                        raise ValueError(f"protocolVersion is not 1: {hub_info.get('protocolVersion')}")

                    runtime_tuning = hub_info.get("runtimeTuning")
                    if not isinstance(runtime_tuning, dict):
                        raise ValueError("runtimeTuning is not an object")

                    for field in ("leaseSeconds", "onlineThresholdSeconds", "launchDedupeWindowSeconds"):
                        value = runtime_tuning.get(field)
                        if not isinstance(value, int) or value < 1:
                            raise ValueError(f"runtimeTuning.{field} is invalid: {runtime_tuning}")

                def read_hub_runtime_continuously():
                    nonlocal read_count, has_observed_snapshot

                    while not stop_event.is_set():
                        try:
                            with open(hub_json_path, "r", encoding="utf-8") as f:
                                hub_info = json.load(f)
                            validate_hub_runtime_snapshot(hub_info)
                            has_observed_snapshot = True
                        except FileNotFoundError:
                            if has_observed_snapshot and not restart_in_progress.is_set():
                                read_errors.append("reader saw hub.json disappear after a valid snapshot")
                                stop_event.set()
                                return
                            time.sleep(0.002)
                            continue
                        except PermissionError:
                            if has_observed_snapshot and not restart_in_progress.is_set():
                                read_errors.append("reader lost access to hub.json after a valid snapshot")
                                stop_event.set()
                                return
                            time.sleep(0.002)
                            continue
                        except json.JSONDecodeError as e:
                            read_errors.append(f"reader saw invalid JSON: {e}")
                            stop_event.set()
                            return
                        except Exception as e:
                            read_errors.append(f"reader failed to consume hub.json: {e}")
                            stop_event.set()
                            return

                        read_count += 1
                        time.sleep(0.001)

                def restart_hub_and_collect_snapshots():
                    last_snapshot = None

                    for i in range(update_rounds):
                        if stop_event.is_set():
                            return

                        process = None
                        restart_in_progress.set()
                        try:
                            with open(host_log_path, "a+", encoding="utf-8", errors="backslashreplace") as log_file:
                                process = start_isolated_hub_process(data_dir, log_file)

                                if not self._wait_for_hub_runtime_files(process, runtime_dir, timeout_seconds=45):
                                    process_output = self._read_open_log_tail(log_file)
                                    update_errors.append(
                                        f"restart {i + 1}: hub.json was not generated or host exited early, output={process_output}")
                                    stop_event.set()
                                    return

                                hub_info = None
                                snapshot_key = None
                                wait_deadline = time.time() + 20
                                while time.time() < wait_deadline:
                                    if process.poll() is not None:
                                        process_output = self._read_open_log_tail(log_file)
                                        update_errors.append(
                                            f"restart {i + 1}: host exited before writing a fresh snapshot, output={process_output}")
                                        stop_event.set()
                                        return

                                    try:
                                        with open(hub_json_path, "r", encoding="utf-8") as f:
                                            candidate = json.load(f)
                                        validate_hub_runtime_snapshot(candidate)
                                        candidate_key = (
                                            candidate["pid"],
                                            candidate["startedAtUtc"],
                                            candidate["httpBaseUrl"])
                                    except (FileNotFoundError, PermissionError, json.JSONDecodeError, ValueError):
                                        time.sleep(0.05)
                                        continue
                                    except Exception as e:
                                        update_errors.append(f"restart {i + 1}: failed to read hub.json snapshot: {e}")
                                        stop_event.set()
                                        return

                                    if last_snapshot is None or candidate_key != last_snapshot:
                                        hub_info = candidate
                                        snapshot_key = candidate_key
                                        break

                                    time.sleep(0.05)

                                if hub_info is None or snapshot_key is None:
                                    process_output = self._read_open_log_tail(log_file)
                                    update_errors.append(
                                        f"restart {i + 1}: timed out waiting for a fresh hub.json snapshot, output={process_output}")
                                    stop_event.set()
                                    return

                                token_path = hub_info.get("tokenFile")
                                if not isinstance(token_path, str) or not os.path.isabs(token_path):
                                    update_errors.append(f"restart {i + 1}: tokenFile is not absolute: {token_path}")
                                    stop_event.set()
                                    return
                                if not os.path.exists(token_path):
                                    update_errors.append(f"restart {i + 1}: token file does not exist: {token_path}")
                                    stop_event.set()
                                    return

                                with open(token_path, "r", encoding="utf-8") as token_file:
                                    token = token_file.read().strip()
                                if not token:
                                    update_errors.append(f"restart {i + 1}: token file is empty")
                                    stop_event.set()
                                    return

                                ok, error = self._wait_for_hub_ping(
                                    process,
                                    hub_info["httpBaseUrl"],
                                    token,
                                    timeout_seconds=20)
                                if not ok:
                                    process_output = self._read_open_log_tail(log_file)
                                    update_errors.append(
                                        f"restart {i + 1}: hub.ping is unavailable: {error}; output={process_output}")
                                    stop_event.set()
                                    return

                                rewrite_observations.append((hub_info["pid"], hub_info["startedAtUtc"]))
                                last_snapshot = snapshot_key
                                restart_in_progress.clear()
                                time.sleep(0.1)
                        except Exception as e:
                            update_errors.append(f"restart {i + 1}: unexpected exception: {e}")
                            stop_event.set()
                            return
                        finally:
                            self._stop_process(process)
                            restart_in_progress.set()
                            time.sleep(0.1)

                reader_thread = threading.Thread(target=read_hub_runtime_continuously, daemon=True)
                writer_thread = threading.Thread(target=restart_hub_and_collect_snapshots, daemon=True)
                reader_thread.start()
                writer_thread.start()

                writer_thread.join(timeout=240)
                stop_event.set()
                reader_thread.join(timeout=10)

                if writer_thread.is_alive():
                    result.mark_failure("Concurrent restart thread timed out")
                    return result

            if update_errors:
                result.mark_failure(f"Failed to trigger hub.json updates: {update_errors[0]}")
                return result
            if read_errors:
                result.mark_failure(f"Observed non-atomic hub.json snapshot: {read_errors[0]}")
                return result
            if read_count == 0:
                result.mark_failure("Reader did not observe any hub.json snapshots")
                return result

            unique_snapshots = set(rewrite_observations)
            if len(unique_snapshots) < 2:
                result.mark_failure("Did not observe multiple distinct hub.json snapshots")
                return result

            result.add_detail(
                f"Triggered {update_rounds} hub restarts and consumed {read_count} snapshots without partial reads")
            result.mark_success()

        except Exception as e:
            result.mark_failure(f"Unable to validate atomic write behavior: {e}")

        return result

    def test_custom_data_dir_real_hub_files(self):
        """测试 DEVHUB_DATA_DIR 下 Hub 实际生成发现文件并可访问"""
        result = TestResult("测试 DEVHUB_DATA_DIR Hub 实际行为")
        process = None

        try:
            with tempfile.TemporaryDirectory(prefix="devhub-test-data-real-") as data_dir:
                runtime_dir = os.path.join(data_dir, "runtime")
                definitions_dir = os.path.join(data_dir, "apps", "definitions")
                os.makedirs(definitions_dir, exist_ok=True)
                result.add_detail(f"隔离 Hub 启动命令: {describe_test_hub_command()}")

                try:
                    with temporary_env_var("DEVHUB_DATA_DIR", data_dir):
                        host_log_path = os.path.join(data_dir, "host-data-dir.log")
                        with open(host_log_path, "w+", encoding="utf-8", errors="backslashreplace") as log_file:
                            process = start_isolated_hub_process(data_dir, log_file)

                            if not self._wait_for_hub_runtime_files(process, runtime_dir, timeout_seconds=45):
                                process_output = self._read_open_log_tail(log_file)
                                if process.poll() is None:
                                    result.mark_failure(
                                        f"❌ 等待超时：Hub 未在自定义数据根目录生成 hub.json。日志片段: {process_output}")
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
                            if not paths_refer_to_same_location(token_file, runtime_token):
                                result.mark_failure(f"❌ tokenFile 路径不在自定义数据根目录: {token_file}")
                                return result

                            base_url, token = DiscoveryService.get_hub_info()
                            ok, error = self._wait_for_hub_ping(process, base_url, token, timeout_seconds=20)
                            if not ok:
                                process_output = self._read_open_log_tail(log_file)
                                result.mark_failure(
                                    f"❌ Hub 在自定义数据根目录下不可访问: {error}; output={process_output}")
                                return result

                            result.add_detail(f"✅ 通过自定义数据根目录发现并访问 Hub 成功: {base_url}")
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

        # 测试 DEVHUB_DATA_DIR 环境变量支持（真实 Hub 进程路径）
        results.append(self.test_custom_data_dir_real_hub_files())

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
