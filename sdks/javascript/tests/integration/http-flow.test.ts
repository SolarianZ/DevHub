import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubRpcErrorCode } from "../../src/errors.js";
import { DevHubHostFixture } from "./host.js";

const launchScriptPath = fileURLToPath(new URL("../assets/launch_noop.mjs", import.meta.url));
const INSTANCE_PASSWORD = "http-flow-password";

let host: DevHubHostFixture | undefined;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "http.flow.app",
    displayName: "HTTP Flow App",
    description: "用于 SDK HTTP 链路测试。"
  });
  await host.writeDefinition(createLaunchDefinition("http.launch.started.app"));
  await host.writeDefinition(createLaunchDefinition("http.launch.starting.app"));
  await host.writeDefinition(createLaunchDefinition("http.launch.running.app"));
}, 120_000);

afterAll(async () => {
  await host?.close();
});

it("M5_E2E_001_And_002 HTTP 链路应可完成基础流程", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-flow-client",
    dataDir: getHost().dataDirectory
  });

  const ping = await client.ping({ value: 1 });
  expect(ping.ok).toBe(true);
  expect((ping.echo as { value: number }).value).toBe(1);

  const definitions = await client.listDefinitions();
  expect(definitions.some((definition) => definition.appId === "http.flow.app")).toBe(true);
  expect(definitions.find((definition) => definition.appId === "http.flow.app")?.capabilities).toEqual({
    rpc: true
  });

  const definitionResult = await client.getDefinition("http.flow.app");
  expect(definitionResult.displayName).toBe("HTTP Flow App");
  expect(definitionResult.capabilities).toEqual({
    rpc: true
  });

  const validation = await client.validateDefinition({
    appId: "http.managed.app",
    displayName: ""
  });
  expect(validation.ok).toBe(true);
  expect(validation.valid).toBe(false);
  expect(validation.errors[0]).toMatchObject({
    path: "definition.displayName",
    code: "missing_display_name"
  });

  const upserted = await client.upsertDefinition({
    appId: "http.managed.app",
    displayName: "HTTP Managed App"
  });
  expect(upserted.displayName).toBe("HTTP Managed App");

  const managedDefinition = await client.getDefinition("http.managed.app");
  expect(managedDefinition.displayName).toBe("HTTP Managed App");
  expect(managedDefinition.capabilities).toEqual({
    rpc: true
  });

  await client.deleteDefinition("http.managed.app");
  await expect(client.getDefinition("http.managed.app")).rejects.toMatchObject({
    code: DevHubRpcErrorCode.AppDefinitionNotFound
  });

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
  }, INSTANCE_PASSWORD);

  expect(registered.instanceId).toBe("http-flow-inst-1");

  const instances = await client.listInstances({
    appId: "http.flow.app"
  });
  expect(instances.length).toBe(1);

  const lastSeenUtc = await client.heartbeat("http-flow-inst-1");
  expect(lastSeenUtc.getTime()).toBeGreaterThan(0);

  await client.unregisterInstance("http-flow-inst-1", INSTANCE_PASSWORD);
  const instancesAfter = await client.listInstances({
    appId: "http.flow.app"
  });
  expect(instancesAfter.length).toBe(0);

  await client.dispose();
});

it("M5_E2E_002 launch 应覆盖 started / starting / already_running", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-launch-client",
    dataDir: getHost().dataDirectory
  });

  const started = await client.launch({
    appId: "http.launch.started.app",
    waitForRegisterMs: 0
  });
  expect(started.ok).toBe(true);
  expect(started.status).toBe("started");
  expect(started.pid).toBeGreaterThan(0);
  expect(started.launchId).toMatch(/^launch-/);

  const starting = await client.launch({
    appId: "http.launch.starting.app",
    waitForRegisterMs: 200
  });
  expect(starting.ok).toBe(true);
  expect(starting.status).toBe("starting");
  expect(starting.pid).toBeGreaterThan(0);
  expect(starting.launchId).toMatch(/^launch-/);

  const registered = await client.registerInstance({
    instanceId: "http-launch-running-inst-1",
    appId: "http.launch.running.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  const alreadyRunning = await client.launch({
    appId: "http.launch.running.app"
  });
  expect(alreadyRunning.ok).toBe(true);
  expect(alreadyRunning.status).toBe("already_running");
  expect(alreadyRunning.pid).toBe(registered.pid);
  expect(alreadyRunning.launchId).toMatch(/^launch-/);

  await client.unregisterInstance("http-launch-running-inst-1", INSTANCE_PASSWORD);
  await client.dispose();
});

function createLaunchDefinition(appId: string): Record<string, unknown> {
  return {
    appId,
    displayName: appId,
    launch: {
      exePath: process.execPath,
      argsTemplate: quoteCommandArgument(path.normalize(launchScriptPath))
    }
  };
}

function quoteCommandArgument(value: string): string {
  return value.includes(" ") ? `"${value}"` : value;
}

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture not started.");
  }

  return host;
}
