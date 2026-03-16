import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["tests/**/*.test.ts"],
    // 集成测试会按当前源码构建隔离 Host，文件级并发会争用 .NET 中间产物。
    fileParallelism: false,
    testTimeout: 60_000
  }
});
