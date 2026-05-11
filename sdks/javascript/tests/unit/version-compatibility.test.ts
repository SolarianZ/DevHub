import { fileURLToPath } from "node:url";
import { afterEach, expect, it, vi } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
import {
  DevHubRpcError,
  DevHubRpcErrorCode
} from "../../src/errors.js";
import { SDK_VERSION } from "../../src/sdk-version.js";
import type { JsonRpcEventSession } from "../../src/index.js";
import {
  checkVersionCompatibilityWithFallback,
  createVersionCompatibilityResult,
  parseSemVerMajorMinor
} from "../../src/versioning.js";

const TEST_RUNTIME_DIRECTORY = absoluteTestPath("devhub-js-sdk-runtime/runtime");
const TEST_TOKEN_FILE = absoluteTestPath("devhub-js-sdk-runtime/runtime/token.txt");

afterEach(() => {
  vi.restoreAllMocks();
});

it("createVersionCompatibilityResult 应按 major/minor 规则比较并忽略 patch/预发布/构建元数据", () => {
  const sdk = requireSdkSemVer();

  expect(
    createVersionCompatibilityResult(
      SDK_VERSION,
      `${sdk.major + 1}.${sdk.minor}.0`
    ).status
  ).toBe("incompatible");

  expect(
    createVersionCompatibilityResult(
      SDK_VERSION,
      `${sdk.major}.${sdk.minor + 1}.0`
    ).status
  ).toBe("updateRecommended");

  expect(
    createVersionCompatibilityResult(
      SDK_VERSION,
      `${sdk.major}.${sdk.minor}.99-beta.1+build.3`
    ).status
  ).toBe("compatible");
});

it("createVersionCompatibilityResult 在版本缺失或非法时应返回 unknown", () => {
  expect(createVersionCompatibilityResult(SDK_VERSION, null)).toEqual({
    sdkVersion: SDK_VERSION,
    hostVersion: null,
    status: "unknown"
  });

  expect(createVersionCompatibilityResult(SDK_VERSION, "not-a-semver")).toEqual({
    sdkVersion: SDK_VERSION,
    hostVersion: "not-a-semver",
    status: "unknown"
  });
});

it("checkVersionCompatibilityWithFallback 遇到 method_not_found 时应使用 runtime.hubVersion", async () => {
  const sdk = requireSdkSemVer();
  const fallbackVersion = `${sdk.major}.${sdk.minor}.7`;

  const result = await checkVersionCompatibilityWithFallback(
    async () => {
      throw new DevHubRpcError({
        code: DevHubRpcErrorCode.MethodNotFound,
        message: "Method not found",
        requestId: "req-version-fallback"
      });
    },
    fallbackVersion,
    SDK_VERSION
  );

  expect(result).toEqual({
    sdkVersion: SDK_VERSION,
    hostVersion: fallbackVersion,
    status: "compatible"
  });
});

it("checkVersionCompatibilityWithFallback 不应吞掉非 method_not_found 错误", async () => {
  const forbiddenError = new DevHubRpcError({
    code: DevHubRpcErrorCode.Forbidden,
    message: "Forbidden",
    requestId: "req-version-forbidden"
  });

  await expect(
    checkVersionCompatibilityWithFallback(
      async () => {
        throw forbiddenError;
      },
      "0.7.0",
      SDK_VERSION
    )
  ).rejects.toBe(forbiddenError);
});

it("DevHubClient.getHostVersion 应调用 hub.getVersion", async () => {
  const transport = {
    send: vi.fn(async (method: string) => {
      expect(method).toBe("hub.getVersion");
      return {
        ok: true,
        version: "0.7.0-host"
      };
    })
  };

  const client = await DevHubClient.fromRuntime(
    { clientId: "unit-http-get-version-client" },
    {
      runtimeResolver: {
        resolve: async () => createConnectionInfo()
      },
      transportFactory: () => transport
    }
  );

  try {
    await expect(client.getHostVersion()).resolves.toBe("0.7.0-host");
    expect(transport.send).toHaveBeenCalledTimes(1);
  } finally {
    await client.dispose();
  }
});

it("DevHubClient.checkVersionCompatibility 遇到旧 Host 时应回退到 runtime.hubVersion", async () => {
  const sdk = requireSdkSemVer();
  const fallbackVersion = `${sdk.major}.${sdk.minor}.3+runtime`;
  const transport = {
    send: vi.fn(async () => {
      throw new DevHubRpcError({
        code: DevHubRpcErrorCode.MethodNotFound,
        message: "Method not found",
        requestId: "req-client-fallback"
      });
    })
  };

  const client = await DevHubClient.fromRuntime(
    { clientId: "unit-http-check-version-client" },
    {
      runtimeResolver: {
        resolve: async () => createConnectionInfo(fallbackVersion)
      },
      transportFactory: () => transport
    }
  );

  try {
    await expect(client.checkVersionCompatibility()).resolves.toEqual({
      sdkVersion: SDK_VERSION,
      hostVersion: fallbackVersion,
      status: "compatible"
    });
    expect(transport.send).toHaveBeenCalledTimes(1);
  } finally {
    await client.dispose();
  }
});

