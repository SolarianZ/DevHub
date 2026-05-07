import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { DevHubClient } from "../../src/client.js";
import {
  DevHubConnectionError,
  DevHubRpcError,
  DevHubRpcErrorCode,
} from "../../src/errors.js";
import type { NormalizedDevHubClientOptions } from "../../src/models.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("dispose should forward to an injected transport and remain idempotent", async () => {
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

it("dispose should tolerate transports without a dispose hook", async () => {
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

it("disposed client should reject further RPCs without calling transport", async () => {
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
      clientId: "unit-disposed-client",
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

  await expect(client.ping()).rejects.toThrow(/disposed/i);
  expect(transport.send).not.toHaveBeenCalled();
});

it("fromRuntime 应支持注入 runtimeResolver 与 transportFactory", async () => {
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
  expect(client.runtime).toEqual({
    protocolVersion: 1,
    pid: 12345,
    startedAtUtc: new Date("2026-03-09T00:00:00Z"),
    hubVersion: "0.7.0-test"
  });
  expect((client.runtime as unknown as Record<string, unknown>).httpBaseUrl).toBeUndefined();
  expect((client.runtime as unknown as Record<string, unknown>).wsUrl).toBeUndefined();
  expect((client.runtime as unknown as Record<string, unknown>).tokenFile).toBeUndefined();
  expect((client as unknown as Record<string, unknown>).connection).toBeUndefined();
  expect(runtimeResolver.resolve).toHaveBeenCalledTimes(1);
  expect(transportFactory).toHaveBeenCalledTimes(1);
  expect(transport.send).toHaveBeenCalledTimes(1);
});

it("fromRuntime 应拒绝注入 resolver 返回的非法 WebSocket 端点", async () => {
  const connection = createConnectionInfo({
    runtime: {
      wsUrl: "ws://127.0.0.1:57231/ws?"
    },
    websocketEndpoint: "ws://127.0.0.1:57231/ws?"
  });

  await expect(DevHubClient.fromRuntime(
    {
      clientId: "unit-invalid-resolver-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => {
        throw new Error("transportFactory should not be called.");
      }
    }
  )).rejects.toThrow(/wsUrl/);
});

it("runtime 应返回脱敏快照", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-runtime-view-client",
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

  const firstRuntime = client.runtime;
  firstRuntime.startedAtUtc.setUTCFullYear(2000);

  const secondRuntime = client.runtime;
  expect(secondRuntime).toEqual({
    protocolVersion: 1,
    pid: 12345,
    startedAtUtc: new Date("2026-03-09T00:00:00Z"),
    hubVersion: "0.7.0-test"
  });
  expect((secondRuntime as unknown as Record<string, unknown>).httpBaseUrl).toBeUndefined();
  expect((secondRuntime as unknown as Record<string, unknown>).wsUrl).toBeUndefined();
  expect((secondRuntime as unknown as Record<string, unknown>).tokenFile).toBeUndefined();
});

it("ping 应拒绝注入 transport 返回的非法 echo JSON", async () => {
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

it("request 应拒绝注入 transport 返回的非法 value JSON", async () => {
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
    method: "test.request",
    target: {
      scope: ""
    }
  })).rejects.toThrow("hub.invoke.request.result.value.callback 包含不支持的 JSON 类型。");
});

it("HTTP 请求超时应抛出 typed connection error", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("fetch", vi.fn(async (_input: unknown, init?: RequestInit) => {
    await new Promise((_, reject) => {
      const signal = init?.signal as AbortSignal | undefined;
      signal?.addEventListener("abort", () => {
        const abortError = new Error("aborted");
        abortError.name = "AbortError";
        reject(abortError);
      }, { once: true });
    });
    return createJsonResponse("unused", { ok: true });
  }));

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-http-timeout-client",
    dataDir: runtimeDir,
    requestTimeoutMs: 10,
  });

  let captured: unknown;
  try {
    await client.ping();
  } catch (error) {
    captured = error;
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect((captured as DevHubConnectionError).kind).toBe("timeout");
});

