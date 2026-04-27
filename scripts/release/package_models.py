from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass
class ValidationRecord:
    name: str
    command: list[str]
    cwd: str
    logPath: str
    status: str


@dataclass(frozen=True)
class ReleaseAsset:
    path: Path
    category: str
    target: str
    variant: str | None = None
    monitorVersion: str | None = None
    javascriptSdkVersion: str | None = None


@dataclass(frozen=True)
class HostVariant:
    name: str
    archive_name_suffix: str
    publish_arguments: tuple[str, ...]

    def archive_name(self, rid: str) -> str:
        return f"devhub-host-{rid}{self.archive_name_suffix}.zip"

    def archive_root_name(self, rid: str) -> str:
        return f"devhub-host-{rid}{self.archive_name_suffix}"


HOST_VARIANTS = (
    HostVariant(
        name="multi-file",
        archive_name_suffix="",
        publish_arguments=(
            "--self-contained",
            "false",
            "-p:PublishSingleFile=false",
        ),
    ),
    HostVariant(
        name="single-file",
        archive_name_suffix="-single-file",
        publish_arguments=(
            "--self-contained",
            "false",
            "-p:PublishSingleFile=true",
            "-p:EnableCompressionInSingleFile=true",
        ),
    ),
)
HOST_VARIANT_ORDER = {variant.name: index for index, variant in enumerate(HOST_VARIANTS)}


@dataclass(frozen=True)
class PackageHelpOption:
    flag: str
    description: str


@dataclass(frozen=True)
class PackageHelpSection:
    title: str
    options: tuple[PackageHelpOption, ...]


@dataclass(frozen=True)
class PackageHelp:
    command: str
    summary: str
    sections: tuple[PackageHelpSection, ...]


@dataclass
class HostPackageOptions:
    output_dir: Path
    checks_dir: Path
    host_rids: tuple[str, ...]
    verify_only: bool = False


@dataclass(frozen=True)
class HostPackageResult:
    output_dir: Path
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    validation_records: tuple[ValidationRecord, ...]


@dataclass
class DotNetSdkPackageOptions:
    output_dir: Path
    checks_dir: Path
    verify_only: bool = False


@dataclass(frozen=True)
class DotNetSdkPackageResult:
    output_dir: Path
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    validation_records: tuple[ValidationRecord, ...]


@dataclass
class JavaScriptSdkPackageOptions:
    output_dir: Path
    checks_dir: Path
    verify_only: bool = False


@dataclass(frozen=True)
class JavaScriptSdkPackageResult:
    output_dir: Path
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    validation_records: tuple[ValidationRecord, ...]


@dataclass
class PythonSdkPackageOptions:
    output_dir: Path
    checks_dir: Path
    verify_only: bool = False


@dataclass(frozen=True)
class PythonSdkPackageResult:
    output_dir: Path
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    validation_records: tuple[ValidationRecord, ...]


@dataclass
class MonitorPackageOptions:
    release_id: str
    output_dir: Path
    checks_dir: Path
    tauri_args: tuple[str, ...] = ()
    verify_only: bool = False


@dataclass(frozen=True)
class MonitorPackageResult:
    output_dir: Path
    target_platform: str
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    manifest: dict[str, object] | None
    validation_records: tuple[ValidationRecord, ...]


@dataclass(frozen=True)
class ReleasePackageOptions:
    release_id: str
    channel: str
    release_tag: str
    release_name: str
    commit: str
    output_root: Path
    host_rids: tuple[str, ...]
    skip_monitor: bool = False
    monitor_assets_root: Path | None = None
    reuse_existing_output: bool = False


@dataclass(frozen=True)
class ReleasePackageResult:
    output_dir: Path
    assets: tuple[ReleaseAsset, ...]
    versions: dict[str, str]
    manifest: dict[str, object]
    includes_monitor: bool
    validation_records: tuple[ValidationRecord, ...]
