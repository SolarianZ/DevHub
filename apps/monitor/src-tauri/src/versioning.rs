pub const MONITOR_VERSION: &str = env!("DEVHUB_MONITOR_VERSION");
pub const SDK_VERSION: &str = env!("DEVHUB_MONITOR_SDK_VERSION");

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VersionCompatibilityResult {
    pub sdk_version: String,
    pub host_version: Option<String>,
    pub status: VersionCompatibilityStatus,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VersionCompatibilityStatus {
    Compatible,
    UpdateRecommended,
    Incompatible,
    Unknown,
}

impl VersionCompatibilityStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Compatible => "compatible",
            Self::UpdateRecommended => "updateRecommended",
            Self::Incompatible => "incompatible",
            Self::Unknown => "unknown",
        }
    }
}

pub fn create_version_compatibility_result(
    host_version: Option<&str>,
) -> VersionCompatibilityResult {
    create_version_compatibility_result_for_sdk(SDK_VERSION, host_version)
}

pub fn create_version_compatibility_result_for_sdk(
    sdk_version: &str,
    host_version: Option<&str>,
) -> VersionCompatibilityResult {
    VersionCompatibilityResult {
        sdk_version: sdk_version.to_string(),
        host_version: host_version.map(str::to_string),
        status: resolve_version_compatibility_status(sdk_version, host_version),
    }
}

pub fn resolve_version_compatibility_status(
    sdk_version: &str,
    host_version: Option<&str>,
) -> VersionCompatibilityStatus {
    let Some(sdk) = parse_semver_major_minor(sdk_version) else {
        return VersionCompatibilityStatus::Unknown;
    };
    let Some(host) = host_version.and_then(parse_semver_major_minor) else {
        return VersionCompatibilityStatus::Unknown;
    };

    if sdk.major != host.major {
        return VersionCompatibilityStatus::Incompatible;
    }

    if sdk.minor != host.minor {
        return VersionCompatibilityStatus::UpdateRecommended;
    }

    VersionCompatibilityStatus::Compatible
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct SemVerMajorMinor {
    major: u64,
    minor: u64,
}

fn parse_semver_major_minor(version: &str) -> Option<SemVerMajorMinor> {
    if version.is_empty() || version.trim() != version {
        return None;
    }

    let (without_build, build_metadata) = match version.split_once('+') {
        Some((head, tail)) => (head, Some(tail)),
        None => (version, None),
    };
    if let Some(value) = build_metadata {
        validate_dot_identifiers(value)?;
    }

    let (core, prerelease) = match without_build.split_once('-') {
        Some((head, tail)) => (head, Some(tail)),
        None => (without_build, None),
    };
    if let Some(value) = prerelease {
        validate_dot_identifiers(value)?;
    }

    let mut parts = core.split('.');
    let major = parse_numeric_identifier(parts.next()?)?;
    let minor = parse_numeric_identifier(parts.next()?)?;
    let _patch = parse_numeric_identifier(parts.next()?)?;
    if parts.next().is_some() {
        return None;
    }

    Some(SemVerMajorMinor { major, minor })
}

fn validate_dot_identifiers(value: &str) -> Option<()> {
    if value.is_empty() {
        return None;
    }

    for identifier in value.split('.') {
        if identifier.is_empty()
            || !identifier
                .chars()
                .all(|character| character.is_ascii_alphanumeric() || character == '-')
        {
            return None;
        }
    }

    Some(())
}

fn parse_numeric_identifier(raw_value: &str) -> Option<u64> {
    if raw_value.is_empty()
        || (raw_value.len() > 1 && raw_value.starts_with('0'))
        || !raw_value
            .chars()
            .all(|character| character.is_ascii_digit())
    {
        return None;
    }

    raw_value.parse::<u64>().ok()
}

#[cfg(test)]
mod tests {
    use super::{
        create_version_compatibility_result_for_sdk, parse_semver_major_minor,
        resolve_version_compatibility_status, VersionCompatibilityStatus,
    };

    #[test]
    fn version_compatibility_follows_major_minor_only() {
        assert_eq!(
            resolve_version_compatibility_status("0.7.0", Some("0.7.9-rc.1+build.5")),
            VersionCompatibilityStatus::Compatible
        );
        assert_eq!(
            resolve_version_compatibility_status("0.7.0", Some("0.8.1")),
            VersionCompatibilityStatus::UpdateRecommended
        );
        assert_eq!(
            resolve_version_compatibility_status("0.7.0", Some("1.0.0")),
            VersionCompatibilityStatus::Incompatible
        );
    }

    #[test]
    fn version_compatibility_returns_unknown_for_missing_or_invalid_versions() {
        assert_eq!(
            create_version_compatibility_result_for_sdk("0.7.0", None),
            super::VersionCompatibilityResult {
                sdk_version: "0.7.0".to_string(),
                host_version: None,
                status: VersionCompatibilityStatus::Unknown,
            }
        );
        assert_eq!(
            resolve_version_compatibility_status("0.7.0", Some(" 0.7.0 ")),
            VersionCompatibilityStatus::Unknown
        );
        assert_eq!(parse_semver_major_minor("0.7"), None);
        assert_eq!(parse_semver_major_minor("00.7.0"), None);
    }
}