it("非 200 HTTP 响应应抛出 typed connection error", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("fetch", vi.fn(async () => ({
    ok: false,
    status: 503,
    statusText: "Service Unavailable",
    text: async () => "gateway down",
  } as Response)));

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-http-status-client",
    dataDir: runtimeDir,
  });

  let captured: unknown;
  try {
    await client.ping();
  } catch (error) {
    captured = error;
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect(captured).toMatchObject({
    kind: "http_status",
    status: 503,
    statusText: "Service Unavailable",
    responseBody: "gateway down",
  });
});

it("非法 JSON-RPC 响应应抛出 typed connection error", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("fetch", vi.fn(async () => ({
    ok: true,
    status: 200,
    statusText: "OK",
    text: async () => JSON.stringify({
      jsonrpc: "1.0",
      id: "unexpected",
      result: {
        ok: true,
      },
    }),
  } as Response)));

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-invalid-response-client",
    dataDir: runtimeDir,
  });

  let captured: unknown;
  try {
    await client.ping();
  } catch (error) {
    captured = error;
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect((captured as DevHubConnectionError).kind).toBe("invalid_response");
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
    expect(body.params.target).toEqual({
      scope: "",
      instanceId: null
    });

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
    method: "test.notify",
    target: {
      scope: ""
    }
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

it("listDefinitions 应显式发送请求对象并保留 Global、精确 scope 与字面值 global", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.listDefinitions");
    expect(body.params).toEqual({
      scope: null
    });

    return createJsonResponse(body.id, {
      ok: true,
      definitions: [
        {
          appId: "test.launch.app",
          scope: "",
          displayName: "Test Launch App Global"
        },
        {
          appId: "test.launch.app",
          scope: "workspace-a",
          displayName: "Test Launch App Workspace A"
        },
        {
          appId: "test.launch.app",
          scope: "global",
          displayName: "Test Launch App Literal Global"
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-list-definitions-client",
    dataDir: runtimeDir
  });

  const definitions = await client.listDefinitions({
    scope: null
  });

  expect(definitions.map((definition) => definition.scope)).toEqual([
    "",
    "workspace-a",
    "global"
  ]);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("listDefinitions should reject blank displayName from transport while preserving blank launch.exePath behavior elsewhere", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.listDefinitions");

    return createJsonResponse(body.id, {
      ok: true,
      definitions: [
        {
          appId: "test.invalid-display-name.app",
          scope: "",
          displayName: "   ",
          launch: {
            exePath: ""
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-list-definitions-invalid-display-name-client",
    dataDir: runtimeDir
  });

  await expect(client.listDefinitions({
    scope: null
  })).rejects.toThrow("hub.apps.listDefinitions.result.definitions[0].displayName must be a non-empty string.");
});

it("getDefinition 应将缺省 capabilities.rpc 归一化为 true", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");
    expect(body.params).toEqual({
      appId: "test.rpc-default.app",
      scope: ""
    });

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.rpc-default.app",
        scope: "",
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

  const definition = await client.getDefinition({
    appId: "test.rpc-default.app",
    scope: ""
  });

  expect(definition.scope).toBe("");
  expect(definition.capabilities).toEqual({
    rpc: true,
    events: false
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("getDefinition 应保留 canonical mixed-case appId 与 dotted scope", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");
    expect(body.params).toEqual({
      appId: "Sample.App",
      scope: "Workspace-A.v2"
    });

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "Sample.App",
        scope: "Workspace-A.v2",
        displayName: "Sample App"
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-canonical-client",
    dataDir: runtimeDir
  });

  const definition = await client.getDefinition({
    appId: "Sample.App",
    scope: "Workspace-A.v2"
  });

  expect(definition).toMatchObject({
    appId: "Sample.App",
    scope: "Workspace-A.v2",
    displayName: "Sample App"
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("getInstance should send the exact instanceId and parse a single AppInstance", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getInstance");
    expect(body.params).toEqual({
      instanceId: "inst-1"
    });

    return createJsonResponse(body.id, {
      ok: true,
      instance: {
        instanceId: "inst-1",
        appId: "test.instance.app",
        scope: "",
        pid: 12345,
        registeredAtUtc: "2026-03-09T00:00:00Z",
        lastSeenUtc: "2026-03-09T00:00:01Z",
        invoke: {
          poll: true,
          respond: false
        },
        meta: {
          source: "unit"
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-instance-client",
    dataDir: runtimeDir
  });

  const instance = await client.getInstance("inst-1");

  expect(instance).toEqual({
    instanceId: "inst-1",
    appId: "test.instance.app",
    scope: "",
    pid: 12345,
    registeredAtUtc: new Date("2026-03-09T00:00:00Z"),
    lastSeenUtc: new Date("2026-03-09T00:00:01Z"),
    invoke: {
      poll: true,
      respond: false
    },
    meta: {
      source: "unit"
    }
  });
  expect((instance as unknown as Record<string, unknown>).instanceSessionToken).toBeUndefined();
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("validateDefinition should reject blank displayName before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-validate-definition-client",
    dataDir: runtimeDir
  });

  await expect(client.validateDefinition({
    appId: "test.validate.app",
    scope: "",
    displayName: "   "
  })).rejects.toThrow("definition.displayName 不能为空白字符串。");
  expect(fetchSpy).not.toHaveBeenCalled();
});

it("upsertDefinition 应发送写请求并解析返回定义", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.upsertDefinition");
    expect(body.params).toEqual({
      definition: {
        appId: "test.upsert.app",
        scope: "",
        displayName: "Upsert App",
        capabilities: {
          rpc: true,
          events: false
        },
        launch: {
          exePath: process.execPath,
          args: ["./app.js", "--scope", "{scope}"]
        }
      }
    });

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.upsert.app",
        scope: "",
        displayName: "Upsert App",
        capabilities: {
          rpc: true,
          events: false
        },
        launch: {
          exePath: process.execPath,
          args: ["./app.js", "--scope", "{scope}"]
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-upsert-definition-client",
    dataDir: runtimeDir
  });

  const result = await client.upsertDefinition({
    appId: "test.upsert.app",
    scope: "",
    displayName: "Upsert App",
    capabilities: {
      rpc: true,
      events: false
    },
    launch: {
      exePath: process.execPath,
      args: ["./app.js", "--scope", "{scope}"]
    }
  });

  expect(result).toEqual({
    appId: "test.upsert.app",
    scope: "",
    displayName: "Upsert App",
    description: undefined,
    capabilities: {
      rpc: true,
      events: false
    },
    launch: {
      exePath: process.execPath,
      args: ["./app.js", "--scope", "{scope}"],
      argsTemplate: undefined,
      workingDirectory: undefined,
      dedupeKeyTemplate: undefined
    }
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("upsertDefinition should reject blank displayName before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-upsert-definition-invalid-display-name-client",
    dataDir: runtimeDir
  });

  await expect(client.upsertDefinition({
    appId: "test.upsert.app",
    scope: "",
    displayName: " "
  })).rejects.toThrow("definition.displayName 不能为空白字符串。");
  expect(fetchSpy).not.toHaveBeenCalled();
});

it("deleteDefinition 应发送删除请求", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.deleteDefinition");
    expect(body.params).toEqual({
      appId: "test.delete.app",
      scope: ""
    });

    return createJsonResponse(body.id, {
      ok: true
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-delete-definition-client",
    dataDir: runtimeDir
  });

  await expect(client.deleteDefinition({
    appId: "test.delete.app",
    scope: ""
  })).resolves.toBeUndefined();
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("registerInstance 应返回 instanceSessionToken，实例拥有者 RPC 应携带顶层 token 参数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);

    if (body.method === "hub.apps.registerInstance") {
      expect(body.params).toEqual({
        password: "secret-1",
        instance: {
          instanceId: "inst-1",
          appId: "test.app",
          scope: "",
          pid: 12345,
          invoke: {
            poll: true,
            respond: true
          }
        }
      });

      return createJsonResponse(body.id, {
        ok: true,
        instance: {
          instanceId: "inst-1",
          appId: "test.app",
          scope: "",
          pid: 12345,
          registeredAtUtc: "2026-03-09T00:00:00Z",
          lastSeenUtc: "2026-03-09T00:00:00Z",
          invoke: {
            poll: true,
            respond: true
          }
        },
        instanceSessionToken: "session-1"
      });
    }

    if (body.method === "hub.apps.heartbeat") {
      expect(body.params).toEqual({
        instanceId: "inst-1",
        instanceSessionToken: "session-1"
      });

      return createJsonResponse(body.id, {
        ok: true,
        lastSeenUtc: "2026-03-09T00:00:02Z"
      });
    }

    if (body.method === "hub.invoke.poll") {
      expect(body.params).toEqual({
        instanceId: "inst-1",
        instanceSessionToken: "session-1",
        maxCount: 1,
        waitMs: 0
      });

      return createJsonResponse(body.id, {
        ok: true,
        serverTimeUtc: "2026-03-09T00:00:03Z",
        items: []
      });
    }

    if (body.method === "hub.invoke.respond") {
      expect(body.params).toEqual({
        instanceId: "inst-1",
        instanceSessionToken: "session-1",
        invocationId: "invk-1",
        leaseToken: "lease-1",
        value: {
          ok: true
        }
      });

      return createJsonResponse(body.id, {
        ok: true
      });
    }

    expect(body.method).toBe("hub.apps.unregisterInstance");
    expect(body.params).toEqual({
      instanceId: "inst-1",
      instanceSessionToken: "session-1"
    });
    return createJsonResponse(body.id, {
      ok: true
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-instance-password-client",
    dataDir: runtimeDir
  });

  const instance = await client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "secret-1");

  expect((instance as unknown as Record<string, unknown>).password).toBeUndefined();
  expect(instance.instanceSessionToken).toBe("session-1");
  await expect(client.heartbeat("inst-1", instance.instanceSessionToken)).resolves.toEqual(new Date("2026-03-09T00:00:02Z"));
  await expect(client.poll({
    instanceId: "inst-1",
    instanceSessionToken: instance.instanceSessionToken,
    maxCount: 1,
    waitMs: 0
  })).resolves.toMatchObject({
    ok: true,
    items: []
  });
  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: instance.instanceSessionToken,
    invocationId: "invk-1",
    leaseToken: "lease-1",
    value: {
      ok: true
    }
  })).resolves.toBeUndefined();
  await expect(client.unregisterInstance("inst-1", instance.instanceSessionToken)).resolves.toBeUndefined();
  expect(fetchSpy).toHaveBeenCalledTimes(5);
});

it("registerInstance 应把 launchId 作为顶层 params 发送", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.registerInstance");
    expect(body.params).toEqual({
      password: "secret-1",
      launchId: "launch-1",
      instance: {
        instanceId: "inst-1",
        appId: "test.app",
        scope: "",
        pid: 12345,
        invoke: {
          poll: true,
          respond: true
        }
      }
    });

    return createJsonResponse(body.id, {
      ok: true,
      instance: {
        instanceId: "inst-1",
        appId: "test.app",
        scope: "",
        pid: 12345,
        registeredAtUtc: "2026-03-09T00:00:00Z",
        lastSeenUtc: "2026-03-09T00:00:00Z",
        invoke: {
          poll: true,
          respond: true
        }
      },
      instanceSessionToken: "session-1"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-launch-id-client",
    dataDir: runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "secret-1", { launchId: "launch-1" })).resolves.toMatchObject({
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
  });
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("registerInstance 应拒绝返回包含 password 的实例结果", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-instance-password-leak-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          instance: {
            instanceId: "inst-1",
            appId: "test.app",
            scope: "",
            pid: 12345,
            registeredAtUtc: "2026-03-09T00:00:00Z",
            lastSeenUtc: "2026-03-09T00:00:01Z",
            invoke: {
              poll: true,
              respond: true
            },
            password: "secret-1"
          }
        })
      })
    }
  );

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "secret-1")).rejects.toThrow(/password/i);
});

