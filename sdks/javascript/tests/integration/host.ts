import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { existsSync } from "node:fs";
import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const OUTPUT_LIMIT = 200;
let sharedHostAssemblyPromise: Promise<string> | undefined;

export class DevHubHostFixture {
  readonly repoRoot: string;
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
    runtimeDirectory: string,
    definitionsDirectory: string,
    instancesDirectory: string,
    logsDirectory: string,
    hostAssemblyPath: string
  ) {
    this.repoRoot = repoRoot;
    this.tempRoot = tempRoot;
    this.runtimeDirectory = runtimeDirectory;
    this.definitionsDirectory = definitionsDirectory;
    this.instancesDirectory = instancesDirectory;
    this.logsDirectory = logsDirectory;
    this.hostAssemblyPath = hostAssemblyPath;
  }

  static async start(): Promise<DevHubHostFixture> {
    const repoRoot = resolveRepoRoot();
    const tempRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-"));
    const runtimeDirectory = path.join(tempRoot, "runtime");
    const definitionsDirectory = path.join(tempRoot, "definitions");
    const instancesDirectory = path.join(tempRoot, "instances");
    const logsDirectory = path.join(tempRoot, "logs");

    await fsPromises.mkdir(runtimeDirectory, { recursive: true });
    await fsPromises.mkdir(definitionsDirectory, { recursive: true });
    const hostAssemblyPath = await resolveHostAssemblyPath(repoRoot);

    const fixture = new DevHubHostFixture(
      repoRoot,
      tempRoot,
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
    if (this.process && this.process.exitCode === null) {
      this.process.kill("SIGKILL");
      await Promise.race([once(this.process, "exit"), delay(10_000)]);
    }
    this.process = null;
    await fsPromises.rm(this.tempRoot, { recursive: true, force: true });
  }

  private async startProcess(): Promise<void> {
    if (!(await fileExists(this.hostAssemblyPath))) {
      throw new Error(`未找到 Host 程序：${this.hostAssemblyPath}`);
    }

    const env = {
      ...process.env,
      DEVHUB_RUNTIME_DIR: this.runtimeDirectory,
      DEVHUB_APPDEFS_DIR: this.definitionsDirectory,
      DEVHUB_APPINST_DIR: this.instancesDirectory,
      DEVHUB_LOG_DIR: this.logsDirectory,
      DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS: randomUUID().replace(/-/g, "")
    };

    this.process = spawn("dotnet", [this.hostAssemblyPath], {
      cwd: this.repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
      env
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
    if (existsSync(path.join(current, "AGENTS.md")) && existsSync(path.join(current, "src", "DevHub.Host", "DevHub.Host.csproj"))) {
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
  sharedHostAssemblyPromise ??= (async () => {
    const hostBuildRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-host-build-"));
    return await buildHostAssembly(repoRoot, hostBuildRoot);
  })();

  return sharedHostAssemblyPromise;
}

async function buildHostAssembly(repoRoot: string, buildRoot: string): Promise<string> {
  const hostProjectPath = path.join(repoRoot, "src", "DevHub.Host", "DevHub.Host.csproj");
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
    stdio: ["ignore", "pipe", "pipe"]
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
