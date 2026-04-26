import rawMetadata from "../generated/version-metadata.json";

interface VersionMetadata {
  monitorVersion: string;
  sdkVersion: string;
}

function validateVersionMetadata(value: unknown): VersionMetadata {
  if (!isRecord(value)) {
    throw new Error("Monitor 版本元数据格式非法。");
  }

  const monitorVersion = value.monitorVersion;
  const sdkVersion = value.sdkVersion;
  if (!isNonEmptyString(monitorVersion) || !isNonEmptyString(sdkVersion)) {
    throw new Error("Monitor 版本元数据缺少有效的 monitorVersion 或 sdkVersion。");
  }

  return {
    monitorVersion,
    sdkVersion,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

export const MONITOR_VERSION_METADATA = validateVersionMetadata(rawMetadata);