it("registerInstance 应拒绝缺少 instanceSessionToken 的成功结果", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-instance-session-token-required-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          instance: {
            instanceId: "inst-1",
            appId: "test.app",
            scope: "",
            pid: 12345,
            registeredAtUtc: "2026-03-09T00:00:00Z",
            lastSeenUtc: "2026-03-09T00:00:01Z",
            invoke: {
              poll: true,
              respond: true
            }
          }
        })
      })
    }
  );

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "secret-1")).rejects.toThrow(/instanceSessionToken/i);
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

it("invocation_expired 应暴露 unknown_invocation 的辅助属性", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, undefined, {
      code: DevHubRpcErrorCode.InvocationExpired,
      message: "invocation_expired",
      data: {
        invocationId: "invk-unknown",
        reason: "unknown_invocation"
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-unknown-invocation-client",
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
  expect(rpcError.code).toBe(DevHubRpcErrorCode.InvocationExpired);
  expect(rpcError.invocationId).toBe("invk-unknown");
  expect(rpcError.reason).toBe("unknown_invocation");
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
    dataDir: runtimeDir
  });

  const result = await client.ping();

  expect(result.ok).toBe(true);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("fromRuntime 应拒绝非法 requestTimeoutMs 类型", async () => {
  const runtimeDir = await createRuntime();

  await expect(DevHubClient.fromRuntime({
    clientId: "unit-timeout-client",
    dataDir: runtimeDir,
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
    dataDir: runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: "true" as unknown as boolean,
      respond: true
    }
  }, "secret-1")).rejects.toThrow("invoke.poll 必须为布尔值。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("registerInstance 应在本地拒绝空 password", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-password-client",
    dataDir: runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: "inst-1",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "")).rejects.toThrow("password 不能为空。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("heartbeat / unregisterInstance 应在本地拒绝空 instanceSessionToken", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-instance-session-token-client",
    dataDir: runtimeDir
  });

  await expect(client.heartbeat("inst-1", "")).rejects.toThrow("instanceSessionToken 不能为空。");
  await expect(client.unregisterInstance("inst-1", "")).rejects.toThrow("instanceSessionToken 不能为空。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("getDefinition should reject an invalid appId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-appid-client",
    dataDir: runtimeDir
  });

  await expect(client.getDefinition({
    appId: ".Invalid.App",
    scope: ""
  })).rejects.toThrow(/appId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("getInstance should reject an invalid instanceId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-instance-instanceid-client",
    dataDir: runtimeDir
  });

  await expect(client.getInstance("node-01-")).rejects.toThrow(/instanceId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("registerInstance should reject an invalid instanceId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-register-instanceid-client",
    dataDir: runtimeDir
  });

  await expect(client.registerInstance({
    instanceId: ".node-01",
    appId: "test.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  }, "secret-1")).rejects.toThrow(/instanceId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("notify 应在本地校验 target.instanceId 类型", async () => {
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
      scope: "",
      instanceId: 123 as unknown as string
    }
  })).rejects.toThrow("target.instanceId 类型非法。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it.each([
  ["notify", (client: DevHubClient) => client.notify({
    appId: "test.app",
    method: "test.notify",
    target: {
      scope: "",
      instanceId: "a".repeat(257)
    },
    options: {
      autoLaunch: false
    }
  })],
  ["request", (client: DevHubClient) => client.request({
    appId: "test.app",
    method: "test.request",
    target: {
      scope: "",
      instanceId: "a".repeat(257)
    },
    options: {
      autoLaunch: false
    }
  })]
])("%s 应在发送前拒绝超长 target.instanceId", async (_name, act) => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-target-instanceid-length-client",
    dataDir: runtimeDir
  });

  await expect(act(client)).rejects.toThrow(/target\.instanceId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("launch should reject null waitForRegisterMs before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-launch-null-wait-client",
    dataDir: runtimeDir
  });

  await expect(client.launch({
    appId: "test.app",
    scope: "",
    waitForRegisterMs: null as unknown as number
  })).rejects.toThrow(/waitForRegisterMs/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("launch should reject a null scope before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-launch-empty-scope-client",
    dataDir: runtimeDir
  });

  await expect(client.launch({
    appId: "test.app",
    scope: null as unknown as string
  })).rejects.toThrow(/scope/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("poll 应在本地校验 waitMs 为整数", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    waitMs: 1.5 as unknown as number
  })).rejects.toThrow("waitMs 必须为大于等于 0 的整数。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("poll / respond 应在本地拒绝空 instanceSessionToken", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-owned-rpc-session-token-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1",
    instanceSessionToken: "",
    waitMs: 0
  })).rejects.toThrow("instanceSessionToken 不能为空。");

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "",
    invocationId: "invk-1",
    leaseToken: "lease-1",
    value: {
      ok: true
    }
  })).rejects.toThrow("instanceSessionToken 不能为空。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("request should reject null queueIfOffline before sending the request", async () => {
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
    target: {
      scope: ""
    },
    options: {
      queueIfOffline: null as unknown as boolean
    }
  })).rejects.toThrow(/queueIfOffline/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("notify 应在本地拒绝 waitTimeoutMs", async () => {
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
    target: {
      scope: ""
    },
    options: {
      waitTimeoutMs: 1_000
    }
  })).rejects.toThrow("hub.invoke.notify 不支持 waitTimeoutMs。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond 应在本地校验 value 与 error 互斥", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    leaseToken: "lease-1"
  } as unknown as Parameters<typeof client.respond>[0])).rejects.toThrow("RespondRequest 必须且只能包含 value 或 error 之一。");

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    leaseToken: "lease-1",
    value: { ok: true },
    error: {
      code: 1001,
      message: "app_error"
    }
  } as unknown as Parameters<typeof client.respond>[0])).rejects.toThrow("RespondRequest 必须且只能包含 value 或 error 之一。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond 应在本地要求 leaseToken", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-lease-token-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    value: {
      ok: true
    }
  } as unknown as Parameters<typeof client.respond>[0])).rejects.toThrow("leaseToken 不能为空。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond should reject an invalid invocationId before sending the request", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-invocationid-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "bad-id",
    leaseToken: "lease-1",
    value: {
      ok: true
    }
  })).rejects.toThrow(/invocationId/);

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("ping 应在本地拒绝会被静默丢弃的 echo 字段", async () => {
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

it("registerInstance 应在本地拒绝会被静默丢弃的 meta 字段", async () => {
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
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    },
    meta: {
      callback: (() => "ignored") as any
    } as any
  }, "secret-1")).rejects.toThrow("meta.callback 包含不支持的 JSON 类型。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("notify 应在本地拒绝会被重写的空洞数组参数", async () => {
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
    target: {
      scope: ""
    },
    args: sparseArray as any
  })).rejects.toThrow("args[0] 不能为数组空洞。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("request should reject an invalid invocationId in a success payload", async () => {
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
    method: "test.request",
    target: {
      scope: ""
    }
  })).rejects.toThrow(/invocationId/);
});

