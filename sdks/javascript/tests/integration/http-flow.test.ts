import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubHostFixture } from "./host.js";

let host: DevHubHostFixture;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "http.flow.app",
    displayName: "HTTP Flow App",
    description: "用于 SDK HTTP 链路测试。"
  });
}, 60_000);

afterAll(async () => {
  await host.close();
});

it("HTTP 链路应可完成基础流程", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-flow-client",
    runtimeDir: host.runtimeDirectory
  });

  const ping = await client.ping({ value: 1 });
  expect(ping.ok).toBe(true);
  expect((ping.echo as { value: number }).value).toBe(1);

  const definitions = await client.listDefinitions();
  expect(definitions.some((definition) => definition.appId === "http.flow.app")).toBe(true);

  const definitionResult = await client.getDefinition("http.flow.app");
  expect(definitionResult.displayName).toBe("HTTP Flow App");

  const registered = await client.registerInstance({
    instanceId: "http-flow-inst-1",
    appId: "http.flow.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    },
    meta: {
      source: "integration"
    }
  });

  expect(registered.instanceId).toBe("http-flow-inst-1");

  const instances = await client.listInstances({
    appId: "http.flow.app"
  });
  expect(instances.length).toBe(1);

  const lastSeenUtc = await client.heartbeat("http-flow-inst-1");
  expect(lastSeenUtc.getTime()).toBeGreaterThan(0);

  await client.unregisterInstance("http-flow-inst-1");
  const instancesAfter = await client.listInstances({
    appId: "http.flow.app"
  });
  expect(instancesAfter.length).toBe(0);

  await client.dispose();
});
