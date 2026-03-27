import { spawn, spawnSync } from "node:child_process";
import { once } from "node:events";
import { existsSync } from "node:fs";
import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const OUTPUT_LIMIT = 200;
const PrebuiltHostAssemblyEnvironmentVariable = "DEVHUB_JS_SDK_HOST_ASSEMBLY";
let sharedHostAssemblyPromise: Promise<string> | undefined;

export class DevHubHostFixture {
  readonly repoRoot: string;
  readonly dataDirectory: string;
  readonly runtimeDirectory: string;
  readonly definitionsDirectory: string;
  readonly instancesDirectory: string;
  readonly logsDirectory: string;

  private readonly tempRoot: string;
  private readonly hostAssemblyPath: string;
  private process: ReturnType<typeof spawn> | null = null;
  private readonly stdoutBuffer: string[] = [];
  private readonly stderrBuffer: string[] = [];

  private constructor(
    repoRoot: string,
    tempRoot: string,
    dataDirectory: string,
    runtimeDirectory: string,
    definitionsDirectory: string,
    instancesDirectory: string,
    logsDirectory: string,
    hostAssemblyPath: string
  ) {
    this.repoRoot = repoRoot;
    this.tempRoot = tempRoot;
    this.dataDirectory = dataDirectory;
    this.runtimeDirectory = runtimeDirectory;
    this.definitionsDirectory = definitionsDirectory;
    this.instancesDirectory = instancesDirectory;
    this.logsDirectory = logsDirectory;
    this.hostAssemblyPath = hostAssemblyPath;
  }

  get processId(): number | null {
    return this.process?.pid ?? null;
  }

  static async start(): Promise<DevHubHostFixture> {
    const repoRoot = resolveRepoRoot();
    const tempRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-"));
    const dataDirectory = tempRoot;
    const runtimeDirectory = path.join(dataDirectory, "runtime");
    const definitionsDirectory = path.join(dataDirectory, "apps", "definitions");
    const instancesDirectory = path.join(dataDirectory, "apps", "instances");
    const logsDirectory = path.join(dataDirectory, "logs");

    await fsPromises.mkdir(runtimeDirectory, { recursive: true });
    await fsPromises.mkdir(definitionsDirectory, { recursive: true });
    const hostAssemblyPath = await resolveHostAssemblyPath(repoRoot);

    const fixture = new DevHubHostFixture(
      repoRoot,
      tempRoot,
      dataDirectory,
      runtimeDirectory,
      definitionsDirectory,
      instancesDirectory,
      logsDirectory,
      hostAssemblyPath
    );
    await fixture.startProcess();
    return fixture;
  }

  async writeDefinition(definition: Record<string, unknown>): Promise<void> {
    const appId = definition.appId;
    if (typeof appId !== "string" || !appId.trim()) {
      throw new Error("appId 不能为空。");
    }
    const target = path.join(this.definitionsDirectory, `${appId}.json`);
    await fsPromises.writeFile(target, JSON.stringify(definition), "utf-8");
  }

  async close(): Promise<void> {
    const hostProcess = this.process;
    this.process = null;

    if (hostProcess?.pid) {
      await terminateProcessTree(hostProcess);
    }

    await fsPromises.rm(this.tempRoot, { recursive: true, force: true });
  }

  private async startProcess(): Promise<void> {
    if (!(await fileExists(this.hostAssemblyPath))) {
      throw new Error(`未找到 Host 程序：${this.hostAssemblyPath}`);
    }

    const env = {
      ...process.env,
      DEVHUB_DATA_DIR: this.dataDirectory
    };

    this.process = spawn("dotnet", [this.hostAssemblyPath], {
      cwd: this.repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
      env,
      detached: process.platform !== "win32",
      windowsHide: true
    });

    if (this.process.stdout) {
      // 说明：Host 会写 Console 日志，必须持续消费避免管道堵塞。
      this.process.stdout.on("data", (chunk: Buffer) => {
        pushOutput(this.stdoutBuffer, chunk.toString("utf-8"));
      });
    }

    if (this.process.stderr) {
      // 说明：stderr 同样需要 drain，避免日志输出造成死锁。
      this.process.stderr.on("data", (chunk: Buffer) => {
        pushOutput(this.stderrBuffer, chunk.toString("utf-8"));
      });
    }

    const hubJsonPath = path.join(this.runtimeDirectory, "hub.json");
    const deadline = Date.now() + 30_000;

    while (Date.now() < deadline) {
      if (await fileExists(hubJsonPath)) {
        return;
      }

      if (this.process.exitCode !== null) {
        throw new Error(
          `Host 进程提前退出。stdout=${this.stdoutBuffer.join("") || ""} stderr=${this.stderrBuffer.join("") || ""}`
        );
      }

      await delay(250);
    }

    throw new Error("等待 hub.json 超时。");
  }
}

