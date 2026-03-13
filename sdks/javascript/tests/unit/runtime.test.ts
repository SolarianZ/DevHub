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

it("默认运行时目录应指向规范 runtime 目录", () => {
  delete process.env[ENV];

  const runtimeDir = resolveRuntimeDirectory();

  expect(path.basename(runtimeDir).toLowerCase()).toBe("runtime");
  expect(path.basename(path.dirname(runtimeDir))).toBe("DevHub");
});

it("discoverRuntime 应支持标准运行时根目录布局", async () => {
  const { runtimeRoot, runtimeDir } = await createStandardRuntimeLayout();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-std  \r\n", "utf-8");
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

  const result = await discoverRuntime(runtimeRoot);

  expect(result.runtimeDirectory).toBe(runtimeDir);
  expect(result.token).toBe("token-std");
  expect(result.rpcEndpoint).toBe("http://127.0.0.1:47231/rpc");
  expect(result.websocketEndpoint).toBe("ws://127.0.0.1:47231/ws");
  expect(result.runtime.startedAtUtc.toISOString()).toBe("2026-03-09T00:00:00.000Z");
});

it("discoverRuntime 应支持通过环境变量定位标准运行时根目录", async () => {
  const { runtimeRoot, runtimeDir } = await createStandardRuntimeLayout();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-env", "utf-8");
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
  process.env[ENV] = runtimeRoot;

  const result = await discoverRuntime();

  expect(result.runtimeDirectory).toBe(runtimeDir);
  expect(result.token).toBe("token-env");
});

it("discoverRuntime 应兼容旧布局并返回已修剪的连接信息", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
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
  const runtimeDir = await createLegacyRuntimeDirectory();
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

it("discoverRuntime 应拒绝非整数 pid", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12.5,
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

  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/pid/);
});

it("discoverRuntime 应拒绝非整数 runtimeTuning", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
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
      leaseSeconds: 30.5,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/runtimeTuning/);
});

it("discoverRuntime should reject a startedAtUtc value without an explicit timezone", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/startedAtUtc/);
});

it("discoverRuntime 应接受 IPv6 回环端点", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-ipv6", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://[::1]:47231",
    wsUrl: "ws://[::1]:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  const result = await discoverRuntime(runtimeDir);

  expect(result.rpcEndpoint).toBe("http://[::1]:47231/rpc");
  expect(result.websocketEndpoint).toBe("ws://[::1]:47231/ws");
});

it("discoverRuntime should reject a non-string hubVersion when present", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    hubVersion: 1,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/hubVersion/);
});

it("discoverRuntime should preserve a spec-valid empty hubVersion string", async () => {
  const runtimeDir = await createLegacyRuntimeDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    hubVersion: "",
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  const result = await discoverRuntime(runtimeDir);

  expect(result.runtime.hubVersion).toBe("");
});

async function createLegacyRuntimeDirectory(): Promise<string> {
  const runtimeDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-runtime-unit-"));
  tempRoots.push(runtimeDir);
  return runtimeDir;
}

async function createStandardRuntimeLayout(): Promise<{ runtimeRoot: string; runtimeDir: string }> {
  const runtimeRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-runtime-root-unit-"));
  const runtimeDir = path.join(runtimeRoot, "runtime");
  await fsPromises.mkdir(runtimeDir, { recursive: true });
  tempRoots.push(runtimeRoot);
  return { runtimeRoot, runtimeDir };
}

async function writeHubJson(runtimeDir: string, payload: Record<string, unknown>): Promise<void> {
  await fsPromises.writeFile(
    path.join(runtimeDir, "hub.json"),
    JSON.stringify(payload),
    "utf-8"
  );
}
