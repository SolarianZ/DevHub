use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    let metadata_path =
        PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("missing CARGO_MANIFEST_DIR"))
            .join("..")
            .join("src")
            .join("generated")
            .join("version-metadata.json");
    println!("cargo:rerun-if-changed={}", metadata_path.display());
    println!("cargo:rerun-if-env-changed=DEVHUB_MONITOR_SDK_SOURCE");

    let metadata_text = fs::read_to_string(&metadata_path).unwrap_or_else(|error| {
        panic!(
            "failed to read shared Monitor version metadata at {}: {error}",
            metadata_path.display()
        )
    });
    let metadata: serde_json::Value =
        serde_json::from_str(&metadata_text).unwrap_or_else(|error| {
            panic!(
                "failed to parse shared Monitor version metadata at {}: {error}",
                metadata_path.display()
            )
        });

    let monitor_version = metadata
        .get("monitorVersion")
        .and_then(serde_json::Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| {
            panic!(
                "shared Monitor version metadata at {} is missing monitorVersion",
                metadata_path.display()
            )
        });
    let sdk_version = metadata
        .get("sdkVersion")
        .and_then(serde_json::Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| {
            panic!(
                "shared Monitor version metadata at {} is missing sdkVersion",
                metadata_path.display()
            )
        });

    println!("cargo:rustc-env=DEVHUB_MONITOR_VERSION={monitor_version}");
    println!("cargo:rustc-env=DEVHUB_MONITOR_SDK_VERSION={sdk_version}");

    tauri_build::build()
}