it("respond 应在本地拒绝非法 error.data JSON 结构", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-json-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    leaseToken: "lease-1",
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

it("respond 应在本地拒绝非整数 error.code", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-error-code-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    leaseToken: "lease-1",
    error: {
      code: 1001.5,
      message: "app_error"
    }
  })).rejects.toThrow("error.code 必须为整数。");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("respond 应允许标量 error.data", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.params.error.data).toBe("boom");
    return createJsonResponse(body.id, { ok: true });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-respond-error-data-shape-client",
    dataDir: runtimeDir
  });

  await expect(client.respond({
    instanceId: "inst-1",
    instanceSessionToken: "session-1",
    invocationId: "invk-1",
    leaseToken: "lease-1",
    error: {
      code: 1001,
      message: "app_error",
      data: "boom"
    }
  })).resolves.toBeUndefined();

  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("launch 应拒绝 started 缺少 launchId 的成功载荷", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    return createJsonResponse(body.id, {
      ok: true,
      status: "started",
      pid: 12345,
      dedupeKey: "test.app:global"
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-launch-validation-client",
    dataDir: runtimeDir
  });

  await expect(client.launch({
    appId: "test.app",
    scope: ""
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
            scope: "",
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
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
  })).rejects.toThrow(/clientSessionId/);
});

