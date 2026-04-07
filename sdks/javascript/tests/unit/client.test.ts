import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubRpcError, DevHubRpcErrorCode } from "../../src/errors.js";
import type { NormalizedDevHubClientOptions } from "../../src/models.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("M6_TS_UT_001 dispose should forward to an injected transport and remain idempotent", async () => {
  const connection = createConnectionInfo();
  const transport = {
    send: vi.fn(async () => ({
      ok: true,
      serverTimeUtc: "2026-03-09T00:00:00Z"
    })),
    dispose: vi.fn(async () => {})
  };

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-dispose-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => transport
    }
  );

  await client.dispose();
  await client.dispose();

  expect(transport.dispose).toHaveBeenCalledTimes(1);
});

it("M6_TS_UT_001 dispose should tolerate transports without a dispose hook", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-dispose-optional-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          serverTimeUtc: "2026-03-09T00:00:00Z"
        })
      })
    }
  );

  await expect(client.dispose()).resolves.toBeUndefined();
  await expect(client.dispose()).resolves.toBeUndefined();
});

it("M5_TS_UT_007 fromRuntime 应支持注入 runtimeResolver 与 transportFactory", async () => {
  const connection = createConnectionInfo();
  const runtimeResolver = {
    resolve: vi.fn(async (options: Readonly<NormalizedDevHubClientOptions>) => {
      expect(options).toMatchObject({
        clientId: "unit-injected-client",
        dataDir: "/tmp/devhub-js-sdk-runtime",
        protocolVersion: 1
      });
      expect(options.clientSessionId).toEqual(expect.any(String));
      return connection;
    })
  };
  const transport = {
    send: vi.fn(async (method: string, params?: Record<string, unknown> | null) => {
      expect(method).toBe("hub.ping");
      expect(params).toEqual({
        echo: {
          source: "fake-transport"
        }
      });

      return {
        ok: true,
        serverTimeUtc: "2026-03-09T00:00:00Z",
        echo: {
          source: "fake-transport"
        }
      };
    })
  };
  const transportFactory = vi.fn((_options: unknown, resolvedConnection: unknown) => {
    expect(resolvedConnection).toBe(connection);
    return transport;
  });

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver,
      transportFactory
    }
  );

  const result = await client.ping({ source: "fake-transport" });

  expect(result.echo).toEqual({ source: "fake-transport" });
  expect(client.connection).toBe(connection);
  expect(runtimeResolver.resolve).toHaveBeenCalledTimes(1);
  expect(transportFactory).toHaveBeenCalledTimes(1);
  expect(transport.send).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 ping 应拒绝注入 transport 返回的非法 echo JSON", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-invalid-echo-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          serverTimeUtc: "2026-03-09T00:00:00Z",
          echo: {
            callback: (() => "ignored") as any
          }
        })
      })
    }
  );

  await expect(client.ping()).rejects.toThrow("hub.ping.result.echo.callback 包含不支持的 JSON 类型。");
});

it("M5_TS_UT_004 request 应拒绝注入 transport 返回的非法 value JSON", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-invalid-value-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          invocationId: "invk-request-invalid-value",
          value: {
            callback: (() => "ignored") as any
          }
        })
      })
    }
  );

  await expect(client.request({
    appId: "test.app",
    method: "test.request"
  })).rejects.toThrow("hub.invoke.request.result.value.callback 包含不支持的 JSON 类型。");
});

it("M5_TS_UT_006 notify 应应用默认选项", async () => {
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
    dataDir: runtimeDir
  });

  const result = await client.notify({
    appId: "test.app",
    method: "test.notify"
  });

  expect(result.ok).toBe(true);
  expect(result.invocationId).toBe("invk-notify-1");
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_006 request 应保留显式空 scope 并应用默认选项", async () => {
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
    dataDir: runtimeDir
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