function resolveRepoRoot(): string {
  let current = path.dirname(fileURLToPath(import.meta.url));
  for (;;) {
    if (
      existsSync(path.join(current, "AGENTS.md"))
      && existsSync(path.join(current, "host", "src", "DevHub.Host", "DevHub.Host.csproj"))
    ) {
      return current;
    }
    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }
    current = parent;
  }
  throw new Error("无法定位仓库根目录。");
}

function resolveHostAssemblyPath(repoRoot: string): Promise<string> {
  const configuredHostAssemblyPath = process.env[PrebuiltHostAssemblyEnvironmentVariable]?.trim();
  if (configuredHostAssemblyPath) {
    return resolveConfiguredHostAssemblyPath(repoRoot, configuredHostAssemblyPath);
  }

  sharedHostAssemblyPromise ??= (async () => {
    const hostBuildRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-host-build-"));
    return await buildHostAssembly(repoRoot, hostBuildRoot);
  })();

  return sharedHostAssemblyPromise;
}

async function resolveConfiguredHostAssemblyPath(repoRoot: string, configuredPath: string): Promise<string> {
  const resolvedPath = path.isAbsolute(configuredPath)
    ? configuredPath
    : path.resolve(repoRoot, configuredPath);

  if (!(await fileExists(resolvedPath))) {
    throw new Error(
      `环境变量 ${PrebuiltHostAssemblyEnvironmentVariable} 指定的 Host 程序不存在：${resolvedPath}`
    );
  }

  return resolvedPath;
}

async function buildHostAssembly(repoRoot: string, buildRoot: string): Promise<string> {
  const hostProjectPath = path.join(repoRoot, "host", "src", "DevHub.Host", "DevHub.Host.csproj");
  if (!(await fileExists(hostProjectPath))) {
    throw new Error(`未找到 Host 工程：${hostProjectPath}`);
  }

  await fsPromises.mkdir(buildRoot, { recursive: true });

  const stdoutBuffer: string[] = [];
  const stderrBuffer: string[] = [];
  const build = spawn("dotnet", [
    "build",
    hostProjectPath,
    "-c",
    "Release",
    "--nologo",
    `-p:BaseOutputPath=${ensureTrailingSeparator(path.join(buildRoot, "bin"))}`
  ], {
    cwd: repoRoot,
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true
  });

  build.stdout?.on("data", (chunk: Buffer) => {
    pushOutput(stdoutBuffer, chunk.toString("utf-8"));
  });

  build.stderr?.on("data", (chunk: Buffer) => {
    pushOutput(stderrBuffer, chunk.toString("utf-8"));
  });

  const [exitCode] = await once(build, "exit") as [number | null];
  if (exitCode !== 0) {
    throw new Error(
      `构建 Host 失败。stdout=${stdoutBuffer.join("") || ""} stderr=${stderrBuffer.join("") || ""}`
    );
  }

  const hostAssemblyPath = path.join(buildRoot, "bin", "Release", "net10.0", "DevHub.Host.dll");
  if (!(await fileExists(hostAssemblyPath))) {
    throw new Error(`未找到构建后的 Host 程序：${hostAssemblyPath}`);
  }

  return hostAssemblyPath;
}

async function fileExists(target: string): Promise<boolean> {
  try {
    await fsPromises.stat(target);
    return true;
  } catch {
    return false;
  }
}

function ensureTrailingSeparator(value: string): string {
  return value.endsWith(path.sep)
    ? value
    : `${value}${path.sep}`;
}

function pushOutput(buffer: string[], chunk: string): void {
  buffer.push(chunk);
  if (buffer.length > OUTPUT_LIMIT) {
    buffer.splice(0, buffer.length - OUTPUT_LIMIT);
  }
}

async function terminateProcessTree(child: ReturnType<typeof spawn>): Promise<void> {
  const pid = child.pid;
  if (!pid) {
    return;
  }

  if (process.platform === "win32") {
    const killResult = spawnSync("taskkill", ["/PID", String(pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true
    });
    if (killResult.error) {
      throw killResult.error;
    }

    if (killResult.status !== 0 && child.exitCode === null) {
      throw new Error(`终止 Host 进程树失败，PID=${pid}。`);
    }
  } else {
    try {
      process.kill(-pid, "SIGKILL");
    } catch (error) {
      if (!isMissingProcessError(error)) {
        throw error;
      }
    }
  }

  if (child.exitCode !== null) {
    return;
  }

  await Promise.race([
    once(child, "exit"),
    delay(10_000).then(() => {
      throw new Error(`等待 Host 进程退出超时，PID=${pid}。`);
    })
  ]);
}

function isMissingProcessError(error: unknown): boolean {
  return typeof error === "object"
    && error !== null
    && "code" in error
    && (error as NodeJS.ErrnoException).code === "ESRCH";
}
