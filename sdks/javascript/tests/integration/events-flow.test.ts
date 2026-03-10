import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient, DevHubEventsClient } from "../../src/client.js";
import { DevHubRpcError } from "../../src/errors.js";
import { APP_INSTANCE_REGISTERED } from "../../src/events.js";
import { DevHubHostFixture } from "./host.js";

let host: DevHubHostFixture;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "events.flow.app",
    displayName: "events.flow.app"
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

it("订阅未知事件类型应返回 invalid_params", async () => {
  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "events-invalid-client",
    runtimeDir: host.runtimeDirectory
  });

  await eventsClient.authenticate();

  let capturedError: unknown;
  try {
    await eventsClient.subscribe(["unknown.type"]);
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(-32602);
  expect(rpcError.message).toBe("invalid_params");

  await eventsClient.dispose();
});

async function nextWithTimeout<T>(iterator: AsyncIterator<T>, timeoutMs: number): Promise<IteratorResult<T>> {
  return await Promise.race([
    iterator.next(),
    new Promise<IteratorResult<T>>((_, reject) => {
      setTimeout(() => reject(new Error("timeout")), timeoutMs);
    })
  ]);
}