it("M5_TS_UT_004 listDefinitions 应兼容 Host 返回的可选 null 字段", async () => {
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
            exePath: process.execPath
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-list-definitions-client",
    dataDir: runtimeDir
  });

  await expect(client.listDefinitions()).rejects.toThrow(/description/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 getDefinition 应将缺省 capabilities.rpc 归一化为 true", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.rpc-default.app",
        displayName: "RPC Default App",
        capabilities: {
          events: false
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-capabilities-client",
    dataDir: runtimeDir
  });

  const definition = await client.getDefinition("test.rpc-default.app");

  expect(definition.capabilities).toEqual({
    rpc: true,
    events: false
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 RPC 错误应映射为 DevHubRpcError 并暴露辅助属性", async () => {
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
    dataDir: runtimeDir
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

it("M5_TS_UT_003 请求应携带协议头与鉴权头", async () => {
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
    dataDir: runtimeDir
  });

  const result = await client.ping();

  expect(result.ok).toBe(true);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_003 fromRuntime 应拒绝非法 requestTimeoutMs 类型", async () => {
  const runtimeDir = await createRuntime();

  await expect(DevHubClient.fromRuntime({
    clientId: "unit-timeout-client",
    dataDir: runtimeDir,
    requestTimeoutMs: "50" as unknown as number
  })).rejects.toThrow("requestTimeoutMs 必须为大于 0 的整数。");
});

it("M5_TS_UT_003 fromRuntime 应拒绝空 options", async () => {
  await expect(DevHubClient.fromRuntime(undefined as unknown as Parameters<typeof DevHubClient.fromRuntime>[0]))
    .rejects
    .toThrow("options 不能为空。");
});

it("M5_TS_UT_003 registerInstance 应在本地校验 invoke 布尔字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-client",
    dataDir: runtimeDir
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

it("M5_TS_UT_003 getDefinition should reject an invalid appId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-appid-client",
    dataDir: runtimeDir
  });

  await expect(client.getDefinition("Invalid.App")).rejects.toThrow(/appId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 registerInstance should reject an invalid instanceId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-instanceid-client",
    dataDir: runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "bad id",
    appId: "test.app",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  })).rejects.toThrow(/instanceId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 notify 应在本地校验 target.instanceId 类型", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-target-client",
    dataDir: runtimeDir
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

it("M5_TS_UT_003 launch should reject null waitForRegisterMs before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-launch-null-wait-client",
    dataDir: runtimeDir
  });

  await expect(client.launch({
    appId: "test.app",
    waitForRegisterMs: null as unknown as number
  })).rejects.toThrow(/waitForRegisterMs/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 poll 应在本地校验 waitMs 为整数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1",
    waitMs: 1.5 as unknown as number
  })).rejects.toThrow("waitMs 必须为大于等于 0 的整数。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 request should reject null queueIfOffline before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-request-null-bool-client",
    dataDir: runtimeDir
  });

  await expect(client.request({
    appId: "test.app",
    method: "test.request",
    options: {
      queueIfOffline: null as unknown as boolean
    }
  })).rejects.toThrow(/queueIfOffline/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_006 notify 应在本地拒绝 waitTimeoutMs", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-notify-wait-timeout-client",
    dataDir: runtimeDir
  });

  await expect(client.notify({
    appId: "test.app",
    method: "test.notify",
    options: {
      waitTimeoutMs: 1_000
    }
  })).rejects.toThrow("hub.invoke.notify 不支持 waitTimeoutMs。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_006 respond 应在本地校验 value 与 error 互斥", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-client",
    dataDir: runtimeDir
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

it("M5_TS_UT_006 respond should reject an invalid invocationId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-invocationid-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "bad-id",
    value: {
      ok: true
    }
  })).rejects.toThrow(/invocationId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 ping 应在本地拒绝会被静默丢弃的 echo 字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-ping-json-client",
    dataDir: runtimeDir
  });

  await expect(client.ping({
    callback: (() => "ignored") as any
  } as any)).rejects.toThrow("echo.callback 包含不支持的 JSON 类型。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 registerInstance 应在本地拒绝会被静默丢弃的 meta 字段", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-meta-client",
    dataDir: runtimeDir
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

it("M5_TS_UT_006 notify 应在本地拒绝会被重写的空洞数组参数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-notify-json-client",
    dataDir: runtimeDir
  });

  const sparseArray = new Array(1);

  await expect(client.notify({
    appId: "test.app",
    method: "test.notify",
    args: sparseArray as any
  })).rejects.toThrow("args[0] 不能为数组空洞。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_004 request should reject an invalid invocationId in a success payload", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      invocationId: "bad-id",
      value: {
        ok: true
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-request-result-validation-client",
    dataDir: runtimeDir
  });

  await expect(client.request({
    appId: "test.app",
    method: "test.request"
  })).rejects.toThrow(/invocationId/);
});

