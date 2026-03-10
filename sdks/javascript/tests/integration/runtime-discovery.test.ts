import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient, DevHubEventsClient } from "../../src/client.js";
import { discoverRuntime } from "../../src/runtime.js";
import { DevHubHostFixture } from "./host.js";

let host: DevHubHostFixture;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
}, 60_000);

afterAll(async () => {
  await host.close();
});

it("运行时发现应返回有效连接信息", async () => {
  const info = await discoverRuntime(host.runtimeDirectory);
  expect(info.runtime.protocolVersion).toBe(1);
  expect(info.runtime.pid).toBeGreaterThan(0);
  expect(info.runtime.httpBaseUrl).toMatch(/^https?:\/\//);
  expect(info.runtime.wsUrl).toMatch(/^wss?:\/\//);
  expect(info.token.length).toBeGreaterThan(0);
  expect(info.rpcEndpoint).toBe(`${info.runtime.httpBaseUrl}/rpc`);
  expect(info.websocketEndpoint).toBe(info.runtime.wsUrl);
});

it("fromRuntime 应构造客户端连接", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "integration-client",
    runtimeDir: host.runtimeDirectory
  });

  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "integration-events-client",
    runtimeDir: host.runtimeDirectory
  });

  expect(client.connection.runtimeDirectory).toBe(host.runtimeDirectory);
  expect(eventsClient.connection.runtimeDirectory).toBe(host.runtimeDirectory);
  expect(client.connection.token.length).toBeGreaterThan(0);
  expect(eventsClient.connection.token.length).toBeGreaterThan(0);
});
