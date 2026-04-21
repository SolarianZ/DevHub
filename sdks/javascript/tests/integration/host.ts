import { spawn, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { existsSync, rmSync } from "node:fs";
import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const OUTPUT_LIMIT = 200;
const PrebuiltHostAssemblyEnvironmentVariable = "DEVHUB_JS_SDK_HOST_ASSEMBLY";
const SharedPrebuiltHostAssemblyEnvironmentVariable = "DEVHUB_SDK_HOST_ASSEMBLY";
const SingleInstanceSlotEnvironmentVariable = "DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS";
const TestLiveStatusEnvironmentVariable = "DEVHUB_TEST_LIVE_STATUS";
const LongWaitStatusThresholdSeconds = 8;
let sharedHostAssemblyPromise: Promise<string> | undefined;
let sharedHostBuildRoot: string | undefined;
let sharedHostCleanupRegistered = false;

function readLiveStatusEnabled(environment: NodeJS.ProcessEnv = process.env): boolean {
  const rawValue = environment[TestLiveStatusEnvironmentVariable];
  if (rawValue === undefined) {
    return false;
  }

  const candidate = rawValue.trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(candidate)) {
    return true;
  }
  if (["0", "false", "no", "off"].includes(candidate)) {
    return false;
  }

  throw new Error(
    `${TestLiveStatusEnvironmentVariable} 必须是布尔值（1/0/true/false/yes/no/on/off）。`
  );
}

class LongWaitStatus {
  private readonly label: string;
  private readonly estimatedSeconds: number;
  private readonly liveEnabled: boolean;
  private readonly startedAt = Date.now();
  private entered = false;
  private lastRenderedSecond = -1;
  private lastRenderLength = 0;

  constructor(label: string, estimatedSeconds: number, environment: NodeJS.ProcessEnv = process.env) {
    this.label = label;
    this.estimatedSeconds = Math.max(0, Math.ceil(estimatedSeconds));
    this.liveEnabled = readLiveStatusEnabled(environment);
  }

  tick(): void {
    const elapsedSeconds = Math.floor((Date.now() - this.startedAt) / 1000);
    if (!this.entered && elapsedSeconds >= LongWaitStatusThresholdSeconds) {
      this.entered = true;
      if (!this.liveEnabled) {
        console.log(`[状态] ${this.label} 开始，预计等待约 ${this.estimatedSeconds}s`);
        return;
      }
    }

    if (this.liveEnabled && this.entered && elapsedSeconds !== this.lastRenderedSecond) {
      this.lastRenderedSecond = elapsedSeconds;
      const content = `[状态] ${this.label} 已等待 ${elapsedSeconds}s`;
      const trailingSpaces = " ".repeat(Math.max(0, this.lastRenderLength - content.length));
      process.stdout.write(`\r${content}${trailingSpaces}`);
      this.lastRenderLength = Math.max(this.lastRenderLength, content.length);
    }
  }

  finish(): void {
    if (!(this.liveEnabled && this.entered)) {
      return;
    }

    this.tick();
    process.stdout.write("\n");
  }
}

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
    const tempRoot = await fsPromises.realpath(
      await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-"))
    );
    const dataDirectory = tempRoot;
    const runtimeDirectory = path.join(dataDirectory, "runtime");
    const definitionsDirectory = path.join(dataDirectory, "apps", "definitions");
    const instancesDirectory = path.join(dataDirectory, "apps", "instances");
    const logsDirectory = path.join(dataDirectory, "logs");

    await fsPromises.mkdir(runtimeDirectory, { recursive: true });
    await fsPromises.mkdir(definitionsDirectory, { recursive: true });
    await fsPromises.mkdir(instancesDirectory, { recursive: true });
    await fsPromises.mkdir(logsDirectory, { recursive: true });
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
    const payload = { ...definition };
    const appId = payload.appId;
    if (typeof appId !== "string" || !appId.trim()) {
      throw new Error("appId 不能为空。");
    }

    const scope = normalizeDefinitionScope(payload.scope);
    payload.scope = scope;

    const target = path.join(this.definitionsDirectory, buildDefinitionFileName(appId, scope));
    await fsPromises.writeFile(target, JSON.stringify(payload), "utf-8");
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
      DEVHUB_DATA_DIR: this.dataDirectory,
      [SingleInstanceSlotEnvironmentVariable]: randomUUID()
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
    const status = new LongWaitStatus("等待 JS SDK Host fixture 生成 hub.json", 30, process.env);

    try {
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
        status.tick();
      }
    } finally {
      status.finish();
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

export function resolveHostAssemblyPath(
  repoRoot: string,
  environment: NodeJS.ProcessEnv = process.env
): Promise<string> {
  const configuredHostAssembly = resolveConfiguredHostAssemblyFromEnvironment(repoRoot, environment);
  if (configuredHostAssembly) {
    return configuredHostAssembly;
  }

  sharedHostAssemblyPromise ??= (async () => {
    const hostBuildRoot = await fsPromises.realpath(
      await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-host-build-"))
    );
    sharedHostBuildRoot = hostBuildRoot;
    registerSharedHostCleanup();

    try {
      return await buildHostAssembly(repoRoot, hostBuildRoot);
    } catch (error) {
      cleanupSharedHostBuildRoot();
      throw error;
    }
  })();

  return sharedHostAssemblyPromise;
}

export function resolveConfiguredHostAssemblyFromEnvironment(
  repoRoot: string,
  environment: NodeJS.ProcessEnv = process.env
): Promise<string> | undefined {
  for (const environmentVariableName of [
    PrebuiltHostAssemblyEnvironmentVariable,
    SharedPrebuiltHostAssemblyEnvironmentVariable
  ]) {
    const configuredPath = environment[environmentVariableName]?.trim();
    if (configuredPath) {
      return resolveConfiguredHostAssemblyPath(repoRoot, configuredPath, environmentVariableName);
    }
  }

  return undefined;
}

async function resolveConfiguredHostAssemblyPath(
  repoRoot: string,
  configuredPath: string,
  environmentVariableName: string
): Promise<string> {
  const resolvedPath = path.isAbsolute(configuredPath)
    ? configuredPath
    : path.resolve(repoRoot, configuredPath);

  if (!(await fileExists(resolvedPath))) {
    throw new Error(
      `环境变量 ${environmentVariableName} 指定的 Host 程序不存在：${resolvedPath}`
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

function normalizeDefinitionScope(value: unknown): string | null {
  if (value === undefined || value === null) {
    return null;
  }

  if (typeof value !== "string" || !value.trim()) {
    throw new Error("definition.scope 必须为非空字符串或 null。");
  }

  return value;
}

function buildDefinitionFileName(appId: string, scope: string | null): string {
  const scopeSegment = scope === null
    ? "global"
    : Buffer.from(scope, "utf-8").toString("hex").toUpperCase();
  return `${appId}--${scopeSegment}.json`;
}

function registerSharedHostCleanup(): void {
  if (sharedHostCleanupRegistered) {
    return;
  }

  sharedHostCleanupRegistered = true;
  process.once("exit", cleanupSharedHostBuildRoot);
}

function cleanupSharedHostBuildRoot(): void {
  sharedHostAssemblyPromise = undefined;

  const buildRoot = sharedHostBuildRoot;
  sharedHostBuildRoot = undefined;
  if (!buildRoot) {
    return;
  }

  try {
    rmSync(buildRoot, { recursive: true, force: true });
  } catch {
    // 说明：进程退出时这里只做尽力回收，避免把测试失败原因污染为清理异常。
  }
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
