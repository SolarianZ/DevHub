import { promises as fsPromises } from "node:fs";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
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

it("集成 Host 应写入独立目录树", async () => {
  const info = await discoverRuntime(host.runtimeDirectory);

  const [runtimeStat, definitionsStat, instancesStat, logsStat] = await Promise.all([
    fsPromises.stat(host.runtimeDirectory),
    fsPromises.stat(host.definitionsDirectory),
    waitForDirectory(host.instancesDirectory),
    waitForDirectory(host.logsDirectory)
  ]);

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
