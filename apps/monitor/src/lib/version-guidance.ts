import type { VersionCompatibilityResult, VersionCompatibilityStatus } from "@devhub/sdk";
import type { BootstrapSnapshot, MonitorProblem } from "./models";
import { MONITOR_VERSION_METADATA } from "./version-metadata";

export function formatVersionCompatibilityStatusLabel(status: VersionCompatibilityStatus): string {
  switch (status) {
    case "compatible":
      return "兼容";
    case "updateRecommended":
      return "建议升级";
    case "incompatible":
      return "不兼容";
    case "unknown":
      return "兼容性未知";
    default:
      return "未知";
  }
}

export function formatVersionCompatibilityHostVersion(
  hostVersion: string | null | undefined,
  fallback = "未知",
): string {
  const normalized = hostVersion?.trim();
  return normalized && normalized.length > 0 ? normalized : fallback;
}

export function getVersionGuidanceTitle(status: VersionCompatibilityStatus): string {
  switch (status) {
    case "updateRecommended":
      return "建议升级 Host";
    case "unknown":
      return "Host 兼容性未知";
    case "incompatible":
      return "当前 Host 版本不受支持";
    case "compatible":
    default:
      return "版本兼容";
  }
}

export function formatVersionGuidanceMessage(result: VersionCompatibilityResult): string {
  const hostVersion = formatVersionCompatibilityHostVersion(result.hostVersion);

  switch (result.status) {
    case "updateRecommended":
      return `当前 Host 版本为 ${hostVersion}，内置 JS SDK 版本为 ${result.sdkVersion}。两者 major 相同但 minor 不一致，建议升级到同一版本线。`;
    case "unknown":
      return `当前 Host 版本上下文为 ${hostVersion}，内置 JS SDK 版本为 ${result.sdkVersion}。Monitor 无法确认这组版本是否完全兼容。`;
    case "incompatible":
      return `当前 Host 版本为 ${hostVersion}，内置 JS SDK 版本为 ${result.sdkVersion}。这组版本无法建立受支持的会话。`;
    case "compatible":
    default:
      return `当前 Host 版本为 ${hostVersion}，内置 JS SDK 版本为 ${result.sdkVersion}。`;
  }
}

export function createProtectiveIncompatibilityProblem(
  result: VersionCompatibilityResult,
): MonitorProblem {
  return {
    code: "host_incompatible",
    message:
      `当前 Monitor v${MONITOR_VERSION_METADATA.monitorVersion} 内置的 JS SDK 与 DevHub Host 版本不兼容。`
      + ` JS SDK=${result.sdkVersion}; Host=${formatVersionCompatibilityHostVersion(result.hostVersion)}。`,
  };
}

export function createProtectiveIncompatibleBootstrap(
  snapshot: BootstrapSnapshot,
  result: VersionCompatibilityResult,
): BootstrapSnapshot {
  return {
    ...snapshot,
    phase: "host_incompatible",
    connection: null,
    lastProblem: createProtectiveIncompatibilityProblem(result),
  };
}
