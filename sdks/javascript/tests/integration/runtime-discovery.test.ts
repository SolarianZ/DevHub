import { promises as fsPromises } from "node:fs";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { APP_INSTANCE_REGISTERED, DevHubEventsClient } from "../../src/events.js";
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
  const info = await discoverRuntime(host.dataDirectory);
  expect(info.runtime.protocolVersion).toBe(1);
  expect(info.runtime.pid).toBeGreaterThan(0);
  expect(info.runtime.httpBaseUrl).toMatch(/^https?:\/\//);
  expect(info.runtime.wsUrl).toMatch(/^wss?:\/\//);
  expect(info.token.length).toBeGreaterThan(0);
  expect(info.rpcEndpoint).toBe(`${info.runtime.httpBaseUrl}/rpc`);
  expect(info.websocketEndpoint).toBe(info.runtime.wsUrl);
});

it("集成 Host 应写入独立目录树", async () => {
  const info = await discoverRuntime(host.dataDirectory);

  const [dataDirStat, runtimeStat, definitionsStat, instancesStat, logsStat] = await Promise.all([
    fsPromises.stat(host.dataDirectory),
    fsPromises.stat(host.runtimeDirectory),
    fsPromises.stat(host.definitionsDirectory),
    waitForDirectory(host.instancesDirectory),
    waitForDirectory(host.logsDirectory)
  ]);

  expect(dataDirStat.isDirectory()).toBe(true);
  expect(runtimeStat.isDirectory()).toBe(true);
  expect(definitionsStat.isDirectory()).toBe(true);
  expect(instancesStat.isDirectory()).toBe(true);
  expect(logsStat.isDirectory()).toBe(true);
  expect(path.dirname(info.runtime.tokenFile)).toBe(host.runtimeDirectory);

  const logFiles = await waitForLogFiles(host.logsDirectory);
  expect(logFiles.some((file) => file.endsWith(".log"))).toBe(true);
});

it("fromRuntime 应构造客户端连接", async () => {
  const client = await DevHubClient.fromRuntime({
    clientId: "integration-client",
    dataDir: host.dataDirectory
  });

  const eventsClient = await DevHubEventsClient.fromRuntime({
    clientId: "integration-events-client",
    dataDir: host.dataDirectory
  });

  try {
    expect(client.connection.runtimeDirectory).toBe(host.runtimeDirectory);
    expect(eventsClient.connection.runtimeDirectory).toBe(host.runtimeDirectory);
    expect(client.connection.token.length).toBeGreaterThan(0);
    expect(eventsClient.connection.token.length).toBeGreaterThan(0);
  } finally {
    await client.dispose();
    await eventsClient.dispose();
  }
});

