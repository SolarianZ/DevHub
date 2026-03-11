import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubRpcError, DevHubRpcErrorCode } from "../../src/errors.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("notify 应应用默认选项", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.invoke.notify");
    expect(body.params.options).toEqual({
      ttlMs: 60_000,
      queueIfOffline: true,
      autoLaunch: true
    });
    expect(body.params.target).toBeUndefined();

    return createJsonResponse(body.id, {
      ok: true,
      invocationId: "invk-notify-1"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-notify-client",
    runtimeDir
  });

  const result = await client.notify({
    appId: "test.app",
    method: "test.notify"
  });

  expect(result.ok).toBe(true);
  expect(result.invocationId).toBe("invk-notify-1");
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("request 应保留显式空 scope 并应用默认选项", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.invoke.request");
    expect(body.params.target).toEqual({
      scope: "",
      instanceId: null
    });
    expect(body.params.options).toEqual({
      ttlMs: 300_000,
      waitTimeoutMs: 120_000,
      queueIfOffline: true,
      autoLaunch: true
    });

    return createJsonResponse(body.id, {
      ok: true,
      invocationId: "invk-request-1",
      value: {
        ok: true
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-request-client",
    runtimeDir
  });

  const result = await client.request({
    appId: "test.app",
    method: "test.request",
    target: {
      scope: "",
      instanceId: null
    }
  });

  expect(result.ok).toBe(true);
  expect(result.value).toEqual({ ok: true });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("listDefinitions 应兼容 Host 返回的可选 null 字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.listDefinitions");

    return createJsonResponse(body.id, {
      ok: true,
      definitions: [
        {
          appId: "test.launch.app",
          displayName: "Test Launch App",
          description: null,
          launch: {
            exePath: process.execPath,
            argsTemplate: null,
            workingDirectory: null,
            dedupeKeyTemplate: null
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-list-definitions-client",
    runtimeDir
  });

  const definitions = await client.listDefinitions();

  expect(definitions).toHaveLength(1);
  expect(definitions[0]).toEqual({
    appId: "test.launch.app",
    displayName: "Test Launch App",
    description: undefined,
    capabilities: undefined,
    launch: {
      exePath: process.execPath,
      argsTemplate: undefined,
      workingDirectory: undefined,
      dedupeKeyTemplate: undefined
    }
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("RPC 错误应映射为 DevHubRpcError 并暴露辅助属性", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, undefined, {
      code: DevHubRpcErrorCode.InvocationFailed,
      message: "invocation_failed",
      data: {
        invocationId: "invk-1",
        calleeError: {
          code: 1001,
          message: "app_error",
          data: {
            reason: "boom"
          }
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-error-client",
    runtimeDir
  });

  let capturedError: unknown;
  try {
    await client.ping();
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(DevHubRpcErrorCode.InvocationFailed);
  expect(rpcError.knownCode).toBe(DevHubRpcErrorCode.InvocationFailed);
  expect(rpcError.is(DevHubRpcErrorCode.InvocationFailed)).toBe(true);
  expect(rpcError.invocationId).toBe("invk-1");
  expect(rpcError.reason).toBeNull();
  expect(rpcError.calleeError).toEqual({
    code: 1001,
    message: "app_error",
    data: {
      reason: "boom"
    }
  });
  expect(rpcError.tryGetDataProperty("calleeError")).toEqual({
    code: 1001,
    message: "app_error",
    data: {
      reason: "boom"
    }
  });
});

it("请求应携带协议头与鉴权头", async () => {
  const runtimeDir = await createRuntime();
  const clientSessionId = "11111111-1111-4111-8111-111111111111";
  const fetchSpy = vi.fn(async (input: unknown, init?: RequestInit) => {
    expect(input).toBe("http://127.0.0.1:47231/rpc");
    expect(init?.method).toBe("POST");
    expect(init?.headers).toEqual({
      "Content-Type": "application/json",
      Authorization: "Bearer token-1",
      "X-DevHub-Protocol": "1",
      "X-DevHub-ClientId": "unit-header-client",
      "X-DevHub-ClientSessionId": clientSessionId
    });

    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      serverTimeUtc: "2026-03-09T00:00:00Z"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-header-client",
    clientSessionId,
    runtimeDir
  });

  const result = await client.ping();

  expect(result.ok).toBe(true);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("fromRuntime 应拒绝非法 requestTimeoutMs 类型", async () => {
  const runtimeDir = await createRuntime();

  await expect(DevHubClient.fromRuntime({
    clientId: "unit-timeout-client",
    runtimeDir,
    requestTimeoutMs: "50" as unknown as number
  })).rejects.toThrow("requestTimeoutMs 必须为大于 0 的整数。");
});

it("fromRuntime 应拒绝空 options", async () => {
  await expect(DevHubClient.fromRuntime(undefined as unknown as Parameters<typeof DevHubClient.fromRuntime>[0]))
    .rejects
    .toThrow("options 不能为空。");
});

it("registerInstance 应在本地校验 invoke 布尔字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-client",
    runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    pid: 12345,
    invoke: {
      poll: "true" as unknown as boolean,
      respond: true
    }
  })).rejects.toThrow("invoke.poll 必须为布尔值。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("notify 应在本地校验 target.instanceId 类型", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-target-client",
    runtimeDir
  });

  await expect(client.notify({
    appId: "test.app",
    method: "test.notify",
    target: {
      instanceId: 123 as unknown as string
    }
  })).rejects.toThrow("target.instanceId 类型非法。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("poll 应在本地校验 waitMs 为整数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-client",
    runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1",
    waitMs: 1.5 as unknown as number
  })).rejects.toThrow("waitMs 必须为大于等于 0 的整数。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond 应在本地校验 value 与 error 互斥", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-client",
    runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "invk-1"
  })).rejects.toThrow("RespondRequest 必须且只能包含 value 或 error 之一。");

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "invk-1",
    value: { ok: true },
    error: {
      code: 1001,
      message: "app_error"
    }
  })).rejects.toThrow("RespondRequest 必须且只能包含 value 或 error 之一。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("ping 应在本地拒绝会被静默丢弃的 echo 字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-ping-json-client",
    runtimeDir
  });

  await expect(client.ping({
    callback: (() => "ignored") as any
  } as any)).rejects.toThrow("echo.callback 包含不支持的 JSON 类型。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("registerInstance 应在本地拒绝会被静默丢弃的 meta 字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-meta-client",
    runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    },
    meta: {
      callback: (() => "ignored") as any
    } as any
  })).rejects.toThrow("meta.callback 包含不支持的 JSON 类型。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("notify 应在本地拒绝会被重写的空洞数组参数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-notify-json-client",
    runtimeDir
  });

  const sparseArray = new Array(1);

  await expect(client.notify({
    appId: "test.app",
    method: "test.notify",
    args: sparseArray as any
  })).rejects.toThrow("args[0] 不能为数组空洞。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond 应在本地拒绝非法 error.data JSON 结构", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-json-client",
    runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "invk-1",
    error: {
      code: 1001,
      message: "app_error",
      data: {
        callback: (() => "ignored") as any
      } as any
    }
  })).rejects.toThrow("error.data.callback 包含不支持的 JSON 类型。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("launch 应拒绝缺少 launchId 的成功载荷", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      status: "started",
      pid: 12345
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-launch-validation-client",
    runtimeDir
  });

  await expect(client.launch({
    appId: "test.app"
  })).rejects.toThrow(/launchId/);
});