it("M5_TS_UT_006 respond 应在本地拒绝非法 error.data JSON 结构", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-json-client",
    dataDir: runtimeDir
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

it("M5_TS_UT_006 respond 应在本地拒绝非整数 error.code", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-error-code-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "invk-1",
    error: {
      code: 1001.5,
      message: "app_error"
    }
  })).rejects.toThrow("error.code 必须为整数。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_006 respond 应在本地拒绝非对象 error.data", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-error-data-shape-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    invocationId: "invk-1",
    error: {
      code: 1001,
      message: "app_error",
      data: "boom" as any
    }
  })).rejects.toThrow("error.data 必须为 JSON 对象。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_004 launch 应拒绝缺少 launchId 的成功载荷", async () => {
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
    dataDir: runtimeDir
  });

  await expect(client.launch({
    appId: "test.app"
  })).rejects.toThrow(/launchId/);
});

it("M5_TS_UT_004 poll 应拒绝缺少 caller.clientSessionId 的调用项", async () => {
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
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1"
  })).rejects.toThrow(/clientSessionId/);
});

it("M5_TS_UT_004 ping 应拒绝非法 JSON-RPC 版本的响应", async () => {
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
    dataDir: runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/jsonrpc/i);
});

it("M5_TS_UT_004 ping 应拒绝非整数 JSON-RPC error.code", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, undefined, {
      code: 1.5,
      message: "invalid_request",
      data: {
        reason: "bad_code"
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-jsonrpc-error-code-client",
    dataDir: runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/error\.code/i);
});

it("M5_TS_UT_004 ping 应拒绝非对象 JSON-RPC error.data", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, undefined, {
      code: DevHubRpcErrorCode.InvalidRequest,
      message: "invalid_request",
      data: "bad_data"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-jsonrpc-error-data-client",
    dataDir: runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/error\.data/i);
});

it("M5_TS_UT_004 ping should reject a serverTimeUtc value that is not a full date-time", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      serverTimeUtc: "2026-03-09"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-ping-date-validation-client",
    dataDir: runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/serverTimeUtc/i);
});

it("M5_TS_UT_003 notify should reject a non-object target before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-target-shape-client",
    dataDir: runtimeDir
  });

  await expect(client.notify({
    appId: "test.app",
    method: "test.notify",
    target: "global" as unknown as { scope?: string | null; instanceId?: string | null }
  })).rejects.toThrow("target must be an object.");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_003 request should reject a non-object options payload before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-options-shape-client",
    dataDir: runtimeDir
  });

  await expect(client.request({
    appId: "test.app",
    method: "test.request",
    options: 1 as unknown as {
      ttlMs?: number | null;
      waitTimeoutMs?: number | null;
      queueIfOffline?: boolean | null;
      autoLaunch?: boolean | null;
    }
  })).rejects.toThrow("options must be an object.");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("M5_TS_UT_004 poll should reject an invocation item whose waitTimeoutMs exceeds ttlMs", async () => {
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
          method: "test.request",
          kind: "request",
          createdAtUtc: "2026-03-09T00:00:00Z",
          options: {
            ttlMs: 1_000,
            waitTimeoutMs: 1_001
          },
          caller: {
            clientId: "caller-a",
            clientSessionId: "11111111-1111-4111-8111-111111111111"
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-options-validation-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1"
  })).rejects.toThrow(/waitTimeoutMs/i);
});

