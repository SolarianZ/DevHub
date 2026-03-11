import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it } from "vitest";
import { discoverRuntime, resolveRuntimeDirectory } from "../../src/runtime.js";

const ENV = "DEVHUB_RUNTIME_DIR";
const originalEnvValue = process.env[ENV];
const tempRoots: string[] = [];

afterEach(() => {
  if (originalEnvValue === undefined) {
    delete process.env[ENV];
  } else {
    process.env[ENV] = originalEnvValue;
  }

  return Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("优先使用显式 override", () => {
  process.env[ENV] = path.join("temp", "runtime-env");
  const override = path.join("temp", "runtime-override");
  expect(resolveRuntimeDirectory(override)).toBe(path.resolve(override));
});

it("其次使用环境变量", () => {
  const envValue = path.join("temp", "runtime-env");
  process.env[ENV] = envValue;
  expect(resolveRuntimeDirectory()).toBe(path.resolve(envValue));
});

it("discoverRuntime 应返回已修剪的连接信息", async () => {
  const runtimeDir = await createRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1  \r\n", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  const result = await discoverRuntime(runtimeDir);

  expect(result.runtimeDirectory).toBe(runtimeDir);
  expect(result.token).toBe("token-1");
  expect(result.rpcEndpoint).toBe("http://127.0.0.1:47231/rpc");
  expect(result.websocketEndpoint).toBe("ws://127.0.0.1:47231/ws");
  expect(result.runtime.startedAtUtc.toISOString()).toBe("2026-03-09T00:00:00.000Z");
});

it("discoverRuntime 应拒绝非法 runtimeTuning", async () => {
  const runtimeDir = await createRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 0,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/runtimeTuning/);
});

async function createRuntimeDirectory(): Promise<string> {
  const runtimeDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-runtime-unit-"));
  tempRoots.push(runtimeDir);
  return runtimeDir;
}

async function writeHubJson(runtimeDir: string, payload: Record<string, unknown>): Promise<void> {
  await fsPromises.writeFile(
    path.join(runtimeDir, "hub.json"),
    JSON.stringify(payload),
    "utf-8"
  );
}