it("poll 应拒绝非法 caller.clientSessionId 的调用项", async () => {
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
            scope: "",
            instanceId: null
          },
          method: "test.notify",
          kind: "notify",
          createdAtUtc: "2026-03-09T00:00:00Z",
          caller: {
            clientId: "caller-a",
            clientSessionId: "bad-client-session-id"
          }
        }
      ]
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-poll-invalid-client-session-id-client",
    dataDir: runtimeDir
  });

  await expect(client.poll({
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
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
    dataDir: runtimeDir
  });

  await expect(client.ping()).rejects.toThrow(/jsonrpc/i);
});

it("ping 应拒绝非整数 JSON-RPC error.code", async () => {
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

it("ping 应拒绝非对象 JSON-RPC error.data", async () => {
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

it("ping should reject a serverTimeUtc value that is not a full date-time", async () => {
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

it("notify should reject a non-object target before sending the request", async () => {
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
    target: "global" as unknown as { scope: string; instanceId?: string | null }
  })).rejects.toThrow("target must be an object.");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("request should reject a non-object options payload before sending the request", async () => {
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
    target: {
      scope: ""
    },
    options: 1 as unknown as {
      ttlMs?: number | null;
      waitTimeoutMs?: number | null;
      queueIfOffline?: boolean | null;
      autoLaunch?: boolean | null;
    }
  })).rejects.toThrow("options must be an object.");

  expect(fetchSpy).not.toHaveBeenCalled();
});

it("poll should reject an invocation item whose waitTimeoutMs exceeds ttlMs", async () => {
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
            scope: "",
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
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
  })).rejects.toThrow(/waitTimeoutMs/i);
});

