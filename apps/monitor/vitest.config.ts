import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { resolveMonitorSdkSourceConfig } from "./monitor-sdk-source";

const sdkSourceConfig = resolveMonitorSdkSourceConfig();

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: sdkSourceConfig.resolveAlias,
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    setupFiles: ["./src/test/setup.ts"],
  },
});
