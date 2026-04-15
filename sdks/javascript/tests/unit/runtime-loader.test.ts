import { afterEach, expect, it, vi } from "vitest";
import type { RuntimeConnectionInfo } from "../../src/runtime.js";

afterEach(() => {
  vi.unmock("../../src/runtime.js");
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
      FileSystemRuntimeResolver: MockFileSystemRuntimeResolver
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
      sessionFactory: () => ({
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
  let constructorCount = 0;
  const resolve = vi.fn(async () => createConnectionInfo());

  vi.doMock("../../src/runtime.js", () => {
    class MockFileSystemRuntimeResolver {
      constructor() {
        constructorCount += 1;
      }

      resolve = resolve;
    }

    return {
      FileSystemRuntimeResolver: MockFileSystemRuntimeResolver
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
      sessionFactory: () => ({
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

  expect(constructorCount).toBe(1);
  expect(resolve).toHaveBeenCalledTimes(2);

  await client.dispose();
  await eventsClient.dispose();
});

function createConnectionInfo(): RuntimeConnectionInfo {
  return {
    runtimeDirectory: "/tmp/devhub-js-sdk-runtime",
    token: "token-1",
    runtime: {
      protocolVersion: 1,
      pid: 12345,
      httpBaseUrl: "http://127.0.0.1:47231",
      wsUrl: "ws://127.0.0.1:47231/ws",
      tokenFile: "/tmp/devhub-js-sdk-runtime/runtime/token.txt",
      startedAtUtc: new Date("2026-03-09T00:00:00Z"),
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 90,
        launchDedupeWindowSeconds: 15
      }
    },
    rpcEndpoint: "http://127.0.0.1:47231/rpc",
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  };
}
