import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
import {
  APP_DEFINITION_DELETED,
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED
} from "../../src/events.js";
import type { DevHubEventType } from "../../src/events.js";
import { DevHubHostFixture } from "./host.js";

const INSTANCE_PASSWORD = "events-flow-password";

let host: DevHubHostFixture | undefined;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "events.flow.app",
    displayName: "events.flow.app"
  });
  await host.writeDefinition({
    appId: "events.reconnect.app",
    displayName: "events.reconnect.app"
  });
}, 120_000);

afterAll(async () => {
  await host?.close();
});

it("WS 认证 + 订阅/取消订阅应控制事件交付", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client",
    dataDir: getHost().dataDirectory
  });

  await eventsClient.authenticate();
  const subscriptionId = await eventsClient.subscribe([APP_INSTANCE_REGISTERED]);

  const iterator = eventsClient.readEvents()[Symbol.asyncIterator]();

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-http-client",
    dataDir: getHost().dataDirectory
  });

  await httpClient.registerInstance({
    instanceId: "events-inst-1",
    appId: "events.flow.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  const first = await nextWithTimeout(iterator, 5_000);
  expect(first.done).toBe(false);
  expect(first.value.subscriptionId).toBe(subscriptionId);
  expect(first.value.type).toBe(APP_INSTANCE_REGISTERED);

  await eventsClient.unsubscribe(subscriptionId);

  await httpClient.registerInstance({
    instanceId: "events-inst-2",
    appId: "events.flow.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  await expect(nextWithTimeout(iterator, 600)).rejects.toThrow(/timeout/i);

  await httpClient.dispose();
  await eventsClient.dispose();
});

it("定义变更事件应可订阅并携带最新载荷", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "definition-events-client",
    dataDir: getHost().dataDirectory
  });

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "definition-events-http-client",
    dataDir: getHost().dataDirectory
  });

  try {
    await eventsClient.authenticate();
    const subscriptionId = await eventsClient.subscribe([
      APP_DEFINITION_UPSERTED,
      APP_DEFINITION_DELETED
    ]);

    const iterator = eventsClient.readEvents()[Symbol.asyncIterator]();

    await httpClient.upsertDefinition({
      appId: "events.managed.app",
      displayName: "Events Managed App",
      description: "用于事件定义变更集成测试。",
      capabilities: {
        rpc: true,
        events: false
      },
      launch: {
        exePath: process.execPath
      }
    });

    const upserted = await nextWithTimeout(iterator, 5_000);
    expect(upserted.done).toBe(false);
    expect(upserted.value.subscriptionId).toBe(subscriptionId);
    expect(upserted.value.type).toBe(APP_DEFINITION_UPSERTED);
    expect(upserted.value.payload?.appId).toBe("events.managed.app");
    expect((upserted.value.payload?.definition as { displayName?: string }).displayName).toBe("Events Managed App");

    await httpClient.deleteDefinition("events.managed.app");

    const deleted = await nextWithTimeout(iterator, 5_000);
    expect(deleted.done).toBe(false);
    expect(deleted.value.subscriptionId).toBe(subscriptionId);
    expect(deleted.value.type).toBe(APP_DEFINITION_DELETED);
    expect(deleted.value.payload?.appId).toBe("events.managed.app");
  } finally {
    await httpClient.dispose();
    await eventsClient.dispose();
  }
});

it("authenticated WS should support ping and apps queries", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-query-client",
    dataDir: getHost().dataDirectory
  });

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-query-http-client",
    dataDir: getHost().dataDirectory
  });

  try {
    await eventsClient.authenticate();

    const ping = await eventsClient.ping({
      channel: "ws"
    });
    expect(ping.ok).toBe(true);
    expect(ping.echo).toEqual({
      channel: "ws"
    });

    const definitions = await eventsClient.listDefinitions();
    expect(definitions.some((definition) => definition.appId === "events.flow.app")).toBe(true);

    const definition = await eventsClient.getDefinition("events.flow.app");
    expect(definition.displayName).toBe("events.flow.app");

    await httpClient.registerInstance({
      instanceId: "events-query-inst-1",
      appId: "events.flow.app",
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true
      }
    }, INSTANCE_PASSWORD);

    const instances = await eventsClient.listInstances({
      appId: "events.flow.app"
    });
    expect(instances.some((instance) => instance.instanceId === "events-query-inst-1")).toBe(true);

    await httpClient.unregisterInstance("events-query-inst-1", INSTANCE_PASSWORD);
  } finally {
    await httpClient.dispose();
    await eventsClient.dispose();
  }
});

it("订阅未知事件类型应在客户端本地被拒绝", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-invalid-client",
    dataDir: getHost().dataDirectory
  });

  await eventsClient.authenticate();

  let capturedError: unknown;
  try {
    await eventsClient.subscribe(["unknown.type" as unknown as DevHubEventType]);
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(Error);
  expect((capturedError as Error).message).toMatch(/supported DevHub event type/i);

  await eventsClient.dispose();
});

it("断开后重连应需要重新订阅", async () => {
  const firstClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client-1",
    dataDir: getHost().dataDirectory
  });

  await firstClient.authenticate();
  await firstClient.subscribe([APP_INSTANCE_REGISTERED]);
  await firstClient.dispose();

  const secondClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client-2",
    dataDir: getHost().dataDirectory
  });

  await secondClient.authenticate();

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-reconnect-http-client",
    dataDir: getHost().dataDirectory
  });

  await httpClient.registerInstance({
    instanceId: "events-reconnect-inst-1",
    appId: "events.reconnect.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  await secondClient.subscribe([APP_INSTANCE_REGISTERED]);

  await httpClient.registerInstance({
    instanceId: "events-reconnect-inst-2",
    appId: "events.reconnect.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  }, INSTANCE_PASSWORD);

  const iterator = secondClient.readEvents()[Symbol.asyncIterator]();
  const delivered = await nextWithTimeout(iterator, 5_000);
  expect(delivered.done).toBe(false);
  expect(delivered.value.type).toBe(APP_INSTANCE_REGISTERED);
  expect(delivered.value.payload?.instanceId).toBe("events-reconnect-inst-2");

  await httpClient.dispose();
  await secondClient.dispose();
});

async function nextWithTimeout<T>(iterator: AsyncIterator<T>, timeoutMs: number): Promise<IteratorResult<T>> {
  return await Promise.race([
    iterator.next(),
    new Promise<IteratorResult<T>>((_, reject) => {
      setTimeout(() => reject(new Error("timeout")), timeoutMs);
    })
  ]);
}

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture not started.");
  }

  return host;
}
