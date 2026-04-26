import { DevHubRpcError, DevHubRpcErrorCode } from "./errors.js";
import type {
  VersionCompatibilityResult,
  VersionCompatibilityStatus
} from "./models.js";
import { SDK_VERSION } from "./sdk-version.js";

interface SemVerMajorMinor {
  major: number;
  minor: number;
}

const SEMVER_REGEX = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

export function parseSemVerMajorMinor(version: string): SemVerMajorMinor | null {
  if (typeof version !== "string" || !version) {
    return null;
  }

  const trimmed = version.trim();
  if (trimmed !== version) {
    return null;
  }

  const match = SEMVER_REGEX.exec(trimmed);
  if (!match) {
    return null;
  }

  const major = parseNumericIdentifier(match[1]);
  const minor = parseNumericIdentifier(match[2]);
  if (major === null || minor === null) {
    return null;
  }

  return {
    major,
    minor
  };
}

export function resolveVersionCompatibilityStatus(
  sdkVersion: string,
  hostVersion: string | null
): VersionCompatibilityStatus {
  const sdk = parseSemVerMajorMinor(sdkVersion);
  const host = hostVersion === null ? null : parseSemVerMajorMinor(hostVersion);

  if (!sdk || !host) {
    return "unknown";
  }

  if (sdk.major !== host.major) {
    return "incompatible";
  }

  if (sdk.minor !== host.minor) {
    return "updateRecommended";
  }

  return "compatible";
}

export function createVersionCompatibilityResult(
  sdkVersion: string,
  hostVersion: string | null | undefined
): VersionCompatibilityResult {
  const normalizedHostVersion = hostVersion ?? null;

  return {
    sdkVersion,
    hostVersion: normalizedHostVersion,
    status: resolveVersionCompatibilityStatus(sdkVersion, normalizedHostVersion)
  };
}

export async function checkVersionCompatibilityWithFallback(
  getHostVersion: () => Promise<string>,
  fallbackHostVersion: string | undefined,
  sdkVersion = SDK_VERSION
): Promise<VersionCompatibilityResult> {
  try {
    return createVersionCompatibilityResult(sdkVersion, await getHostVersion());
  } catch (error) {
    if (!isMethodNotFoundError(error)) {
      throw error;
    }

    return createVersionCompatibilityResult(sdkVersion, fallbackHostVersion);
  }
}

function isMethodNotFoundError(error: unknown): boolean {
  return error instanceof DevHubRpcError && error.is(DevHubRpcErrorCode.MethodNotFound);
}

function parseNumericIdentifier(rawValue: string | undefined): number | null {
  if (rawValue === undefined) {
    return null;
  }

  const parsed = Number(rawValue);
  if (!Number.isSafeInteger(parsed) || parsed < 0) {
    return null;
  }

  return parsed;
}