it("M5_TS_UT_004 getDefinition should accept spec-valid empty displayName and launch.exePath", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.empty-fields.app",
        displayName: "",
        launch: {
          exePath: ""
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-empty-strings-client",
    dataDir: runtimeDir
  });

  const definition = await client.getDefinition("test.empty-fields.app");

  expect(definition).toEqual({
    appId: "test.empty-fields.app",
    displayName: "",
    description: undefined,
    capabilities: {
      rpc: true
    },
    launch: {
      exePath: "",
      argsTemplate: undefined,
      workingDirectory: undefined,
      dedupeKeyTemplate: undefined
    }
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 getDefinition should reject null capabilities flags", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.invalid-capabilities.app",
        displayName: "Invalid Capabilities App",
        capabilities: {
          rpc: null
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-invalid-capabilities-client",
    dataDir: runtimeDir
  });

  await expect(client.getDefinition("test.invalid-capabilities.app")).rejects.toThrow(/capabilities\.rpc/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 listInstances should reject a null meta object", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.listInstances");

    return createJsonResponse(body.id, {
      ok: true,
      instances: [
        {
          instanceId: "inst-1",
          appId: "test.app",
          scope: null,
          pid: 12345,
          registeredAtUtc: "2026-03-09T00:00:00Z",
          lastSeenUtc: "2026-03-09T00:00:01Z",
          invoke: {
            poll: true,
            respond: true
          },
          meta: null
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-list-instances-null-meta-client",
    dataDir: runtimeDir
  });

  await expect(client.listInstances()).rejects.toThrow(/meta/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 listInstances 应拒绝注入 transport 返回的非法 meta JSON", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-invalid-meta-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          instances: [
            {
              instanceId: "inst-1",
              appId: "test.app",
              scope: null,
              pid: 12345,
              registeredAtUtc: "2026-03-09T00:00:00Z",
              lastSeenUtc: "2026-03-09T00:00:01Z",
              invoke: {
                poll: true,
                respond: true
              },
              meta: {
                callback: (() => "ignored") as any
              }
            }
          ]
        })
      })
    }
  );

  await expect(client.listInstances()).rejects.toThrow(
    "hub.apps.listInstances.result.instances[0].meta.callback 包含不支持的 JSON 类型。"
  );
});

it("M5_TS_UT_004 poll should reject null optional invocation booleans", async () => {
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
          method: "test.request",
          kind: "request",
          createdAtUtc: "2026-03-09T00:00:00Z",
          options: {
            ttlMs: 1_000,
            autoLaunch: null
          },
          caller: {
            clientId: "caller-a",
            clientSessionId: "11111111-1111-4111-8111-111111111111"
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-null-bool-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1"
  })).rejects.toThrow(/autoLaunch/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("M5_TS_UT_004 poll 应拒绝注入 transport 返回的非法 args JSON", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-invalid-args-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
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
              args: {
                callback: (() => "ignored") as any
              },
              kind: "notify",
              createdAtUtc: "2026-03-09T00:00:00Z",
              caller: {
                clientId: "caller-a",
                clientSessionId: "11111111-1111-4111-8111-111111111111"
              }
            }
          ]
        })
      })
    }
  );

  await expect(client.poll({
    instanceId: "inst-1"
  })).rejects.toThrow("hub.invoke.poll.result.items[0].args.callback 包含不支持的 JSON 类型。");
});

function createConnectionInfo() {
  return {
    runtimeDirectory: "/tmp/devhub-js-sdk-runtime/runtime",
    token: "token-fake",
    runtime: {
      protocolVersion: 1,
      pid: 12345,
      httpBaseUrl: "http://127.0.0.1:57231",
      wsUrl: "ws://127.0.0.1:57231/ws",
      tokenFile: "/tmp/devhub-js-sdk-runtime/runtime/token.txt",
      startedAtUtc: new Date("2026-03-09T00:00:00Z"),
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 30,
        launchDedupeWindowSeconds: 30
      }
    },
    rpcEndpoint: "http://127.0.0.1:57231/rpc",
    websocketEndpoint: "ws://127.0.0.1:57231/ws"
  };
}

async function createRuntime(): Promise<string> {
  const dataDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-unit-"));
  const runtimeDir = path.join(dataDir, "runtime");
  tempRoots.push(dataDir);

  await fsPromises.mkdir(runtimeDir, { recursive: true });

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

  return dataDir;
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