it("DevHubClient.checkVersionCompatibility 在无法判断时应返回 unknown", async () => {
  const transport = {
    send: vi.fn(async () => {
      throw new DevHubRpcError({
        code: DevHubRpcErrorCode.MethodNotFound,
        message: "Method not found",
        requestId: "req-client-unknown"
      });
    })
  };

  const client = await DevHubClient.fromRuntime(
    { clientId: "unit-http-check-version-unknown-client" },
    {
      runtimeResolver: {
        resolve: async () => createConnectionInfo(undefined)
      },
      transportFactory: () => transport
    }
  );

  try {
    await expect(client.checkVersionCompatibility()).resolves.toEqual({
      sdkVersion: SDK_VERSION,
      hostVersion: null,
      status: "unknown"
    });
  } finally {
    await client.dispose();
  }
});

it("DevHubEventsClient 在认证前调用版本接口时应本地失败且不发送 WS 请求", async () => {
  const ensureConnected = vi.fn(async () => {
  });
  const sendRequest = vi.fn(async () => ({
    ok: true,
    version: "0.7.0-host"
  }));

  const client = await DevHubEventsClient.fromRuntime(
    { clientId: "unit-events-version-preauth-client" },
    {
      runtimeResolver: {
        resolve: async () => createConnectionInfo()
      },
      sessionFactory: () => createStubEventSession({
        ensureConnected,
        sendRequest,
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await expect(client.getHostVersion()).rejects.toThrow(/not authenticated/i);
    await expect(client.checkVersionCompatibility()).rejects.toThrow(/not authenticated/i);
    expect(ensureConnected).not.toHaveBeenCalled();
    expect(sendRequest).not.toHaveBeenCalled();
  } finally {
    await client.dispose();
  }
});

it("DevHubEventsClient 认证后应支持版本查询与兼容性检查", async () => {
  const sdk = requireSdkSemVer();
  const hostVersion = `${sdk.major}.${sdk.minor}.4-beta.2`;
  const requests: string[] = [];

  const client = await DevHubEventsClient.fromRuntime(
    { clientId: "unit-events-version-client" },
    {
      runtimeResolver: {
        resolve: async () => createConnectionInfo(hostVersion)
      },
      sessionFactory: () => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          requests.push(method);
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.getVersion") {
            return {
              ok: true,
              version: hostVersion
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    await expect(client.getHostVersion()).resolves.toBe(hostVersion);
    await expect(client.checkVersionCompatibility()).resolves.toEqual({
      sdkVersion: SDK_VERSION,
      hostVersion,
      status: "compatible"
    });
    expect(requests).toEqual([
      "hub.ws.authenticate",
      "hub.getVersion",
      "hub.getVersion"
    ]);
  } finally {
    await client.dispose();
  }
});

function requireSdkSemVer(): { major: number; minor: number } {
  const parsed = parseSemVerMajorMinor(SDK_VERSION);
  expect(parsed).not.toBeNull();
  return parsed as { major: number; minor: number };
}

function createConnectionInfo(hubVersion?: string) {
  return {
    runtimeDirectory: TEST_RUNTIME_DIRECTORY,
    token: "token-fake",
    runtime: {
      protocolVersion: 1,
      pid: 12345,
      httpBaseUrl: "http://127.0.0.1:57231",
      wsUrl: "ws://127.0.0.1:57231/ws",
      tokenFile: TEST_TOKEN_FILE,
      startedAtUtc: new Date("2026-03-09T00:00:00Z"),
      ...(hubVersion === undefined ? {} : { hubVersion }),
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 30,
        launchDedupeWindowSeconds: 30,
        launchRegisterTimeoutSeconds: 30
      }
    },
    rpcEndpoint: "http://127.0.0.1:57231/rpc",
    websocketEndpoint: "ws://127.0.0.1:57231/ws"
  };
}

function createStubEventSession(
  session: Omit<JsonRpcEventSession, "getAbandonedRequestCount" | "clearAbandonedRequests">
    & Partial<Pick<JsonRpcEventSession, "getAbandonedRequestCount" | "clearAbandonedRequests">>
): JsonRpcEventSession {
  return {
    getAbandonedRequestCount: () => 0,
    clearAbandonedRequests: () => 0,
    ...session
  };
}

function absoluteTestPath(relativePath: string): string {
  return fileURLToPath(new URL(`../../../.tmp-test-paths/${relativePath}`, import.meta.url));
}
