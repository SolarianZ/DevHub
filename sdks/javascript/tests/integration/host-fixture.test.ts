import { spawnSync } from "node:child_process";
import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import {
  DevHubHostFixture,
  resolveConfiguredHostAssemblyFromEnvironment,
  resolveHostAssemblyPath
} from "./host.js";

const longRunningLaunchScriptPath = fileURLToPath(new URL("../assets/launch_wait_forever.mjs", import.meta.url));

it("close 应回收 Host 进程树并清理临时目录", async () => {
  const host = await DevHubHostFixture.start();
  const dataDirectory = host.dataDirectory;
  const hostProcessId = host.processId;
  let launchedProcessId: number | undefined;

  try {
    await host.writeDefinition({
      appId: "host.cleanup.app",
      scope: "",
      displayName: "host.cleanup.app",
      launch: {
        exePath: process.execPath,
        argsTemplate: quoteCommandArgument(path.normalize(longRunningLaunchScriptPath))
      }
    });

    const client = await DevHubClient.fromRuntime({
      clientId: "host-cleanup-client",
      dataDir: host.dataDirectory
    });

    try {
      const launchResult = await client.launch({
        appId: "host.cleanup.app",
        scope: "",
        waitForRegisterMs: 0
      });

      expect(launchResult.status).toBe("started");
      expect(launchResult.pid).toBeGreaterThan(0);

      if (launchResult.pid === null || launchResult.pid === undefined) {
        throw new Error("launch 应返回子进程 PID。");
      }

      launchedProcessId = launchResult.pid;
      await waitForProcessState(launchedProcessId, true);
    } finally {
      await client.dispose();
    }
  } finally {
    await host.close();
  }

  expect(hostProcessId).toBeGreaterThan(0);
  expect(await pathExists(dataDirectory)).toBe(false);

  if (hostProcessId !== null) {
    await waitForProcessState(hostProcessId, false);
  }

  if (launchedProcessId !== undefined) {
    await waitForProcessState(launchedProcessId, false);
  }
}, 120_000);

it("writeDefinition 应按 appId + scope 生成复合键文件名并写入规范化 scope", async () => {
  const host = await DevHubHostFixture.start();

  try {
    await host.writeDefinition({
      appId: "fixture.scope.app",
      scope: "",
      displayName: "fixture.scope.app.global"
    });
    await host.writeDefinition({
      appId: "fixture.scope.app",
      scope: "workspace-A",
      displayName: "fixture.scope.app.workspace-A"
    });

    const fileNames = await fsPromises.readdir(host.definitionsDirectory);
    expect(fileNames).toContain("fixture.scope.app--global.json");
    expect(fileNames).toContain("fixture.scope.app--776F726B73706163652D41.json");

    const globalDefinition = JSON.parse(
      await fsPromises.readFile(
        path.join(host.definitionsDirectory, "fixture.scope.app--global.json"),
        "utf-8"
      )
    ) as { appId: string; scope: string; displayName: string };

    expect(globalDefinition).toEqual({
      appId: "fixture.scope.app",
      scope: "",
      displayName: "fixture.scope.app.global"
    });
  } finally {
    await host.close();
  }
}, 120_000);

it("共享预构建 Host 路径应可直接复用而不触发本地构建", async () => {
  const repoRoot = await createFakeRepositoryRoot();

  try {
    const configuredHostAssemblyPath = await createConfiguredHostAssembly(
      repoRoot,
      path.join("shared", "DevHub.Host.dll")
    );

    const resolvedPath = await resolveHostAssemblyPath(repoRoot, {
      DEVHUB_SDK_HOST_ASSEMBLY: path.join("shared", "DevHub.Host.dll")
    });

    expect(resolvedPath).toBe(configuredHostAssemblyPath);
  } finally {
    await fsPromises.rm(repoRoot, { recursive: true, force: true });
  }
});

it("JS 专用 Host 覆盖变量应优先于共享变量", async () => {
  const repoRoot = await createFakeRepositoryRoot();

  try {
    const sharedHostAssemblyPath = await createConfiguredHostAssembly(
      repoRoot,
      path.join("shared", "DevHub.Host.dll")
    );
    const jsHostAssemblyPath = await createConfiguredHostAssembly(
      repoRoot,
      path.join("js", "DevHub.Host.dll")
    );

    const resolvedPath = await resolveConfiguredHostAssemblyFromEnvironment(repoRoot, {
      DEVHUB_SDK_HOST_ASSEMBLY: sharedHostAssemblyPath,
      DEVHUB_JS_SDK_HOST_ASSEMBLY: jsHostAssemblyPath
    });

    expect(resolvedPath).toBe(jsHostAssemblyPath);
  } finally {
    await fsPromises.rm(repoRoot, { recursive: true, force: true });
  }
});

async function waitForProcessState(pid: number, expectedRunning: boolean): Promise<void> {
  const deadline = Date.now() + 15_000;

  while (Date.now() < deadline) {
    if (isProcessRunning(pid) === expectedRunning) {
      return;
    }

    await delay(250);
  }

  expect(isProcessRunning(pid)).toBe(expectedRunning);
}

function isProcessRunning(pid: number): boolean {
  if (process.platform === "win32") {
    const query = spawnSync("tasklist", ["/FI", `PID eq ${pid}`], {
      encoding: "utf-8",
      windowsHide: true
    });

    if (query.error) {
      throw query.error;
    }

    return query.stdout.split(/\r?\n/).some((line) => line.includes(` ${pid} `));
  }

  const query = spawnSync("ps", ["-p", String(pid), "-o", "pid="], {
    encoding: "utf-8"
  });
  if (query.error) {
    throw query.error;
  }

  return query.stdout.trim() === String(pid);
}

async function pathExists(target: string): Promise<boolean> {
  try {
    await fsPromises.access(target);
    return true;
  } catch {
    return false;
  }
}

function quoteCommandArgument(value: string): string {
  return value.includes(" ") ? `"${value}"` : value;
}

async function createFakeRepositoryRoot(): Promise<string> {
  return await fsPromises.realpath(
    await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-host-fixture-"))
  );
}

async function createConfiguredHostAssembly(repoRoot: string, relativePath: string): Promise<string> {
  const hostAssemblyPath = path.join(repoRoot, relativePath);
  await fsPromises.mkdir(path.dirname(hostAssemblyPath), { recursive: true });
  await fsPromises.writeFile(hostAssemblyPath, "", "utf-8");
  return hostAssemblyPath;
}
