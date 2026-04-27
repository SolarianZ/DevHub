use crate::models::{MonitorHubRuntime, MonitorRuntimeConnectionInfo, MonitorRuntimeTuning};
use anyhow::{Context, Result};
use chrono::DateTime;
use reqwest::header::{HeaderMap, HeaderValue, AUTHORIZATION, CONTENT_TYPE};
use reqwest::{Client, Url};
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use uuid::Uuid;

const DISCOVERY_CLIENT_ID: &str = "DevHubMonitor";
const METHOD_NOT_FOUND_ERROR_CODE: i64 = -32601;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeVerification {
    pub host_version: Option<String>,
}

pub fn discover_runtime(data_directory: &Path) -> Result<MonitorRuntimeConnectionInfo> {
    reject_legacy_layout(data_directory)?;

    let runtime_directory = data_directory.join("runtime");
    let hub_json_path = runtime_directory.join("hub.json");
    let hub_json = fs::read_to_string(&hub_json_path)
        .with_context(|| format!("未找到 hub.json：{}", hub_json_path.display()))?;

    let runtime: MonitorHubRuntime = serde_json::from_str(&hub_json)
        .with_context(|| format!("hub.json 解析失败：{}", hub_json_path.display()))?;

    validate_runtime(&runtime, &hub_json_path)?;

    let token_path = PathBuf::from(&runtime.token_file);
    let token = fs::read_to_string(&token_path)
        .with_context(|| format!("未找到 token 文件：{}", token_path.display()))?
        .trim()
        .to_string();

    if token.is_empty() {
        anyhow::bail!("token 文件为空：{}", token_path.display());
    }

    Ok(MonitorRuntimeConnectionInfo {
        runtime_directory: runtime_directory.display().to_string(),
        token,
        rpc_endpoint: format!("{}/rpc", runtime.http_base_url),
        websocket_endpoint: runtime.ws_url.clone(),
        runtime,
    })
}

pub async fn verify_runtime(
    connection: &MonitorRuntimeConnectionInfo,
) -> Result<RuntimeVerification> {
    let client = build_discovery_client()?;
    let headers = build_request_headers(connection)?;

    let ping_request_id = format!("monitor-ping-{}", Uuid::new_v4());
    let ping_payload = send_json_rpc_request(
        &client,
        connection,
        headers.clone(),
        &json!({
            "jsonrpc": "2.0",
            "id": ping_request_id,
            "method": "hub.ping",
            "params": {
                "echo": "monitor-discovery"
            }
        }),
        "hub.ping",
    )
    .await?;
    validate_ping_response(&ping_payload, &ping_request_id)?;

    let host_version = resolve_host_version(&client, connection, headers).await?;
    Ok(RuntimeVerification { host_version })
}

pub fn port_from_runtime(connection: &MonitorRuntimeConnectionInfo) -> Option<u16> {
    Url::parse(&connection.runtime.http_base_url)
        .ok()
        .and_then(|url| url.port_or_known_default())
}

fn reject_legacy_layout(data_directory: &Path) -> Result<()> {
    let legacy_hub_json = data_directory.join("hub.json");
    let legacy_token = data_directory.join("token.txt");

    if legacy_hub_json.exists() || legacy_token.exists() {
        anyhow::bail!(
            "DEVHUB_DATA_DIR 必须指向数据根目录，不支持直接传入 runtime 子目录或旧版布局：{}",
            data_directory.display()
        );
    }

    Ok(())
}

fn validate_runtime(runtime: &MonitorHubRuntime, source: &Path) -> Result<()> {
    if runtime.protocol_version == 0 {
        anyhow::bail!("hub.json.protocolVersion 非法：{}", source.display());
    }

    if runtime.pid == 0 {
        anyhow::bail!("hub.json.pid 非法：{}", source.display());
    }

    validate_runtime_url(
        &runtime.http_base_url,
        &["http", "https"],
        "hub.json.httpBaseUrl",
        source,
    )?;
    validate_runtime_url(&runtime.ws_url, &["ws", "wss"], "hub.json.wsUrl", source)?;

    if runtime.token_file.trim().is_empty() {
        anyhow::bail!("hub.json.tokenFile 非法：{}", source.display());
    }

    let token_path = Path::new(&runtime.token_file);
    if !token_path.is_absolute() {
        anyhow::bail!("hub.json.tokenFile 必须为绝对路径：{}", source.display());
    }

    validate_runtime_tuning(&runtime.runtime_tuning, source)?;
    DateTime::parse_from_rfc3339(&runtime.started_at_utc)
        .context(format!("hub.json.startedAtUtc 非法：{}", source.display()))?;

    Ok(())
}

