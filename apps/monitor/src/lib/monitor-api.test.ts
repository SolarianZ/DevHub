import { describe, expect, it } from "vitest";
import {
  createStaticRuntimeResolver,
  toSdkRuntimeConnectionInfo,
} from "./monitor-api";
import type { MonitorRuntimeConnectionInfo } from "./models";

function createConnection(): MonitorRuntimeConnectionInfo {
  return {
    runtimeDirectory: "/tmp/devhub/runtime",
    token: "secret-token",
    rpcEndpoint: "http://127.0.0.1:4123/rpc",
    websocketEndpoint: "ws://127.0.0.1:4123/ws",
    runtime: {
      protocolVersion: 1,
      pid: 4242,
      httpBaseUrl: "http://127.0.0.1:4123",
      wsUrl: "ws://127.0.0.1:4123/ws",
      tokenFile: "/tmp/devhub/runtime/token.txt",
      startedAtUtc: "2026-04-12T01:02:03Z",
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 15,
        launchDedupeWindowSeconds: 5,
      },
      hubVersion: "0.6.0",
    },
  };
}

describe("monitor-api runtime conversion", () => {
  it("converts monitor runtime payloads into SDK runtime objects", () => {
    const connection = createConnection();

    expect(toSdkRuntimeConnectionInfo(connection)).toEqual({
      runtimeDirectory: "/tmp/devhub/runtime",
      token: "secret-token",
      rpcEndpoint: "http://127.0.0.1:4123/rpc",
      websocketEndpoint: "ws://127.0.0.1:4123/ws",
      runtime: {
        protocolVersion: 1,
        pid: 4242,
        httpBaseUrl: "http://127.0.0.1:4123",
        wsUrl: "ws://127.0.0.1:4123/ws",
        tokenFile: "/tmp/devhub/runtime/token.txt",
        startedAtUtc: new Date("2026-04-12T01:02:03Z"),
        runtimeTuning: {
          leaseSeconds: 30,
          onlineThresholdSeconds: 15,
          launchDedupeWindowSeconds: 5,
        },
        hubVersion: "0.6.0",
      },
    });
  });

  it("returns a stable runtime resolver for the current connection", async () => {
    const connection = createConnection();
    const resolver = createStaticRuntimeResolver(connection);
    const clientOptions = {
      clientId: "monitor-tests",
      clientSessionId: "00000000-0000-0000-0000-000000000001",
      protocolVersion: 1,
    };

    await expect(resolver.resolve(clientOptions)).resolves.toEqual(
      toSdkRuntimeConnectionInfo(connection),
    );
    await expect(resolver.resolve(clientOptions)).resolves.toEqual(
      toSdkRuntimeConnectionInfo(connection),
    );
  });
});
