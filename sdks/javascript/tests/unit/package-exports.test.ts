import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { afterEach, expect, it } from "vitest";
import * as sdk from "../../src/index.js";
import {
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  SUPPORTED_EVENT_TYPES
} from "../../src/events.js";
import { JsonRpcHttpTransport } from "../../src/http-transport.js";
import { FileSystemRuntimeResolver } from "../../src/runtime.js";
import { JsonRpcWsSession } from "../../src/ws-session.js";

const execFileAsync = promisify(execFile);
const tempRoots: string[] = [];

afterEach(async () => {
  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await rm(target, { recursive: true, force: true });
  }));
});

it("顶层入口应导出高级扩展点", () => {
  expect(sdk.JsonRpcHttpTransport).toBe(JsonRpcHttpTransport);
  expect(sdk.JsonRpcWsSession).toBe(JsonRpcWsSession);
  expect(sdk.DevHubConnectionError).toBeTypeOf("function");
  expect(sdk.SDK_VERSION).toBeTypeOf("string");
  expect((sdk as Record<string, unknown>).FileSystemRuntimeResolver).toBeUndefined();
  expect(sdk.SUPPORTED_EVENT_TYPES).toBe(SUPPORTED_EVENT_TYPES);
  expect(sdk.APP_INSTANCE_REGISTERED).toBe(APP_INSTANCE_REGISTERED);
  expect(sdk.APP_DEFINITION_UPSERTED).toBe(APP_DEFINITION_UPSERTED);
  expect(FileSystemRuntimeResolver).toBeTypeOf("function");
});

it("package exports 应为 runtime 提供显式子路径", async () => {
  const packageJsonUrl = new URL("../../package.json", import.meta.url);
  const packageJson = JSON.parse(await readFile(packageJsonUrl, "utf-8")) as {
    version?: string;
    exports?: Record<string, { default?: string; types?: string }>;
  };

  expect(sdk.SDK_VERSION).toBe(packageJson.version);
  expect(packageJson.exports?.["."]).toEqual({
    types: "./dist/index.d.ts",
    default: "./dist/index.js"
  });
  expect(packageJson.exports?.["./runtime"]).toEqual({
    types: "./dist/runtime.d.ts",
    default: "./dist/runtime.js"
  });
});

it("构建后的根入口应保持浏览器安全", async () => {
  const outDir = await buildPackageToTempDist();
  const distIndex = await readFile(path.join(outDir, "index.js"), "utf-8");

  expect(distIndex).not.toContain("./runtime.js");
  expect(distIndex).not.toMatch(/["']node:[^"']+["']/);
});

it("源码根入口不得静态解析 Node 专用 ws 依赖", async () => {
  const wsSessionUrl = new URL("../../src/ws-session.ts", import.meta.url);
  const wsSessionSource = await readFile(wsSessionUrl, "utf-8");

  expect(wsSessionSource).not.toMatch(/import\s*\(\s*["']ws["']\s*\)/);
  expect(wsSessionSource).not.toMatch(/from\s+["']ws["']/);
  expect(wsSessionSource).not.toContain("@types/ws");
});

async function buildPackageToTempDist(): Promise<string> {
  const tempRoot = await mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-dist-unit-"));
  tempRoots.push(tempRoot);

  const outDir = path.join(tempRoot, "dist");
  const tsconfigPath = path.join(tempRoot, "tsconfig.build.json");
  const sdkRoot = new URL("../..", import.meta.url);
  const sdkRootPath = fileURLToPath(sdkRoot);
  const tscPath = new URL("../../node_modules/typescript/bin/tsc", import.meta.url);

  await writeFile(
    tsconfigPath,
    JSON.stringify({
      extends: fileURLToPath(new URL("../../tsconfig.build.json", import.meta.url)),
      compilerOptions: {
        outDir,
        typeRoots: [
          path.join(sdkRootPath, "node_modules", "@types")
        ]
      }
    }),
    "utf-8"
  );

  await execFileAsync(process.execPath, [fileURLToPath(tscPath), "-p", tsconfigPath], {
    cwd: sdkRootPath,
    maxBuffer: 10 * 1024 * 1024
  });

  return outDir;
}
