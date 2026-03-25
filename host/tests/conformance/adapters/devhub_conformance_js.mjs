#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { DevHubClient, DevHubRpcError, discoverRuntime } from "../../../../sdks/javascript/dist/index.js";

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

async function runInvocation(context) {
  const vector = context.vector;
  const request = vector.request ?? {};
  const operation = request.kind === "sdk.notify" ? "notify" : "request";
  const client = await DevHubClient.fromRuntime({
    clientId: typeof request.clientId === "string" && request.clientId.trim()
      ? request.clientId
      : "ConformanceInvocation",
    dataDir: context.dataDir
  });

  try {
    const invokeRequest = buildInvokeRequest(request.invokeRequest);
    if (operation === "notify") {
      const result = await client.notify(invokeRequest);
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "sdk-invocation",
        operation,
        outcome: "success",
        actual: {
          ok: result.ok,
          invocationId: result.invocationId
        },
        error: null
      };
    }

    const result = await client.request(invokeRequest);
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "sdk-invocation",
      operation,
      outcome: "success",
      actual: {
        ok: result.ok,
        invocationId: result.invocationId,
        value: result.value
      },
      error: null
    };
  } catch (error) {
    if (error instanceof DevHubRpcError) {
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "sdk-invocation",
        operation,
        outcome: "error",
        actual: normalizeInvocationError(error),
        error: null
      };
    }

    throw error;
  } finally {
    await client.dispose();
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

function buildInvokeRequest(payload) {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    throw new Error("request.invokeRequest 必须为对象。");
  }

  const request = {
    appId: payload.appId,
    method: payload.method
  };

  if ("target" in payload && payload.target !== null) {
    request.target = payload.target;
  }

  if ("args" in payload) {
    request.args = payload.args;
  }

  if ("options" in payload && payload.options !== null) {
    request.options = payload.options;
  }

  return request;
}

function normalizeInvocationError(error) {
  const actual = {
    code: error.code,
    message: error.message
  };

  if (error.reason !== null) {
    actual.reason = error.reason;
  }

  if (error.invocationId !== null) {
    actual.invocationId = error.invocationId;
  }

  if (error.calleeError !== null) {
    actual.calleeError = {
      code: error.calleeError.code,
      message: error.calleeError.message
    };
    if (error.calleeError.data !== undefined) {
      actual.calleeError.data = error.calleeError.data;
    }
  }

  return actual;
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
    const kind = context.vector?.request?.kind;
    const result = context.vector?.expectedDiscovery
      ? await runDiscovery(context)
      : kind === "sdk.notify" || kind === "sdk.request"
        ? await runInvocation(context)
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
