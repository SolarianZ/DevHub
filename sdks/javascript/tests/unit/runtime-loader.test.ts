import { fileURLToPath } from "node:url";
import { afterEach, expect, it, vi } from "vitest";
import type { RuntimeConnectionInfo } from "../../src/runtime.js";

const TEST_RUNTIME_DIRECTORY = absoluteTestPath("devhub-js-sdk-runtime");
const TEST_TOKEN_FILE = absoluteTestPath("devhub-js-sdk-runtime/runtime/token.txt");

afterEach(() => {
  vi.doUnmock("../../src/runtime.js");
  vi.resetModules();
  vi.restoreAllMocks();
});

it("显式 runtimeResolver 不应触发默认 runtime 模块加载", async () => {
  let runtimeModuleLoaded = false;
  vi.doMock("../../src/runtime.js", () => {
    runtimeModuleLoaded = true;
    class MockFileSystemRuntimeResolver {
      async resolve(): Promise<RuntimeConnectionInfo> {
        return createConnectionInfo();
      }
    }

    return {
      FileSystemRuntimeResolver: MockFileSystemRuntimeResolver,
      validateRuntimeConnectionInfo: () => {}
    };
  });

  const { DevHubClient } = await import("../../src/client.js");
  const { DevHubEventsClient } = await import("../../src/events-client.js");
  const runtimeResolver = {
    resolve: vi.fn(async () => createConnectionInfo())
  };

  const client = await DevHubClient.fromRuntime(
    { clientId: "runtime-loader-client" },
    {
      runtimeResolver,
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          serverTimeUtc: "2026-03-09T00:00:00Z"
        })
      })
    }
  );
  const eventsClient = await DevHubEventsClient.fromRuntime(
    { clientId: "runtime-loader-events" },
    {
      runtimeResolver,
      sessionFactory: () => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(): Promise<Record<string, unknown>> {
          return {
            ok: true,
            protocolVersion: 1
          };
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  expect(runtimeResolver.resolve).toHaveBeenCalledTimes(2);
  expect(runtimeModuleLoaded).toBe(false);

  await client.dispose();
  await eventsClient.dispose();
});

it("默认 runtime 模块应在两个入口之间共享缓存", async () => {
  const resolve = vi.fn(async () => createConnectionInfo());

  vi.doMock("../../src/runtime.js", () => {
    class MockFileSystemRuntimeResolver {
      resolve = resolve;
    }

    return {
      FileSystemRuntimeResolver: MockFileSystemRuntimeResolver,
      validateRuntimeConnectionInfo: () => {}
    };
  });

  const { DevHubClient } = await import("../../src/client.js");
  const { DevHubEventsClient } = await import("../../src/events-client.js");

  const client = await DevHubClient.fromRuntime(
    { clientId: "runtime-loader-default-client" },
    {
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          serverTimeUtc: "2026-03-09T00:00:00Z"
        })
      })
    }
  );
  const eventsClient = await DevHubEventsClient.fromRuntime(
    { clientId: "runtime-loader-default-events" },
    {
      sessionFactory: () => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(): Promise<Record<string, unknown>> {
          return {
            ok: true,
            protocolVersion: 1
          };
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  expect(resolve).toHaveBeenCalledTimes(2);

  await client.dispose();
  await eventsClient.dispose();
});

function createConnectionInfo(): RuntimeConnectionInfo {
  return {
    runtimeDirectory: TEST_RUNTIME_DIRECTORY,
    token: "token-1",
    runtime: {
      protocolVersion: 1,
      pid: 12345,
      httpBaseUrl: "http://127.0.0.1:47231",
      wsUrl: "ws://127.0.0.1:47231/ws",
      tokenFile: TEST_TOKEN_FILE,
      startedAtUtc: new Date("2026-03-09T00:00:00Z"),
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 90,
        launchDedupeWindowSeconds: 15,
        launchRegisterTimeoutSeconds: 45
      }
    },
    rpcEndpoint: "http://127.0.0.1:47231/rpc",
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  };
}

function createStubEventSession(session: {
  ensureConnected(): Promise<void>;
  sendRequest(): Promise<Record<string, unknown>>;
  disconnect(): Promise<void>;
  dispose(): Promise<void>;
}) {
  return {
    getAbandonedRequestCount: () => 0,
    clearAbandonedRequests: () => 0,
    ...session
  };
}

function absoluteTestPath(relativePath: string): string {
  return fileURLToPath(new URL(`../../../.tmp-test-paths/${relativePath}`, import.meta.url));
}
