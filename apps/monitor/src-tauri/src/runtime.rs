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

pub async fn verify_runtime(connection: &MonitorRuntimeConnectionInfo) -> Result<()> {
    let client = Client::builder()
        .timeout(std::time::Duration::from_millis(1500))
        .build()
        .context("无法创建 Hub 校验 HTTP 客户端。")?;

    let request_id = format!("monitor-ping-{}", Uuid::new_v4());
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

    let response = client
        .post(&connection.rpc_endpoint)
        .headers(headers)
        .json(&json!({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "hub.ping",
            "params": {
                "echo": "monitor-discovery"
            }
        }))
        .send()
        .await
        .with_context(|| format!("hub.ping 请求失败：{}", connection.rpc_endpoint))?;

    let payload = response
        .json::<Value>()
        .await
        .context("hub.ping 响应 JSON 无法解析。")?;

    if payload.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
        anyhow::bail!("hub.ping 响应缺少合法 jsonrpc 字段。");
    }

    if payload.get("id").and_then(Value::as_str) != Some(request_id.as_str()) {
        anyhow::bail!("hub.ping 响应 id 不匹配。");
    }

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
    if runtime.protocol_version != 1 {
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

#[cfg(test)]
mod tests {
    use super::{discover_runtime, port_from_runtime};
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
        fs::write(
            runtime_directory.join("hub.json"),
            format!(
                r#"{{
  "protocolVersion": 1,
  "pid": 4321,
  "httpBaseUrl": "http://127.0.0.1:4123",
  "wsUrl": "ws://127.0.0.1:4123/ws",
  "tokenFile": "{}",
  "startedAtUtc": "2026-04-12T00:00:00Z",
  "runtimeTuning": {{
    "leaseSeconds": 30,
    "onlineThresholdSeconds": 15,
    "launchDedupeWindowSeconds": 5
  }},
  "hubVersion": "0.6.0"
}}"#,
                token_path.display()
            ),
        )
        .expect("failed to write hub.json");

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
}
