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

export class DevHubHostFixture {
  readonly repoRoot: string;
  readonly runtimeDirectory: string;
  readonly definitionsDirectory: string;

  private readonly tempRoot: string;
  private process: ReturnType<typeof spawn> | null = null;
  private readonly stdoutBuffer: string[] = [];
  private readonly stderrBuffer: string[] = [];

  private constructor(repoRoot: string, tempRoot: string, runtimeDirectory: string, definitionsDirectory: string) {
    this.repoRoot = repoRoot;
    this.tempRoot = tempRoot;
    this.runtimeDirectory = runtimeDirectory;
    this.definitionsDirectory = definitionsDirectory;
  }

  static async start(): Promise<DevHubHostFixture> {
    const repoRoot = resolveRepoRoot();
    const tempRoot = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-"));
    const runtimeDirectory = path.join(tempRoot, "runtime");
    const definitionsDirectory = path.join(tempRoot, "definitions");

    await fsPromises.mkdir(runtimeDirectory, { recursive: true });
    await fsPromises.mkdir(definitionsDirectory, { recursive: true });

    const fixture = new DevHubHostFixture(repoRoot, tempRoot, runtimeDirectory, definitionsDirectory);
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
    const hostAssemblyPath = path.join(
      this.repoRoot,
      "src",
      "DevHub.Host",
      "bin",
      "Release",
      "net10.0",
      "DevHub.Host.dll"
    );

    if (!(await fileExists(hostAssemblyPath))) {
      throw new Error(`未找到 Host 程序：${hostAssemblyPath}`);
    }

    const env = {
      ...process.env,
      DEVHUB_RUNTIME_DIR: this.runtimeDirectory,
      DEVHUB_APPDEFS_DIR: this.definitionsDirectory,
      DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS: randomUUID().replace(/-/g, "")
    };

    this.process = spawn("dotnet", [hostAssemblyPath], {
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

async function fileExists(target: string): Promise<boolean> {
  try {
    await fsPromises.stat(target);
    return true;
  } catch {
    return false;
  }
}

function pushOutput(buffer: string[], chunk: string): void {
  buffer.push(chunk);
  if (buffer.length > OUTPUT_LIMIT) {
    buffer.splice(0, buffer.length - OUTPUT_LIMIT);
  }
}
