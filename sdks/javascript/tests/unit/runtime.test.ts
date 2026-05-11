import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it } from "vitest";
import { discoverRuntime, resolveDataDirectory } from "../../src/runtime.js";

const DATA_DIR_ENV = "DEVHUB_DATA_DIR";
const originalDataDirEnvValue = process.env[DATA_DIR_ENV];
const tempRoots: string[] = [];

afterEach(() => {
  if (originalDataDirEnvValue === undefined) {
    delete process.env[DATA_DIR_ENV];
  } else {
    process.env[DATA_DIR_ENV] = originalDataDirEnvValue;
  }

  return Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("优先使用显式 dataDir override", () => {
  process.env[DATA_DIR_ENV] = path.join("temp", "data-env");
  const override = path.join("temp", "data-override");
  expect(resolveDataDirectory(override)).toBe(path.resolve(override));
});

it("其次使用 DEVHUB_DATA_DIR", () => {
  const envValue = path.join("temp", "data-env");
  process.env[DATA_DIR_ENV] = envValue;
  expect(resolveDataDirectory()).toBe(path.resolve(envValue));
});

it("默认数据目录应指向规范 data dir", () => {
  delete process.env[DATA_DIR_ENV];

  const dataDir = resolveDataDirectory();

  expect(path.basename(dataDir)).toBe("DevHub");
  expect(path.basename(dataDir).toLowerCase()).not.toBe("runtime");
});

it("discoverRuntime 应支持标准 dataDir 布局", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  const result = await discoverRuntime(dataDir);

  expect(result.runtimeDirectory).toBe(runtimeDir);
  expect(result.token).toBe("token-std");
  expect(result.rpcEndpoint).toBe("http://127.0.0.1:47231/rpc");
  expect(result.websocketEndpoint).toBe("ws://127.0.0.1:47231/ws");
  expect(result.runtime.startedAtUtc.toISOString()).toBe("2026-03-09T00:00:00.000Z");
});

it("discoverRuntime 应支持通过 DEVHUB_DATA_DIR 定位 dataDir", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });
  process.env[DATA_DIR_ENV] = dataDir;

  const result = await discoverRuntime();

  expect(result.runtimeDirectory).toBe(runtimeDir);
  expect(result.token).toBe("token-env");
});

it("discoverRuntime 应拒绝 dataDir 根目录直放 hub.json/token.txt 的旧布局", async () => {
  const dataDir = await createTempRoot("devhub-js-sdk-runtime-legacy-root-unit-");
  const tokenFile = path.join(dataDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-legacy", "utf-8");
  await writeHubJson(dataDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/dataDir 必须指向数据根目录/);
});

it("discoverRuntime 应拒绝仅在 dataDir 根目录直放 token.txt 的旧布局", async () => {
  const dataDir = await createTempRoot("devhub-js-sdk-runtime-legacy-token-root-unit-");
  await fsPromises.writeFile(path.join(dataDir, "token.txt"), "token-legacy", "utf-8");

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/dataDir 必须指向数据根目录/);
});

it("discoverRuntime 应拒绝直接传入 runtime 子目录", async () => {
  const { runtimeDir } = await createPopulatedDataDirectory();
  await expect(discoverRuntime(runtimeDir)).rejects.toThrow(/dataDir 必须指向数据根目录/);
});

it("discoverRuntime 应拒绝非法 runtimeTuning", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/runtimeTuning/);
});

it("discoverRuntime 应拒绝缺少 launchRegisterTimeoutSeconds 的 runtimeTuning", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/runtimeTuning/);
});

it("discoverRuntime 应拒绝非整数 pid", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/pid/);
});

it("discoverRuntime 应拒绝非整数 runtimeTuning", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/runtimeTuning/);
});

it("discoverRuntime should reject a startedAtUtc value without an explicit timezone", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/startedAtUtc/);
});

