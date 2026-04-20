use crate::logging::MonitorLogService;
use crate::models::{MonitorLogLevel, MonitorProblem, MonitorStructuredLogRecord};
use anyhow::Result;
use chrono::Utc;
use serde_json::{Map, Value};

pub fn record_backend_log(
    log_service: &MonitorLogService,
    effective_data_dir: String,
    level: MonitorLogLevel,
    category: &str,
    action: &str,
    result: &str,
    message: Option<&str>,
    context: Option<Map<String, Value>>,
) -> Result<()> {
    log_service.record(build_backend_log_record(
        effective_data_dir,
        level,
        category,
        action,
        result,
        message,
        context,
    ))
}

fn build_backend_log_record(
    effective_data_dir: String,
    level: MonitorLogLevel,
    category: &str,
    action: &str,
    result: &str,
    message: Option<&str>,
    context: Option<Map<String, Value>>,
) -> MonitorStructuredLogRecord {
    MonitorStructuredLogRecord {
        timestamp_utc: Utc::now().to_rfc3339(),
        level,
        category: category.to_string(),
        action: action.to_string(),
        result: result.to_string(),
        message: message.map(str::to_string),
        data_dir: Some(effective_data_dir),
        host_pid: context
            .as_ref()
            .and_then(|map| map.get("hostPid"))
            .and_then(Value::as_u64)
            .map(|value| value as u32),
        port: context
            .as_ref()
            .and_then(|map| map.get("port"))
            .and_then(Value::as_u64)
            .map(|value| value as u16),
        app_id: context
            .as_ref()
            .and_then(|map| map.get("appId"))
            .and_then(Value::as_str)
            .map(str::to_string),
        instance_id: context
            .as_ref()
            .and_then(|map| map.get("instanceId"))
            .and_then(Value::as_str)
            .map(str::to_string),
        error_code: context
            .as_ref()
            .and_then(|map| map.get("errorCode"))
            .and_then(Value::as_str)
            .map(str::to_string),
        context,
    }
}

pub fn problem(code: impl Into<String>, message: impl Into<String>) -> MonitorProblem {
    MonitorProblem {
        code: code.into(),
        message: message.into(),
    }
}

pub fn json_map(entries: Vec<(&str, Value)>) -> Map<String, Value> {
    entries
        .into_iter()
        .map(|(key, value)| (key.to_string(), value))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{build_backend_log_record, json_map, problem};
    use crate::models::MonitorLogLevel;
    use chrono::DateTime;
    use serde_json::json;

    #[test]
    fn backend_log_record_populates_common_fields_and_context_identifiers() {
        let context = json_map(vec![
            ("hostPid", json!(4123)),
            ("port", json!(6200)),
            ("appId", json!("demo.app")),
            ("instanceId", json!("instance-1")),
            ("errorCode", json!("launch_failed")),
            ("reason", json!("unit-test")),
        ]);
        let record = build_backend_log_record(
            "/tmp/devhub".to_string(),
            MonitorLogLevel::Warn,
            "discovery",
            "scan",
            "failed",
            Some("Discovery failed."),
            Some(context.clone()),
        );

        DateTime::parse_from_rfc3339(&record.timestamp_utc).expect("expected RFC3339 timestamp");
        assert!(matches!(record.level, MonitorLogLevel::Warn));
        assert_eq!(record.category, "discovery");
        assert_eq!(record.action, "scan");
        assert_eq!(record.result, "failed");
        assert_eq!(record.message.as_deref(), Some("Discovery failed."));
        assert_eq!(record.data_dir.as_deref(), Some("/tmp/devhub"));
        assert_eq!(record.host_pid, Some(4123));
        assert_eq!(record.port, Some(6200));
        assert_eq!(record.app_id.as_deref(), Some("demo.app"));
        assert_eq!(record.instance_id.as_deref(), Some("instance-1"));
        assert_eq!(record.error_code.as_deref(), Some("launch_failed"));
        assert_eq!(record.context, Some(context));
    }

    #[test]
    fn problem_returns_expected_monitor_problem() {
        let issue = problem("host_unavailable", "hub.ping failed");

        assert_eq!(issue.code, "host_unavailable");
        assert_eq!(issue.message, "hub.ping failed");
    }

    #[test]
    fn json_map_collects_entries_into_object() {
        let map = json_map(vec![("reason", json!("startup")), ("attempt", json!(2))]);

        assert_eq!(map.get("reason"), Some(&json!("startup")));
        assert_eq!(map.get("attempt"), Some(&json!(2)));
    }
}