it("poll 应拒绝缺少 caller.clientSessionId 的调用项", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      serverTimeUtc: "2026-03-09T00:00:00Z",
      items: [
        {
          invocationId: "invk-1",
          appId: "test.app",
          target: {
            scope: null,
            instanceId: null
          },
          method: "test.notify",
          kind: "notify",
          createdAtUtc: "2026-03-09T00:00:00Z",
          caller: {
            clientId: "caller-a"
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-validation-client",
    runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1"
  })).rejects.toThrow(/clientSessionId/);
});

it("ping 应拒绝非法 JSON-RPC 版本的响应", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(input).toBe("http://127.0.0.1:47231/rpc");

    return {
      ok: true,
      status: 200,
      statusText: "OK",
      text: async () => JSON.stringify({
        jsonrpc: "1.0",
        id: body.id,
        result: {
          ok: true,
          serverTimeUtc: "2026-03-09T00:00:00Z"
        }
      })
    } as Response;
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-jsonrpc-validation-client",
    runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/jsonrpc/i);
});

async function createRuntime(): Promise<string> {
  const runtimeDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-unit-"));
  tempRoots.push(runtimeDir);

  const tokenFile = path.join(runtimeDir, "token.txt");
  await fsPromises.writeFile(tokenFile, "token-1", "utf-8");
  await fsPromises.writeFile(
    path.join(runtimeDir, "hub.json"),
    JSON.stringify({
      protocolVersion: 1,
      pid: 12345,
      httpBaseUrl: "http://127.0.0.1:47231",
      wsUrl: "ws://127.0.0.1:47231/ws",
      tokenFile,
      startedAtUtc: "2026-03-09T00:00:00Z",
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 30,
        launchDedupeWindowSeconds: 30
      }
    }),
    "utf-8"
  );

  return runtimeDir;
}

function parseRequestBody(init?: RequestInit): Record<string, any> {
  return JSON.parse(String(init?.body));
}

function createJsonResponse(requestId: string, result?: unknown, error?: unknown): Response {
  return {
    ok: true,
    status: 200,
    statusText: "OK",
    text: async () => JSON.stringify({
      jsonrpc: "2.0",
      id: requestId,
      ...(error === undefined ? { result } : { error })
    })
  } as Response;
}