it("discoverRuntime 应接受 IPv6 回环端点", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  const result = await discoverRuntime(dataDir);

  expect(result.rpcEndpoint).toBe("http://[::1]:47231/rpc");
  expect(result.websocketEndpoint).toBe("ws://[::1]:47231/ws");
});

it("discoverRuntime 应拒绝规范外的 IPv4 loopback 端点", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-ipv4", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.2:47231",
    wsUrl: "wss://127.0.0.2:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/hub\.json\.httpBaseUrl/);
});

it.each([
  "ws://127.0.0.1:47231/ws?debug=true",
  "ws://127.0.0.1:47231/ws?",
  "ws://127.0.0.1:47231/ws#events",
  "ws://127.0.0.1:47231/ws#",
  " ws://127.0.0.1:47231/ws",
  "ws://127.0.0.1:47231/ws ",
  "ws://user:pass@127.0.0.1:47231/ws",
  "ws://127.0.0.1:47231/events",
  "ws://127.0.0.1:47231/ws/",
  "http://127.0.0.1:47231/ws",
  "ws://192.168.1.10:47231/ws"
])("discoverRuntime 应拒绝非法 wsUrl 且不重写端点: %s", async (wsUrl) => {
  const { dataDir, runtimeDir } = await createDataDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-ws", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl: "http://127.0.0.1:47231",
    wsUrl,
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/hub\.json\.wsUrl/);
});

it.each([
  "http://127.0.0.1:47231/",
  "http://127.0.0.1:47231/rpc",
  "http://127.0.0.1:47231?debug=true",
  "http://127.0.0.1:47231#runtime",
  "http://user:pass@127.0.0.1:47231"
])("discoverRuntime 应拒绝非 origin 形式的 httpBaseUrl: %s", async (httpBaseUrl) => {
  const { dataDir, runtimeDir } = await createDataDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await writeHubJson(runtimeDir, {
    protocolVersion: 1,
    pid: 12345,
    httpBaseUrl,
    wsUrl: "ws://127.0.0.1:47231/ws",
    tokenFile,
    startedAtUtc: "2026-03-09T00:00:00Z",
    runtimeTuning: {
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(/httpBaseUrl/);
});

it("discoverRuntime should reject a non-string hubVersion when present", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
  const tokenFile = path.join(runtimeDir, "token.txt");
  const hubJsonPath = path.join(runtimeDir, "hub.json");
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  await expect(discoverRuntime(dataDir)).rejects.toThrow(`hub.json.hubVersion 非法：${hubJsonPath}`);
});

it("discoverRuntime should preserve a spec-valid empty hubVersion string", async () => {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  const result = await discoverRuntime(dataDir);

  expect(result.runtime.hubVersion).toBe("");
});

async function createPopulatedDataDirectory(): Promise<{ dataDir: string; runtimeDir: string }> {
  const { dataDir, runtimeDir } = await createDataDirectory();
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
      leaseSeconds: 30,
      onlineThresholdSeconds: 30,
      launchDedupeWindowSeconds: 30,
      launchRegisterTimeoutSeconds: 30
    }
  });

  return { dataDir, runtimeDir };
}

async function createDataDirectory(): Promise<{ dataDir: string; runtimeDir: string }> {
  const dataDir = await createTempRoot("devhub-js-sdk-data-root-unit-");
  const runtimeDir = path.join(dataDir, "runtime");
  await fsPromises.mkdir(runtimeDir, { recursive: true });
  return { dataDir, runtimeDir };
}

async function createTempRoot(prefix: string): Promise<string> {
  const target = await fsPromises.mkdtemp(path.join(os.tmpdir(), prefix));
  tempRoots.push(target);
  return target;
}

async function writeHubJson(runtimeDir: string, payload: Record<string, unknown>): Promise<void> {
  await fsPromises.writeFile(
    path.join(runtimeDir, "hub.json"),
    JSON.stringify(payload),
    "utf-8"
  );
}
