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
    scope: "",
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

it("HTTP 链路应可完成基础流程", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-flow-client",
    dataDir: getHost().dataDirectory
  });

  const ping = await client.ping({ value: 1 });
  expect(ping.ok).toBe(true);
  expect((ping.echo as { value: number }).value).toBe(1);

  const definitions = await client.listDefinitions({
    scope: null
  });
  expect(definitions.some((definition) => definition.appId === "http.flow.app")).toBe(true);
  expect(definitions.find((definition) => definition.appId === "http.flow.app")?.capabilities).toEqual({
    rpc: true
  });

  const definitionResult = await client.getDefinition({
    appId: "http.flow.app",
    scope: ""
  });
  expect(definitionResult.displayName).toBe("HTTP Flow App");
  expect(definitionResult.scope).toBe("");
  expect(definitionResult.capabilities).toEqual({
    rpc: true
  });

  const validation = await client.validateDefinition({
    appId: "http.managed.app",
    scope: "",
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
    scope: "",
    displayName: "HTTP Managed App",
    description: "用于 HTTP upsert 集成测试。",
    capabilities: {
      rpc: true,
      events: false
    },
    launch: {
      exePath: process.execPath
    }
  });
  expect(upserted.displayName).toBe("HTTP Managed App");

  const managedDefinition = await client.getDefinition({
    appId: "http.managed.app",
    scope: ""
  });
  expect(managedDefinition.displayName).toBe("HTTP Managed App");
  expect(managedDefinition.scope).toBe("");
  expect(managedDefinition.capabilities).toEqual({
    rpc: true,
    events: false
  });

  await client.deleteDefinition({
    appId: "http.managed.app",
    scope: ""
  });
  await expect(client.getDefinition({
    appId: "http.managed.app",
    scope: ""
  })).rejects.toMatchObject({
    code: DevHubRpcErrorCode.AppDefinitionNotFound
  });

  const registered = await client.registerInstance({
    instanceId: "http-flow-inst-1",
    appId: "http.flow.app",
    scope: "",
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
  expect(registered.instanceSessionToken).toEqual(expect.any(String));

  const instances = await client.listInstances({
    appId: "http.flow.app",
    scope: ""
  });
  expect(instances.length).toBe(1);

  const instance = await client.getInstance("http-flow-inst-1");
  expect(instance).toMatchObject({
    instanceId: "http-flow-inst-1",
    appId: "http.flow.app",
    scope: "",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    },
    meta: {
      source: "integration"
    }
  });
  expect((instance as unknown as Record<string, unknown>).instanceSessionToken).toBeUndefined();

  const lastSeenUtc = await client.heartbeat("http-flow-inst-1", registered.instanceSessionToken);
  expect(lastSeenUtc.getTime()).toBeGreaterThan(0);

  await client.unregisterInstance("http-flow-inst-1", registered.instanceSessionToken);
  const instancesAfter = await client.listInstances({
    appId: "http.flow.app",
    scope: ""
  });
  expect(instancesAfter.length).toBe(0);

  await client.dispose();
});

it("heartbeat / unregisterInstance 使用错误 instanceSessionToken 时应映射 forbidden", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-flow-mismatch-client",
    dataDir: getHost().dataDirectory
  });

  const registered = await client.registerInstance({
    instanceId: "http-flow-mismatch-inst-1",
    appId: "http.flow.app",
    scope: "",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  const mismatchedToken = `wrong-${registered.instanceSessionToken}`;

  await expect(client.heartbeat("http-flow-mismatch-inst-1", mismatchedToken)).rejects.toMatchObject({
    code: DevHubRpcErrorCode.Forbidden,
    reason: "instance_session_token_mismatch"
  });

  await expect(client.unregisterInstance("http-flow-mismatch-inst-1", mismatchedToken)).rejects.toMatchObject({
    code: DevHubRpcErrorCode.Forbidden,
    reason: "instance_session_token_mismatch"
  });

  await client.unregisterInstance("http-flow-mismatch-inst-1", registered.instanceSessionToken);
  await client.dispose();
});

it("getInstance 读取缺失实例时应透传 instance_not_found", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-get-instance-missing-client",
    dataDir: getHost().dataDirectory
  });

  await expect(client.getInstance("http-missing-inst-1")).rejects.toMatchObject({
    code: DevHubRpcErrorCode.InstanceNotFound,
    reason: "unknown_instance"
  });

  await client.dispose();
});

it("HTTP 链路应接受并原样回传 canonical mixed-case 标识符", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-canonical-identifiers-client",
    dataDir: getHost().dataDirectory
  });

  await getHost().writeDefinition({
    appId: "Sample.App",
    scope: "Workspace-A.v2",
    displayName: "Sample App Workspace"
  });

  try {
    const definition = await client.getDefinition({
      appId: "Sample.App",
      scope: "Workspace-A.v2"
    });
    expect(definition).toMatchObject({
      appId: "Sample.App",
      scope: "Workspace-A.v2",
      displayName: "Sample App Workspace"
    });

    const registered = await client.registerInstance({
      instanceId: "NODE_01.alpha",
      appId: "Sample.App",
      scope: "Workspace-A.v2",
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true
      }
    }, INSTANCE_PASSWORD);

    expect(registered).toMatchObject({
      instanceId: "NODE_01.alpha",
      appId: "Sample.App",
      scope: "Workspace-A.v2"
    });

    const instance = await client.getInstance("NODE_01.alpha");
    expect(instance).toMatchObject({
      instanceId: "NODE_01.alpha",
      appId: "Sample.App",
      scope: "Workspace-A.v2"
    });

    await client.unregisterInstance("NODE_01.alpha", registered.instanceSessionToken);
  } finally {
    await client.deleteDefinition({
      appId: "Sample.App",
      scope: "Workspace-A.v2"
    });
    await client.dispose();
  }
});

it("launch 应覆盖 started / starting / already_running", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "http-launch-client",
    dataDir: getHost().dataDirectory
  });

  const started = await client.launch({
    appId: "http.launch.started.app",
    scope: "",
    waitForRegisterMs: 0
  });
  expect(started.ok).toBe(true);
  expect(started.status).toBe("started");
  expect(started.pid).toBeGreaterThan(0);
  expect(started.launchId).toMatch(/^launch-/);

  const starting = await client.launch({
    appId: "http.launch.starting.app",
    scope: "",
    waitForRegisterMs: 200
  });
  expect(starting.ok).toBe(true);
  expect(starting.status).toBe("starting");
  expect(starting.pid).toBeGreaterThan(0);
  expect(starting.launchId).toMatch(/^launch-/);

  const registered = await client.registerInstance({
    instanceId: "http-launch-running-inst-1",
    appId: "http.launch.running.app",
    scope: "",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  const alreadyRunning = await client.launch({
    appId: "http.launch.running.app",
    scope: ""
  });
  expect(alreadyRunning.ok).toBe(true);
  expect(alreadyRunning.status).toBe("already_running");
  expect(alreadyRunning.pid).toBe(registered.pid);
  expect(alreadyRunning.launchId).toMatch(/^launch-/);

  await client.unregisterInstance("http-launch-running-inst-1", registered.instanceSessionToken);
  await client.dispose();
});

function createLaunchDefinition(appId: string): Record<string, unknown> {
  return {
    appId,
    scope: "",
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
