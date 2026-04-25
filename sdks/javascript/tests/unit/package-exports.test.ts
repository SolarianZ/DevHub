import { readFile } from "node:fs/promises";
import { expect, it } from "vitest";
import * as sdk from "../../src/index.js";
import {
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  SUPPORTED_EVENT_TYPES
} from "../../src/events.js";
import { JsonRpcHttpTransport } from "../../src/http-transport.js";
import { FileSystemRuntimeResolver } from "../../src/runtime.js";
import { JsonRpcWsSession } from "../../src/ws-session.js";

it("顶层入口应导出高级扩展点", () => {
  expect(sdk.JsonRpcHttpTransport).toBe(JsonRpcHttpTransport);
  expect(sdk.JsonRpcWsSession).toBe(JsonRpcWsSession);
  expect(sdk.DevHubConnectionError).toBeTypeOf("function");
  expect((sdk as Record<string, unknown>).FileSystemRuntimeResolver).toBeUndefined();
  expect(sdk.SUPPORTED_EVENT_TYPES).toBe(SUPPORTED_EVENT_TYPES);
  expect(sdk.APP_INSTANCE_REGISTERED).toBe(APP_INSTANCE_REGISTERED);
  expect(sdk.APP_DEFINITION_UPSERTED).toBe(APP_DEFINITION_UPSERTED);
  expect(FileSystemRuntimeResolver).toBeTypeOf("function");
});

it("package exports 应为 runtime 提供显式子路径", async () => {
  const packageJsonUrl = new URL("../../package.json", import.meta.url);
  const packageJson = JSON.parse(await readFile(packageJsonUrl, "utf-8")) as {
    exports?: Record<string, { default?: string; types?: string }>;
  };

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
  const distIndexUrl = new URL("../../dist/index.js", import.meta.url);
  const distIndex = await readFile(distIndexUrl, "utf-8");

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
