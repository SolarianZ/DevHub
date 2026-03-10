import path from "node:path";
import { afterEach, expect, it } from "vitest";
import { resolveRuntimeDirectory } from "../../src/runtime.js";

const ENV = "DEVHUB_RUNTIME_DIR";
const originalEnvValue = process.env[ENV];

afterEach(() => {
  if (originalEnvValue === undefined) {
    delete process.env[ENV];
  } else {
    process.env[ENV] = originalEnvValue;
  }
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
