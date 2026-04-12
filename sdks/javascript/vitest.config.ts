import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["tests/**/*.test.ts"],
    // 集成测试会启动真实 Host；串行执行文件可降低资源占用并保持诊断日志稳定。
    fileParallelism: false,
    testTimeout: 60_000
  }
});
