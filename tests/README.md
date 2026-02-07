# DevHub Python 集成测试说明（M1 双模式）

## 前置要求

- .NET SDK 10.0+
- Python 3.9+
- Python 依赖：`pip install requests`

## 运行方式

### 1) 启动 DevHub Host

```bash
dotnet run --project src/DevHub.Host/DevHub.Host.csproj -c Release
```

### 2) 运行 Python 集成测试

#### Quick 模式（默认，快速反馈）

```bash
python3 tests/test_runner.py
```

#### Full 模式（严格覆盖，含耗时场景）

```bash
python3 tests/test_runner.py --full
```

## 报告输出

- 文本报告：`temp/test_results.txt`
- JSON 报告：`temp/test_results.json`
- 日志文件：`temp/test_log.txt`

报告中会标注：

- `mode`: `quick` / `full`
- `coverage`: 覆盖级别描述

## 覆盖矩阵（M1 条目 -> 测试方法）

### A. 启动与发现（M1 必测）

- 发现文件存在与字段约束：`TestLaunchDiscovery.test_discovery_files_exist`
- HTTP 可达：`TestLaunchDiscovery.test_http_server_reachable`
- token/hub.json 权限：`TestLaunchDiscovery.test_token_file_permissions`
- hub.json 原子写完整性：`TestLaunchDiscovery.test_hub_json_atomic_write`
- `DEVHUB_RUNTIME_DIR` 覆盖：`TestLaunchDiscovery.test_custom_runtime_dir`

### B. 鉴权、协议、HTTP 传输（Spec MUST）

- 有效凭证调用：`TestAuthProtocol.test_ping_with_valid_credentials`
- 缺失/无效 token：`test_ping_without_token` / `test_ping_with_invalid_token`
- 协议版本错误/缺失：`test_ping_with_invalid_protocol_version` / `test_ping_without_protocol_header`
- 缺失客户端头：`test_ping_without_client_id` / `test_ping_without_client_session_id`
- 非 Bearer 授权头：`test_authorization_must_use_bearer_scheme`
- sessionId UUID 约束：`test_client_session_id_must_be_uuid`
- batch 禁止：`test_batch_request_rejected`
- Content-Type 约束：`test_content_type_must_be_application_json`
- 错误场景 HTTP 200：`test_http_status_code_always_200`

### C. AppDefinition（M1 必测）

- list/get 正常路径：`test_list_definitions` / `test_get_definition`
- 不存在定义：`test_get_nonexistent_definition`
- 无效定义忽略：`test_invalid_app_definition`
- appId 格式校验：`test_app_definition_appid_format_validation`
- scopePolicy 校验：`test_app_definition_scopepolicy_validation`
- 文件名与 appId 一致性：`test_definition_filename_must_match_appid`

### D. AppInstance（M1 必测）

- register + list：`test_register_and_list_instances`
- 未定义 appId 仍允许注册：`test_register_unknown_appid_is_allowed`
- upsert 刷新 lastSeen：`test_register_upsert_refresh_last_seen`
- heartbeat 更新 lastSeen：`test_heartbeat_updates_last_seen`
- heartbeat 不存在实例：`test_heartbeat_nonexistent_instance`
- unregister 与幂等：`test_unregister_instance` / `test_unregister_nonexistent_instance`
- list 过滤语义：`test_list_instances_with_params`
- 默认全局 scope 过滤：`test_list_instances_default_global_scope`
- scope 非法值校验：`test_register_instance_with_global_scope` / `test_register_instance_empty_scope`
- scope 严格匹配不回退：`test_list_instances_scope_strict_match`
- invoke 字段结构：`test_register_instance_invoke_field_validation`
- 30s 离线判定（full）：`test_instance_offline_after_30s_no_heartbeat`
- includeOffline（full）：`test_list_instances_include_offline`

### E. 参数校验与错误处理

- hub.* 数组参数拒绝：`TestInvalidParams.test_params_as_array`
- getDefinition/register/heartbeat 各类 invalid_params：`TestInvalidParams.*`
- parse_error：`TestInternalErrors.test_parse_error_invalid_json`
- invalid_request：`TestInternalErrors.test_invalid_request_envelope`
- 恢复性与鲁棒性：`test_internal_error_handling` / `test_server_recovery_after_error` / `test_concurrent_invalid_requests`
- 压力/大负载（full 扩展）：`test_server_resource_exhaustion_simulation` / `test_large_payload`

## Quick 与 Full 的差异

- Quick（默认）：覆盖 M1 核心链路 + Spec MUST 关键项，不执行耗时压力场景。
- Full：在 Quick 基础上增加离线判定（30s）与压力/大负载场景，用于严格回归与验收。

## 备注

- 本套测试遵循 `docs/Spec.md` 为最高优先级规范。
- 对于 `internal_errors` 的压力类用例，仅在 Full 模式执行，不作为 Quick 阻塞项。
