import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
import { DevHubRpcError } from "../../src/errors.js";
import { APP_INSTANCE_REGISTERED } from "../../src/events.js";
import type { DevHubEventType } from "../../src/events.js";
import { DevHubHostFixture } from "./host.js";

let host: DevHubHostFixture;

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
}, 60_000);

afterAll(async () => {
  await host.close();
});

it("WS 认证 + 订阅/取消订阅应控制事件交付", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client",
    runtimeDir: host.runtimeDirectory
  });

  await eventsClient.authenticate();
  const subscriptionId = await eventsClient.subscribe([APP_INSTANCE_REGISTERED]);

  const iterator = eventsClient.readEvents()[Symbol.asyncIterator]();

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-http-client",
    runtimeDir: host.runtimeDirectory
  });

  await httpClient.registerInstance({
    instanceId: "events-inst-1",
    appId: "events.flow.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  });

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
  });

  await expect(nextWithTimeout(iterator, 600)).rejects.toThrow(/timeout/i);

  await httpClient.dispose();
  await eventsClient.dispose();
});

it("authenticated WS should support ping and apps queries", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-query-client",
    runtimeDir: host.runtimeDirectory
  });

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-query-http-client",
    runtimeDir: host.runtimeDirectory
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
    });

    const instances = await eventsClient.listInstances({
      appId: "events.flow.app"
    });
    expect(instances.some((instance) => instance.instanceId === "events-query-inst-1")).toBe(true);

    await httpClient.unregisterInstance("events-query-inst-1");
  } finally {
    await httpClient.dispose();
    await eventsClient.dispose();
  }
});

it("订阅未知事件类型应返回 invalid_params", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-invalid-client",
    runtimeDir: host.runtimeDirectory
  });

  await eventsClient.authenticate();

  let capturedError: unknown;
  try {
    await eventsClient.subscribe(["unknown.type" as unknown as DevHubEventType]);
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(-32602);
  expect(rpcError.message).toBe("invalid_params");

  await eventsClient.dispose();
});

it("断开后重连应需要重新订阅", async () => {
  const firstClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client-1",
    runtimeDir: host.runtimeDirectory
  });

  await firstClient.authenticate();
  await firstClient.subscribe([APP_INSTANCE_REGISTERED]);
  await firstClient.dispose();

  const secondClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-client-2",
    runtimeDir: host.runtimeDirectory
  });

  await secondClient.authenticate();

  const httpClient = await DevHubClient.fromRuntime({
    clientId: "events-reconnect-http-client",
    runtimeDir: host.runtimeDirectory
  });

  await httpClient.registerInstance({
    instanceId: "events-reconnect-inst-1",
    appId: "events.reconnect.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  });

  await secondClient.subscribe([APP_INSTANCE_REGISTERED]);

  await httpClient.registerInstance({
    instanceId: "events-reconnect-inst-2",
    appId: "events.reconnect.app",
    pid: process.pid,
    invoke: {
      poll: true,
      respond: true
    }
  });

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