it("不同 dataDir 下的 Host 应并行隔离 HTTP 与 Events 链路", async () => {
  const firstHost = await DevHubHostFixture.start();
  const secondHost = await DevHubHostFixture.start();

  try {
    await Promise.all([
      firstHost.writeDefinition({
        appId: "parallel.flow.app",
        displayName: "parallel.flow.app"
      }),
      secondHost.writeDefinition({
        appId: "parallel.flow.app",
        displayName: "parallel.flow.app"
      })
    ]);

    const [firstInfo, secondInfo] = await Promise.all([
      discoverRuntime(firstHost.dataDirectory),
      discoverRuntime(secondHost.dataDirectory)
    ]);

    expect(firstInfo.runtimeDirectory).toBe(firstHost.runtimeDirectory);
    expect(secondInfo.runtimeDirectory).toBe(secondHost.runtimeDirectory);
    expect(firstInfo.rpcEndpoint).not.toBe(secondInfo.rpcEndpoint);
    expect(firstInfo.runtime.tokenFile).not.toBe(secondInfo.runtime.tokenFile);

    const firstClient = await DevHubClient.fromRuntime({
      clientId: "parallel-http-client-1",
      dataDir: firstHost.dataDirectory
    });
    const secondClient = await DevHubClient.fromRuntime({
      clientId: "parallel-http-client-2",
      dataDir: secondHost.dataDirectory
    });
    const firstEventsClient = await DevHubEventsClient.fromRuntime({
      clientId: "parallel-events-client-1",
      dataDir: firstHost.dataDirectory
    });
    const secondEventsClient = await DevHubEventsClient.fromRuntime({
      clientId: "parallel-events-client-2",
      dataDir: secondHost.dataDirectory
    });

    try {
      await Promise.all([
        firstEventsClient.authenticate(),
        secondEventsClient.authenticate()
      ]);

      await Promise.all([
        firstEventsClient.subscribe([APP_INSTANCE_REGISTERED]),
        secondEventsClient.subscribe([APP_INSTANCE_REGISTERED])
      ]);

      const firstIterator = firstEventsClient.readEvents()[Symbol.asyncIterator]();
      const secondIterator = secondEventsClient.readEvents()[Symbol.asyncIterator]();

      await firstClient.registerInstance({
        instanceId: "parallel-inst-1",
        appId: "parallel.flow.app",
        pid: process.pid,
        invoke: {
          poll: true,
          respond: true
        }
      });

      const firstDelivered = await nextWithTimeout(firstIterator, 5_000);
      expect(firstDelivered.done).toBe(false);
      expect(firstDelivered.value.payload?.instanceId).toBe("parallel-inst-1");

      const secondInstancesBefore = await secondClient.listInstances({
        appId: "parallel.flow.app"
      });
      expect(secondInstancesBefore).toHaveLength(0);

      await secondClient.registerInstance({
        instanceId: "parallel-inst-2",
        appId: "parallel.flow.app",
        pid: process.pid,
        invoke: {
          poll: true,
          respond: true
        }
      });

      const secondDelivered = await nextWithTimeout(secondIterator, 5_000);
      expect(secondDelivered.done).toBe(false);
      expect(secondDelivered.value.payload?.instanceId).toBe("parallel-inst-2");
      await expect(nextWithTimeout(firstIterator, 600)).rejects.toThrow(/timeout/i);

      const firstInstances = await firstClient.listInstances({
        appId: "parallel.flow.app"
      });
      const secondInstances = await secondClient.listInstances({
        appId: "parallel.flow.app"
      });

      expect(firstInstances.map((item) => item.instanceId)).toEqual(["parallel-inst-1"]);
      expect(secondInstances.map((item) => item.instanceId)).toEqual(["parallel-inst-2"]);
    } finally {
      await Promise.all([
        firstClient.dispose(),
        secondClient.dispose(),
        firstEventsClient.dispose(),
        secondEventsClient.dispose()
      ]);
    }
  } finally {
    await Promise.all([
      firstHost.close(),
      secondHost.close()
    ]);
  }
}, 120_000);

async function waitForDirectory(target: string): Promise<Awaited<ReturnType<typeof fsPromises.stat>>> {
  const deadline = Date.now() + 10_000;

  while (Date.now() < deadline) {
    try {
      return await fsPromises.stat(target);
    } catch {
      await delay(250);
    }
  }

  return await fsPromises.stat(target);
}

async function waitForLogFiles(logsDirectory: string): Promise<string[]> {
  const deadline = Date.now() + 10_000;

  while (Date.now() < deadline) {
    try {
      const entries = await fsPromises.readdir(logsDirectory);
      if (entries.length > 0) {
        return entries;
      }
    } catch {
    }

    await delay(250);
  }

  return await fsPromises.readdir(logsDirectory);
}

async function nextWithTimeout<T>(iterator: AsyncIterator<T>, timeoutMs: number): Promise<IteratorResult<T>> {
  return await Promise.race([
    iterator.next(),
    new Promise<IteratorResult<T>>((_, reject) => {
      setTimeout(() => reject(new Error("timeout")), timeoutMs);
    })
  ]);
}