fn validate_runtime_tuning(runtime_tuning: &MonitorRuntimeTuning, source: &Path) -> Result<()> {
    if runtime_tuning.lease_seconds == 0
        || runtime_tuning.online_threshold_seconds == 0
        || runtime_tuning.launch_dedupe_window_seconds == 0
    {
        anyhow::bail!("hub.json.runtimeTuning 非法：{}", source.display());
    }

    Ok(())
}

fn validate_runtime_url(
    value: &str,
    schemes: &[&str],
    field_name: &str,
    source: &Path,
) -> Result<()> {
    if value.trim().is_empty() || value.ends_with('/') {
        anyhow::bail!("{field_name} 非法：{}", source.display());
    }

    let url =
        Url::parse(value).with_context(|| format!("{field_name} 非法：{}", source.display()))?;
    if !schemes.iter().any(|scheme| url.scheme() == *scheme) {
        anyhow::bail!("{field_name} 协议非法：{}", source.display());
    }

    let host = url
        .host_str()
        .map(str::to_lowercase)
        .context(format!("{field_name} 缺少 host：{}", source.display()))?;
    if host != "localhost" && host != "127.0.0.1" && host != "::1" {
        anyhow::bail!("{field_name} 必须使用回环地址：{}", source.display());
    }

    Ok(())
}

fn build_discovery_client() -> Result<Client> {
    Client::builder()
        .timeout(std::time::Duration::from_millis(1500))
        .build()
        .context("无法创建 Hub 校验 HTTP 客户端。")
}

fn build_request_headers(connection: &MonitorRuntimeConnectionInfo) -> Result<HeaderMap> {
    let mut headers = HeaderMap::new();
    headers.insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    headers.insert(
        AUTHORIZATION,
        HeaderValue::from_str(&format!("Bearer {}", connection.token))
            .context("无法构造 Authorization 请求头。")?,
    );
    headers.insert("X-DevHub-Protocol", HeaderValue::from_static("1"));
    headers.insert(
        "X-DevHub-ClientId",
        HeaderValue::from_static(DISCOVERY_CLIENT_ID),
    );
    headers.insert(
        "X-DevHub-ClientSessionId",
        HeaderValue::from_str(&Uuid::new_v4().to_string())
            .context("无法构造 X-DevHub-ClientSessionId 请求头。")?,
    );
    Ok(headers)
}

async fn send_json_rpc_request(
    client: &Client,
    connection: &MonitorRuntimeConnectionInfo,
    headers: HeaderMap,
    body: &Value,
    method_name: &str,
) -> Result<Value> {
    let response = client
        .post(&connection.rpc_endpoint)
        .headers(headers)
        .json(body)
        .send()
        .await
        .with_context(|| format!("{method_name} 请求失败：{}", connection.rpc_endpoint))?;

    response
        .json::<Value>()
        .await
        .with_context(|| format!("{method_name} 响应 JSON 无法解析。"))
}

fn validate_ping_response(payload: &Value, request_id: &str) -> Result<()> {
    validate_json_rpc_envelope(payload, request_id, "hub.ping")?;

    if let Some(error) = payload.get("error") {
        anyhow::bail!("hub.ping 返回错误：{error}");
    }

    let result = payload
        .get("result")
        .and_then(Value::as_object)
        .context("hub.ping 响应缺少 result。")?;

    if result.get("ok").and_then(Value::as_bool) != Some(true) {
        anyhow::bail!("hub.ping 响应缺少 ok=true。");
    }

    let server_time = result
        .get("serverTimeUtc")
        .and_then(Value::as_str)
        .context("hub.ping 响应缺少 serverTimeUtc。")?;
    DateTime::parse_from_rfc3339(server_time)
        .context("hub.ping.serverTimeUtc 不是合法的 RFC 3339 时间。")?;

    Ok(())
}

async fn resolve_host_version(
    client: &Client,
    connection: &MonitorRuntimeConnectionInfo,
    headers: HeaderMap,
) -> Result<Option<String>> {
    let request_id = format!("monitor-get-version-{}", Uuid::new_v4());
    let payload = send_json_rpc_request(
        client,
        connection,
        headers,
        &json!({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "hub.getVersion",
            "params": {}
        }),
        "hub.getVersion",
    )
    .await?;

    parse_host_version_response(
        &payload,
        &request_id,
        connection.runtime.hub_version.as_deref(),
    )
}

fn parse_host_version_response(
    payload: &Value,
    request_id: &str,
    fallback_hub_version: Option<&str>,
) -> Result<Option<String>> {
    validate_json_rpc_envelope(payload, request_id, "hub.getVersion")?;

    if let Some(error) = payload.get("error") {
        let code = error
            .get("code")
            .and_then(Value::as_i64)
            .context("hub.getVersion 错误响应缺少 code。")?;
        if code == METHOD_NOT_FOUND_ERROR_CODE {
            return Ok(fallback_hub_version.map(str::to_string));
        }

        anyhow::bail!("hub.getVersion 返回错误：{error}");
    }

    let result = payload
        .get("result")
        .and_then(Value::as_object)
        .context("hub.getVersion 响应缺少 result。")?;
    if result.get("ok").and_then(Value::as_bool) != Some(true) {
        anyhow::bail!("hub.getVersion 响应缺少 ok=true。");
    }

    let version = result
        .get("version")
        .and_then(Value::as_str)
        .context("hub.getVersion 响应缺少 version。")?;
    if version.trim().is_empty() {
        anyhow::bail!("hub.getVersion 响应 version 为空。");
    }

    Ok(Some(version.to_string()))
}