it("getDefinition should reject blank displayName even when launch.exePath is blank", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");
    expect(body.params).toEqual({
      appId: "test.empty-fields.app",
      scope: ""
    });

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.empty-fields.app",
        scope: "",
        displayName: " ",
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

  await expect(client.getDefinition({
    appId: "test.empty-fields.app",
    scope: ""
  })).rejects.toThrow("hub.apps.getDefinition.result.definition.displayName must be a non-empty string.");
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("getDefinition should continue accepting blank launch.exePath when displayName is valid", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.blank-launch-exepath.app",
        scope: "",
        displayName: "Blank Launch ExePath App",
        launch: {
          exePath: ""
        }
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-definition-blank-launch-exepath-client",
    dataDir: runtimeDir
  });

  const definition = await client.getDefinition({
    appId: "test.blank-launch-exepath.app",
    scope: ""
  });

  expect(definition.launch?.exePath).toBe("");
  expect(definition.displayName).toBe("Blank Launch ExePath App");
});

it("getDefinition should reject null capabilities flags", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getDefinition");
    expect(body.params).toEqual({
      appId: "test.invalid-capabilities.app",
      scope: ""
    });

    return createJsonResponse(body.id, {
      ok: true,
      definition: {
        appId: "test.invalid-capabilities.app",
        scope: "",
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

  await expect(client.getDefinition({
    appId: "test.invalid-capabilities.app",
    scope: ""
  })).rejects.toThrow(/capabilities\.rpc/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("listInstances should reject a null meta object", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.listInstances");
    expect(body.params).toEqual({
      scope: ""
    });

    return createJsonResponse(body.id, {
      ok: true,
      instances: [
        {
          instanceId: "inst-1",
          appId: "test.app",
          scope: "",
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

  await expect(client.listInstances({
    scope: ""
  })).rejects.toThrow(/meta/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("listInstances 应拒绝注入 transport 返回的非法 meta JSON", async () => {
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
              scope: "",
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

  await expect(client.listInstances({
    scope: null
  })).rejects.toThrow(
    "hub.apps.listInstances.result.instances[0].meta.callback 包含不支持的 JSON 类型。"
  );
});

it("getDefinition 应将非法入站标识符视为 invalid_response", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-injected-invalid-identifier-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          definition: {
            appId: ".Invalid.App",
            scope: "",
            displayName: "Invalid App"
          }
        })
      })
    }
  );

  let captured: unknown;
  try {
    await client.getDefinition({
      appId: "Sample.App",
      scope: ""
    });
  } catch (error) {
    captured = error;
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect((captured as DevHubConnectionError).kind).toBe("invalid_response");
  expect((captured as Error).message).toMatch(/appId/);
});

it("getInstance should propagate instance_not_found without rewriting error data", async () => {
  const runtimeDir = await createRuntime();
  const fetchSpy = vi.fn(async (_input: unknown, init?: RequestInit) => {
    const body = parseRequestBody(init);
    expect(body.method).toBe("hub.apps.getInstance");
    expect(body.params).toEqual({
      instanceId: "missing-inst-1"
    });

    return createJsonResponse(body.id, undefined, {
      code: DevHubRpcErrorCode.InstanceNotFound,
      message: "instance_not_found",
      data: {
        reason: "unknown_instance",
        instanceId: "missing-inst-1"
      }
    });
  });
  vi.stubGlobal("fetch", fetchSpy);

  const client = await DevHubClient.fromRuntime({
    clientId: "unit-get-instance-not-found-client",
    dataDir: runtimeDir
  });

  let capturedError: unknown;
  try {
    await client.getInstance("missing-inst-1");
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(DevHubRpcErrorCode.InstanceNotFound);
  expect(rpcError.message).toBe("instance_not_found");
  expect(rpcError.reason).toBe("unknown_instance");
  expect(rpcError.tryGetDataString("instanceId")).toBe("missing-inst-1");
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("getInstance should reject an AppInstance payload containing instanceSessionToken", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-get-instance-token-leak-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          instance: {
            instanceId: "inst-1",
            appId: "test.app",
            scope: "",
            pid: 12345,
            registeredAtUtc: "2026-03-09T00:00:00Z",
            lastSeenUtc: "2026-03-09T00:00:01Z",
            invoke: {
              poll: true,
              respond: true
            },
            instanceSessionToken: "session-1"
          }
        })
      })
    }
  );

  await expect(client.getInstance("inst-1")).rejects.toThrow(/instanceSessionToken/i);
});

it("getInstance should reject an AppInstance payload containing password", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-get-instance-password-leak-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      transportFactory: () => ({
        send: async () => ({
          ok: true,
          instance: {
            instanceId: "inst-1",
            appId: "test.app",
            scope: "",
            pid: 12345,
            registeredAtUtc: "2026-03-09T00:00:00Z",
            lastSeenUtc: "2026-03-09T00:00:01Z",
            invoke: {
              poll: true,
              respond: true
            },
            password: "secret-1"
          }
        })
      })
    }
  );

  await expect(client.getInstance("inst-1")).rejects.toThrow(/password/i);
});

