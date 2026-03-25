#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { discoverRuntime } from "../../../../sdks/javascript/dist/index.js";

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

function normalizeDiscoveryError(explicitDataDir, error) {
  let reason = "discovery_failed";
  if (typeof explicitDataDir === "string" && path.basename(explicitDataDir).toLowerCase() === "runtime") {
    reason = "runtime_subdirectory_rejected";
  }

  return {
    reason,
    message: error instanceof Error ? error.message : String(error)
  };
}

async function runDiscovery(context) {
  const vector = context.vector;
  const request = vector.request ?? {};
  const explicitDataDir = typeof request.dataDir === "string" && request.dataDir.trim()
    ? request.dataDir
    : undefined;
  const environmentDataDir = typeof context.environmentDataDir === "string" && context.environmentDataDir.trim()
    ? context.environmentDataDir
    : undefined;
  const originalDataDirEnv = process.env.DEVHUB_DATA_DIR;

  try {
    if (environmentDataDir) {
      process.env.DEVHUB_DATA_DIR = environmentDataDir;
    }

    const connection = await discoverRuntime(explicitDataDir);
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "discovery",
      outcome: "success",
      actual: {
        runtimeDirectory: connection.runtimeDirectory,
        token: connection.token,
        runtime: {
          protocolVersion: connection.runtime.protocolVersion,
          httpBaseUrl: connection.runtime.httpBaseUrl,
          wsUrl: connection.runtime.wsUrl,
          tokenFile: connection.runtime.tokenFile
        }
      },
      error: null
    };
  } catch (error) {
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "discovery",
      outcome: "error",
      actual: normalizeDiscoveryError(explicitDataDir, error),
      error: null
    };
  } finally {
    if (originalDataDirEnv === undefined) {
      delete process.env.DEVHUB_DATA_DIR;
    } else {
      process.env.DEVHUB_DATA_DIR = originalDataDirEnv;
    }
  }
}

async function runRpc(context) {
  const vector = context.vector;
  const connection = await discoverRuntime(context.dataDir);
  const response = await fetch(`${connection.runtime.httpBaseUrl}/rpc`, {
    method: "POST",
    headers: vector.http?.headers ?? {},
    body: JSON.stringify(vector.request)
  });
  const body = await response.text();
  return {
    sdk: "typescript",
    vectorId: vector.id,
    phase: "rpc",
    outcome: "success",
    actual: JSON.parse(body),
    error: null
  };
}

async function main() {
  if (process.argv.length !== 3) {
    emit({
      sdk: "typescript",
      vectorId: null,
      phase: null,
      outcome: "error",
      actual: null,
      error: { message: "用法错误：需要 execution-context.json 路径。" }
    });
    return;
  }

  try {
    const context = JSON.parse(await readFile(process.argv[2], "utf-8"));
    const result = context.vector?.expectedDiscovery
      ? await runDiscovery(context)
      : await runRpc(context);
    emit(result);
  } catch (error) {
    emit({
      sdk: "typescript",
      vectorId: null,
      phase: null,
      outcome: "error",
      actual: null,
      error: {
        message: error instanceof Error ? error.message : String(error)
      }
    });
  }
}

await main();
