import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
import { SDK_VERSION } from "../../src/sdk-version.js";
import type { VersionCompatibilityStatus } from "../../src/index.js";
import { DevHubHostFixture } from "./host.js";

const SEMVER_REGEX = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

let host: DevHubHostFixture | undefined;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
}, 120_000);

afterAll(async () => {
  await host?.close();
});

it("HTTP client 应可从真实 Host 读取版本并完成兼容性检查", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-version-compatibility-client",
    dataDir: getHost().dataDirectory
  });

  try {
    const hostVersion = await client.getHostVersion();
    const compatibility = await client.checkVersionCompatibility();

    expect(hostVersion).toMatch(SEMVER_REGEX);
    expect(compatibility).toEqual({
      sdkVersion: SDK_VERSION,
      hostVersion,
      status: computeExpectedStatus(SDK_VERSION, hostVersion)
    });
  } finally {
    await client.dispose();
  }
});

it("已鉴权 events client 应可从真实 Host 读取版本并完成兼容性检查", async () => {
  const client = await DevHubEventsClient.fromRuntime({
    clientId: "events-version-compatibility-client",
    dataDir: getHost().dataDirectory
  });

  try {
    await client.authenticate();

    const hostVersion = await client.getHostVersion();
    const compatibility = await client.checkVersionCompatibility();

    expect(hostVersion).toMatch(SEMVER_REGEX);
    expect(compatibility).toEqual({
      sdkVersion: SDK_VERSION,
      hostVersion,
      status: computeExpectedStatus(SDK_VERSION, hostVersion)
    });
  } finally {
    await client.dispose();
  }
});

function computeExpectedStatus(sdkVersion: string, hostVersion: string): VersionCompatibilityStatus {
  const sdk = parseMajorMinor(sdkVersion);
  const host = parseMajorMinor(hostVersion);

  if (sdk.major !== host.major) {
    return "incompatible";
  }

  if (sdk.minor !== host.minor) {
    return "updateRecommended";
  }

  return "compatible";
}

function parseMajorMinor(version: string): { major: number; minor: number } {
  const match = SEMVER_REGEX.exec(version);
  if (!match?.[1] || !match[2]) {
    throw new Error(`无法解析版本：${version}`);
  }

  return {
    major: Number(match[1]),
    minor: Number(match[2])
  };
}

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture 未启动。");
  }

  return host;
}