it("poll should reject null optional invocation booleans", async () => {
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
            scope: "",
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
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
  })).rejects.toThrow(/autoLaunch/i);
  expect(fetchSpy).toHaveBeenCalledTimes(1);
});

it("poll 应拒绝注入 transport 返回的非法 args JSON", async () => {
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
                scope: "",
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
    instanceId: "inst-1",
    instanceSessionToken: "session-1"
  })).rejects.toThrow("hub.invoke.poll.result.items[0].args.callback 包含不支持的 JSON 类型。");
});

type TestRuntimeConnectionInfo = ReturnType<typeof createBaseConnectionInfo>;

function createConnectionInfo(overrides: {
  runtime?: Partial<TestRuntimeConnectionInfo["runtime"]>;
  rpcEndpoint?: string;
  websocketEndpoint?: string;
} = {}): TestRuntimeConnectionInfo {
  const base = createBaseConnectionInfo();
  const runtime = {
    ...base.runtime,
    ...overrides.runtime
  };

  return {
    ...base,
    runtime,
    rpcEndpoint: overrides.rpcEndpoint ?? `${runtime.httpBaseUrl}/rpc`,
    websocketEndpoint: overrides.websocketEndpoint ?? runtime.wsUrl
  };
}

function createBaseConnectionInfo() {
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
      hubVersion: "0.7.0-test",
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 30,
        launchDedupeWindowSeconds: 30,
        launchRegisterTimeoutSeconds: 30
      }
    },
    rpcEndpoint: "http://127.0.0.1:57231/rpc",
    websocketEndpoint: "ws://127.0.0.1:57231/ws"
  };
}

async function createRuntime(): Promise<string> {
  const dataDir = await fsPromises.realpath(
    await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-unit-"))
  );
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
        launchDedupeWindowSeconds: 30,
        launchRegisterTimeoutSeconds: 30
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