fn validate_json_rpc_envelope(payload: &Value, request_id: &str, method_name: &str) -> Result<()> {
    if payload.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
        anyhow::bail!("{method_name} 响应缺少合法 jsonrpc 字段。");
    }

    if payload.get("id").and_then(Value::as_str) != Some(request_id) {
        anyhow::bail!("{method_name} 响应 id 不匹配。");
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{
        discover_runtime, parse_host_version_response, port_from_runtime, validate_ping_response,
    };
    use serde_json::json;
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn create_temp_directory(name: &str) -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("devhub-monitor-{name}-{suffix}"));
        fs::create_dir_all(&directory).expect("failed to create temp directory");
        directory
    }

    #[test]
    fn discover_runtime_reads_valid_runtime_layout() {
        let data_directory = create_temp_directory("runtime-valid");
        let runtime_directory = data_directory.join("runtime");
        fs::create_dir_all(&runtime_directory).expect("failed to create runtime directory");
        let token_path = runtime_directory.join("token.txt");
        fs::write(&token_path, "secret-token\n").expect("failed to write token");
        let hub_json = serde_json::to_string_pretty(&serde_json::json!({
            "protocolVersion": 1,
            "pid": 4321,
            "httpBaseUrl": "http://127.0.0.1:4123",
            "wsUrl": "ws://127.0.0.1:4123/ws",
            "tokenFile": token_path.display().to_string(),
            "startedAtUtc": "2026-04-12T00:00:00Z",
            "runtimeTuning": {
                "leaseSeconds": 30,
                "onlineThresholdSeconds": 15,
                "launchDedupeWindowSeconds": 5
            },
            "hubVersion": "0.7.0"
        }))
        .expect("failed to serialize hub.json");
        fs::write(runtime_directory.join("hub.json"), hub_json).expect("failed to write hub.json");

        let connection = discover_runtime(&data_directory).expect("expected runtime discovery");

        assert_eq!(connection.token, "secret-token");
        assert_eq!(connection.rpc_endpoint, "http://127.0.0.1:4123/rpc");
        assert_eq!(port_from_runtime(&connection), Some(4123));

        fs::remove_dir_all(data_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn discover_runtime_rejects_legacy_runtime_root_layout() {
        let data_directory = create_temp_directory("runtime-legacy");
        fs::write(data_directory.join("hub.json"), "{}").expect("failed to write legacy hub.json");

        let error = discover_runtime(&data_directory).expect_err("expected legacy layout error");
        assert!(error
            .to_string()
            .contains("DEVHUB_DATA_DIR 必须指向数据根目录"));

        fs::remove_dir_all(data_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn validate_ping_response_accepts_expected_payload() {
        let payload = json!({
            "jsonrpc": "2.0",
            "id": "request-1",
            "result": {
                "ok": true,
                "serverTimeUtc": "2026-04-12T00:00:00Z"
            }
        });

        validate_ping_response(&payload, "request-1").expect("expected valid ping response");
    }

    #[test]
    fn parse_host_version_response_falls_back_only_for_method_not_found() {
        let method_not_found = json!({
            "jsonrpc": "2.0",
            "id": "request-2",
            "error": {
                "code": -32601,
                "message": "method_not_found"
            }
        });
        assert_eq!(
            parse_host_version_response(&method_not_found, "request-2", Some("0.7.1"))
                .expect("expected fallback result"),
            Some("0.7.1".to_string())
        );

        let invalid_params = json!({
            "jsonrpc": "2.0",
            "id": "request-3",
            "error": {
                "code": -32602,
                "message": "invalid_params"
            }
        });
        let error = parse_host_version_response(&invalid_params, "request-3", Some("0.7.1"))
            .expect_err("expected non-fallback error");
        assert!(error.to_string().contains("hub.getVersion 返回错误"));
    }

    #[test]
    fn parse_host_version_response_reads_version_result() {
        let payload = json!({
            "jsonrpc": "2.0",
            "id": "request-4",
            "result": {
                "ok": true,
                "version": "0.7.0-rc.1"
            }
        });

        assert_eq!(
            parse_host_version_response(&payload, "request-4", Some("0.6.9"))
                .expect("expected version result"),
            Some("0.7.0-rc.1".to_string())
        );
    }
}
