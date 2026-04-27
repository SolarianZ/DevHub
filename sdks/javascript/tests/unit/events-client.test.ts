import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { WebSocketServer } from "ws";
import type { AbandonedRequestFilter, JsonRpcEventSession } from "../../src/index.js";
import { DevHubClient } from "../../src/client.js";
import { DevHubEventsClient } from "../../src/events.js";
import {
  DevHubConnectionError,
  DevHubRpcError,
  DevHubRpcErrorCode,
} from "../../src/errors.js";
import type { NormalizedDevHubClientOptions } from "../../src/models.js";
import type { JsonRpcWsSessionOptions } from "../../src/ws-session.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.doUnmock("ws");
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("fromRuntime 应支持注入 runtimeResolver 与 sessionFactory", async () => {
  const connection = createConnectionInfo();
  const runtimeResolver = {
    resolve: vi.fn(async (options: Readonly<NormalizedDevHubClientOptions>) => {
      expect(options).toMatchObject({
        clientId: "unit-events-injected-client",
        dataDir: "/tmp/devhub-js-sdk-runtime",
        protocolVersion: 1
      });
      expect(options.clientSessionId).toEqual(expect.any(String));
      return connection;
    })
  };
  let session: FakeInjectedWsSession | undefined;
  const sessionFactory = vi.fn((options: JsonRpcWsSessionOptions) => {
    session = new FakeInjectedWsSession(options);
    return session;
  });

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-injected-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver,
      sessionFactory
    }
  );

  try {
    await client.authenticate();
    const subscriptionId = await client.subscribe(["invocation.completed"]);
    const first = await client.readEvents()[Symbol.asyncIterator]().next();

    expect(subscriptionId).toBe("sub-injected");
    expect(first.done).toBe(false);
    expect(first.value.type).toBe("invocation.completed");
    expect(first.value.payload?.invocationId).toBe("invk-fake-1");
    expect(runtimeResolver.resolve).toHaveBeenCalledTimes(1);
    expect(sessionFactory).toHaveBeenCalledTimes(1);
    expect(session?.requests.map((item) => item.method)).toEqual([
      "hub.ws.authenticate",
      "hub.events.subscribe"
    ]);
  } finally {
    await client.dispose();
  }

  expect(session?.disposedReason).toBe("client_dispose");
});

it("本地已放弃请求维护接口应直接委托 session 且不要求认证", async () => {
  const connection = createConnectionInfo();
  const ensureConnected = vi.fn(async () => {});
  const sendRequest = vi.fn(async () => {
    throw new Error("sendRequest should not be called.");
  });
  const getAbandonedRequestCount = vi.fn((filter?: AbandonedRequestFilter) => filter?.appId === "app-a" ? 2 : 3);
  const clearAbandonedRequests = vi.fn((filter?: AbandonedRequestFilter) => filter?.method === "hub.apps.listInstances" ? 1 : 0);

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-local-abandoned-request-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: () => createStubEventSession({
        ensureConnected,
        sendRequest,
        getAbandonedRequestCount,
        clearAbandonedRequests,
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    expect(client.getAbandonedRequestCount()).toBe(3);
    expect(client.getAbandonedRequestCount({ appId: "app-a" })).toBe(2);
    expect(client.clearAbandonedRequests({ method: "hub.apps.listInstances" })).toBe(1);
    expect(ensureConnected).not.toHaveBeenCalled();
    expect(sendRequest).not.toHaveBeenCalled();
    expect(getAbandonedRequestCount).toHaveBeenNthCalledWith(1, undefined);
    expect(getAbandonedRequestCount).toHaveBeenNthCalledWith(2, { appId: "app-a" });
    expect(clearAbandonedRequests).toHaveBeenCalledWith({ method: "hub.apps.listInstances" });
  } finally {
    await client.dispose();
  }
});

it("同一个 events client 实例一次只允许一个活动中的 readEvents 读取器，return 后应释放租约", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-single-reader-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => new FakeInjectedWsSession(options)
    }
  );

  try {
    await client.authenticate();

    const firstIterator = client.readEvents()[Symbol.asyncIterator]();
    expect(() => client.readEvents()[Symbol.asyncIterator]())
      .toThrow("Only one active readEvents() iterator is allowed per DevHubEventsClient instance.");

    await firstIterator.return?.();

    const secondIterator = client.readEvents()[Symbol.asyncIterator]();
    await expect(secondIterator.return?.()).resolves.toEqual({
      value: undefined,
      done: true
    });
  } finally {
    await client.dispose();
  }
});

it("HTTP/Events 客户端应复用默认 clientSessionId 并隐藏原始连接上下文", async () => {
  const connection = createConnectionInfo();
  const resolvedOptions: Readonly<NormalizedDevHubClientOptions>[] = [];
  const runtimeResolver = {
    resolve: vi.fn(async (options: Readonly<NormalizedDevHubClientOptions>) => {
      resolvedOptions.push(options);
      return connection;
    })
  };
  const transport = {
    send: vi.fn(async () => ({
      ok: true,
      serverTimeUtc: "2026-03-09T00:00:00Z"
    })),
    dispose: vi.fn(async () => {})
  };
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubClient.fromRuntime(
    {
      clientId: "unit-shared-http-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver,
      transportFactory: () => transport
    }
  );

  const eventsClient = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-shared-events-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver,
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.ping();
    await eventsClient.authenticate();
  } finally {
    await client.dispose();
    await eventsClient.dispose();
  }

  expect(resolvedOptions).toHaveLength(2);
  expect(resolvedOptions[0]?.clientSessionId).toBe(resolvedOptions[1]?.clientSessionId);
  expect(resolvedOptions[0]?.clientSessionId).toMatch(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
  );
  expect(client.options.clientSessionId).toBe(eventsClient.options.clientSessionId);
  expect(session?.requests[0]?.params).toMatchObject({
    clientSessionId: client.options.clientSessionId
  });
  expect(eventsClient.runtime).toEqual({
    protocolVersion: 1,
    pid: 12345,
    startedAtUtc: new Date("2026-03-09T00:00:00Z"),
    hubVersion: "0.7.0-test"
  });
  expect((eventsClient.runtime as unknown as Record<string, unknown>).httpBaseUrl).toBeUndefined();
  expect((eventsClient.runtime as unknown as Record<string, unknown>).wsUrl).toBeUndefined();
  expect((eventsClient.runtime as unknown as Record<string, unknown>).tokenFile).toBeUndefined();
  expect((client as unknown as Record<string, unknown>).connection).toBeUndefined();
  expect((eventsClient as unknown as Record<string, unknown>).connection).toBeUndefined();
});

it("authenticate should support WS ping and apps queries", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-ws-rpc-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();

    const ping = await client.ping({
      channel: "ws"
    });
    const definitions = await client.listDefinitions({
      scope: null
    });
    const definition = await client.getDefinition({
      appId: "test.launch.app",
      scope: ""
    });
    const instances = await client.listInstances({
      appId: "test.launch.app",
      scope: null,
      includeOffline: false
    });
    const instance = await client.getInstance("inst-1");

    expect(ping.echo).toEqual({
      channel: "ws"
    });
    expect(definitions).toHaveLength(1);
    expect(definitions[0].appId).toBe("test.launch.app");
    expect(definition.displayName).toBe("Test Launch App");
    expect(instances).toHaveLength(1);
    expect(instances[0].instanceId).toBe("inst-1");
    expect(instance.instanceId).toBe("inst-1");
    expect(session?.requests.map((item) => item.method)).toEqual([
      "hub.ws.authenticate",
      "hub.ping",
      "hub.apps.listDefinitions",
      "hub.apps.getDefinition",
      "hub.apps.listInstances",
      "hub.apps.getInstance"
    ]);
    expect(session?.requests[1]?.params).toEqual({
      echo: {
        channel: "ws"
      }
    });
    expect(session?.requests[3]?.params).toEqual({
      appId: "test.launch.app",
      scope: ""
    });
    expect(session?.requests[4]?.params).toEqual({
      appId: "test.launch.app",
      scope: null,
      includeOffline: false
    });
    expect(session?.requests[5]?.params).toEqual({
      instanceId: "inst-1"
    });
  } finally {
    await client.dispose();
  }
});

it("getInstance should reject an invalid instanceId before sending the WS request", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-instance-id-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    await expect(client.getInstance("node-01-")).rejects.toThrow(/instanceId/);
  } finally {
    await client.dispose();
  }

  expect(session?.requests.map((item) => item.method)).toEqual(["hub.ws.authenticate"]);
});

it("getDefinition 应将 WS 非法入站标识符视为 invalid_response", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-identifier-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: () => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.apps.getDefinition") {
            expect(params).toEqual({
              appId: "Sample.App",
              scope: ""
            });

            return {
              ok: true,
              definition: {
                appId: ".Invalid.App",
                scope: "",
                displayName: "Invalid App"
              }
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  let captured: unknown;
  try {
    await client.authenticate();
    await client.getDefinition({
      appId: "Sample.App",
      scope: ""
    });
  } catch (error) {
    captured = error;
  } finally {
    await client.dispose();
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect((captured as DevHubConnectionError).kind).toBe("invalid_response");
  expect((captured as Error).message).toMatch(/appId/);
});

it("getInstance should propagate instance_not_found over WS without rewriting the error", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-get-instance-not-found-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: () => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.apps.getInstance") {
            expect(params).toEqual({
              instanceId: "missing-inst-1"
            });
            throw new DevHubRpcError({
              code: DevHubRpcErrorCode.InstanceNotFound,
              message: "instance_not_found",
              data: {
                reason: "unknown_instance",
                instanceId: "missing-inst-1"
              },
              requestId: "ws-get-instance-missing"
            });
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  let capturedError: unknown;
  try {
    await client.authenticate();
    await client.getInstance("missing-inst-1");
  } catch (error) {
    capturedError = error;
  } finally {
    await client.dispose();
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(DevHubRpcErrorCode.InstanceNotFound);
  expect(rpcError.message).toBe("instance_not_found");
  expect(rpcError.reason).toBe("unknown_instance");
  expect(rpcError.tryGetDataString("instanceId")).toBe("missing-inst-1");
});

it("事件流应拒绝注入 session 返回的非法 payload JSON", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-payload-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.events.subscribe") {
            options.onEvent?.({
              subscriptionId: "sub-invalid",
              type: "invocation.completed",
              timeUtc: "2026-03-09T00:00:00Z",
              payload: {
                callback: (() => "ignored") as any
              }
            });

            return {
              ok: true,
              subscriptionId: "sub-invalid"
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    await expect(client.subscribe(["invocation.completed"]))
      .rejects
      .toThrow("hub.event.params.payload.callback 包含不支持的 JSON 类型。");
  } finally {
    await client.dispose();
  }
});

it("定义事件应拒绝缺失结构化 payload 的通知", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-definition-payload-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.events.subscribe") {
            options.onEvent?.({
              subscriptionId: "sub-invalid-definition",
              type: "app.definition.upserted",
              timeUtc: "2026-03-09T00:00:00Z",
              payload: {
                appId: "test.app",
                scope: ""
              }
            });

            return {
              ok: true,
              subscriptionId: "sub-invalid-definition"
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    await expect(client.subscribe(["app.definition.upserted"]))
      .rejects
      .toThrow(/definition/i);
  } finally {
    await client.dispose();
  }
});

it("实例事件应拒绝包含 password 的 payload", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-password-leak-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.events.subscribe") {
            options.onEvent?.({
              subscriptionId: "sub-instance-password",
              type: "app.instance.registered",
              timeUtc: "2026-03-09T00:00:00Z",
              payload: {
                appId: "test.app",
                instanceId: "inst-1",
                scope: "",
                password: "secret-1"
              }
            });

            return {
              ok: true,
              subscriptionId: "sub-instance-password"
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    await expect(client.subscribe(["app.instance.registered"]))
      .rejects
      .toThrow(/password/i);
  } finally {
    await client.dispose();
  }
});

it("实例事件应接受省略 scope 的 payload", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-omitted-scope-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.events.subscribe") {
            options.onEvent?.({
              subscriptionId: "sub-instance-omitted-scope",
              type: "app.instance.registered",
              timeUtc: "2026-03-09T00:00:00Z",
              payload: {
                appId: "test.app",
                instanceId: "inst-1"
              }
            });

            return {
              ok: true,
              subscriptionId: "sub-instance-omitted-scope"
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    const iterator = client.readEvents()[Symbol.asyncIterator]();
    await client.subscribe(["app.instance.registered"]);

    const first = await iterator.next();
    expect(first.done).toBe(false);
    expect(first.value.type).toBe("app.instance.registered");
    expect(first.value.payload).toEqual({
      appId: "test.app",
      instanceId: "inst-1"
    });
  } finally {
    await client.dispose();
  }
});

it("实例事件应继续拒绝非法 scope", async () => {
  const connection = createConnectionInfo();

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-scope-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => createStubEventSession({
        async ensureConnected(): Promise<void> {
        },
        async sendRequest(method: string): Promise<Record<string, unknown>> {
          if (method === "hub.ws.authenticate") {
            return {
              ok: true,
              protocolVersion: 1
            };
          }

          if (method === "hub.events.subscribe") {
            options.onEvent?.({
              subscriptionId: "sub-instance-invalid-scope",
              type: "app.instance.registered",
              timeUtc: "2026-03-09T00:00:00Z",
              payload: {
                appId: "test.app",
                instanceId: "inst-1",
                scope: ".invalid-scope"
              }
            });

            return {
              ok: true,
              subscriptionId: "sub-instance-invalid-scope"
            };
          }

          throw new Error(`unexpected method: ${method}`);
        },
        async disconnect(): Promise<void> {
        },
        async dispose(): Promise<void> {
        }
      })
    }
  );

  try {
    await client.authenticate();
    let captured: unknown;
    try {
      await client.subscribe(["app.instance.registered"]);
    } catch (error) {
      captured = error;
    }

    expect(captured).toBeInstanceOf(DevHubConnectionError);
    expect((captured as DevHubConnectionError).kind).toBe("invalid_response");
    expect((captured as Error).message).toMatch(/scope/i);
  } finally {
    await client.dispose();
  }
});

it("断线后重新认证应重建事件流并要求重新订阅", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-reconnect-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    const previousIterator = client.readEvents()[Symbol.asyncIterator]();
    await client.subscribe(["invocation.completed"]);
    session?.terminate(new Error("socket_closed"));
    const buffered = await previousIterator.next();
    expect(buffered.done).toBe(false);
    expect(buffered.value.payload?.invocationId).toBe("invk-fake-1");
    await expect(previousIterator.next()).resolves.toEqual({
      value: undefined,
      done: true
    });
    expect(() => client.readEvents()).toThrow("The event stream is unavailable. Re-authenticate and subscribe again.");

    await client.authenticate();
    await client.subscribe(["invocation.completed"]);

    const second = await client.readEvents()[Symbol.asyncIterator]().next();
    expect(second.done).toBe(false);
    expect(second.value.type).toBe("invocation.completed");
    expect(second.value.payload?.invocationId).toBe("invk-fake-2");
  } finally {
    await client.dispose();
  }
});

it("断线后即使不继续消费旧 iterator 也应允许重新认证恢复", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-reconnect-with-abandoned-iterator-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    const abandonedIterator = client.readEvents()[Symbol.asyncIterator]();
    await client.subscribe(["invocation.completed"]);
    session?.terminate(new Error("socket_closed"));

    expect(() => client.readEvents()).toThrow("The event stream is unavailable. Re-authenticate and subscribe again.");

    await client.authenticate();
    const recoveredIterator = client.readEvents()[Symbol.asyncIterator]();
    const recoveredEventTask = recoveredIterator.next();
    await client.subscribe(["invocation.completed"]);

    const recovered = await recoveredEventTask;
    expect(recovered.done).toBe(false);
    expect(recovered.value.type).toBe("invocation.completed");
    expect(recovered.value.payload?.invocationId).toBe("invk-fake-2");

    await recoveredIterator.return?.();
    await abandonedIterator.return?.();
  } finally {
    await client.dispose();
  }
});

it("旧 iterator 的收尾动作不应释放新 reader 租约", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-stale-iterator-cleanup-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    const abandonedIterator = client.readEvents()[Symbol.asyncIterator]();
    await client.subscribe(["invocation.completed"]);
    session?.terminate(new Error("socket_closed"));
    await client.authenticate();

    const recoveredIterator = client.readEvents()[Symbol.asyncIterator]();
    await expect(abandonedIterator.return?.()).resolves.toEqual({
      value: undefined,
      done: true
    });
    expect(() => client.readEvents()[Symbol.asyncIterator]())
      .toThrow("Only one active readEvents() iterator is allowed per DevHubEventsClient instance.");

    const recoveredEventTask = recoveredIterator.next();
    await client.subscribe(["invocation.completed"]);

    const recovered = await recoveredEventTask;
    expect(recovered.done).toBe(false);
    expect(recovered.value.payload?.invocationId).toBe("invk-fake-2");
    await recoveredIterator.return?.();
  } finally {
    await client.dispose();
  }
});

it("应在认证前拒绝 subscribe 和 readEvents", async () => {
  const runtimeDir = await createRuntime();
  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-unauthenticated-client",
    dataDir: runtimeDir
  });

  await expect(client.subscribe()).rejects.toThrow();
  await expect(client.ping()).rejects.toThrow();
  await expect(client.listDefinitions({
    scope: null
  })).rejects.toThrow();
  await expect(client.getDefinition({
    appId: "test.app",
    scope: ""
  })).rejects.toThrow();
  await expect(client.listInstances({
    scope: null
  })).rejects.toThrow();
  await expect(client.getInstance("inst-1")).rejects.toThrow();
  expect(() => client.readEvents()).toThrow();
});

it("subscribe 应在发送请求前拒绝未知事件类型", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-invalid-subscribe-type-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    await expect(client.subscribe(["future.event" as any])).rejects.toThrow(/supported DevHub event type/i);
  } finally {
    await client.dispose();
  }

  expect(session?.requests.map((item) => item.method)).toEqual(["hub.ws.authenticate"]);
});

it("连接关闭后仍应允许读取已缓冲事件", async () => {
  const runtimeDir = await createRuntime();
  const sockets: FakeWebSocket[] = [];
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, sockets, createDefaultScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-buffered-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  const subscriptionId = await client.subscribe(["invocation.completed"]);
  expect(sockets).toHaveLength(1);
  await sockets[0].waitForClose();

  const first = await iterator.next();
  expect(first.done).toBe(false);
  expect(first.value.subscriptionId).toBe(subscriptionId);
  expect(first.value.type).toBe("invocation.completed");
  expect(first.value.payload).toEqual({
    invocationId: "invk-1"
  });

  const second = await iterator.next();
  expect(second.done).toBe(true);
});

it("连接终止后新的 readEvents 应抛出 session_terminated typed connection error", async () => {
  const connection = createConnectionInfo();
  let session: FakeInjectedWsSession | undefined;

  const client = await DevHubEventsClient.fromRuntime(
    {
      clientId: "unit-events-terminated-stream-client",
      dataDir: "/tmp/devhub-js-sdk-runtime"
    },
    {
      runtimeResolver: {
        resolve: async () => connection
      },
      sessionFactory: (options) => {
        session = new FakeInjectedWsSession(options);
        return session;
      }
    }
  );

  try {
    await client.authenticate();
    const iterator = client.readEvents()[Symbol.asyncIterator]();
    await client.subscribe(["invocation.completed"]);
    session?.terminate(new Error("socket_closed"));

    const buffered = await iterator.next();
    expect(buffered.done).toBe(false);
    expect(buffered.value.payload?.invocationId).toBe("invk-fake-1");
    await expect(iterator.next()).resolves.toEqual({
      value: undefined,
      done: true
    });

    let captured: unknown;
    try {
      client.readEvents();
    } catch (error) {
      captured = error;
    }

    expect(captured).toBeInstanceOf(DevHubConnectionError);
    expect((captured as DevHubConnectionError).kind).toBe("session_terminated");
  } finally {
    await client.dispose();
  }
});

it("事件通知携带 id 时应使事件流报错", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createMalformedEventScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-invalid-notification-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await client.subscribe(["invocation.completed"]);

  let captured: unknown;
  try {
    await iterator.next();
  } catch (error) {
    captured = error;
  }

  expect(captured).toBeInstanceOf(DevHubConnectionError);
  expect((captured as DevHubConnectionError).kind).toBe("invalid_response");
  expect((captured as Error).message).toMatch(/hub\.event/i);

  let streamError: unknown;
  try {
    client.readEvents();
  } catch (error) {
    streamError = error;
  }

  expect(streamError).toBe(captured);
});

it("收到空白文本消息时应使事件流报错", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createBlankTextScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-blank-message-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await client.subscribe(["invocation.completed"]);

  await expect(iterator.next()).rejects.toThrow(/blank/i);
});

it("响应 id 未匹配挂起请求时应中断 authenticate", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createUnexpectedAuthenticateResponseIdScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-unexpected-response-id-client",
    dataDir: runtimeDir,
    requestTimeoutMs: 1_000
  });

  await expect(client.authenticate()).rejects.toThrow(/pending request/i);
});

it("收到未知 WS 通知方法时应使事件流报错", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createUnknownNotificationScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-unknown-notification-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await client.subscribe(["invocation.completed"]);

  await expect(iterator.next()).rejects.toThrow(/supported response or hub\.event/i);
});

it("收到未知事件类型时应使事件流报错", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createUnknownEventTypeScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-unknown-type-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await client.subscribe(["invocation.completed"]);

  await expect(iterator.next()).rejects.toThrow(/supported DevHub event type/i);
});

it("缺少全局 WebSocket 时收到 binary frame 应使事件流报错", async () => {
  vi.stubGlobal("WebSocket", undefined as unknown as typeof WebSocket);

  const server = new WebSocketServer({
    host: "127.0.0.1",
    port: 0,
    path: "/ws"
  });

  try {
    await waitForWebSocketServer(server);

    const address = server.address();
    if (!address || typeof address === "string") {
      throw new Error("无法获取测试 WebSocket 端口。");
    }

    const runtimeDir = await createRuntime({
      httpBaseUrl: `http://127.0.0.1:${address.port}`,
      wsUrl: `ws://127.0.0.1:${address.port}/ws`
    });

    server.once("connection", (socket: any) => {
      socket.on("message", (data: Buffer) => {
        const request = JSON.parse(data.toString("utf-8")) as Record<string, unknown>;
        const method = String(request.method);

        if (method === "hub.ws.authenticate") {
          socket.send(JSON.stringify({
            jsonrpc: "2.0",
            id: String(request.id),
            result: {
              ok: true,
              protocolVersion: 1
            }
          }));
          return;
        }

        if (method === "hub.events.subscribe") {
          socket.send(JSON.stringify({
            jsonrpc: "2.0",
            id: String(request.id),
            result: {
              ok: true,
              subscriptionId: "sub-1"
            }
          }));
          socket.send(Buffer.from([0x01, 0x02, 0x03]), { binary: true });
        }
      });
    });

    const client = await DevHubEventsClient.fromRuntime({
      clientId: "unit-events-fallback-binary-client",
      dataDir: runtimeDir
    });

    try {
      await client.authenticate();
      const iterator = client.readEvents()[Symbol.asyncIterator]();
      await client.subscribe(["invocation.completed"]);
      await expect(iterator.next()).rejects.toThrow(/text frame/i);
    } finally {
      await client.dispose();
    }
  } finally {
    await closeWebSocketServer(server);
  }
});

it("authenticate 应映射 DevHub RPC 错误", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createAuthenticateErrorScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-auth-error-client",
    dataDir: runtimeDir
  });

  let capturedError: unknown;
  try {
    await client.authenticate();
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(DevHubRpcErrorCode.Unauthorized);
  expect(rpcError.reason).toBe("invalid_token");
});

it("authenticate 应拒绝非法 JSON-RPC 版本", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createInvalidAuthenticateEnvelopeScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-auth-envelope-client",
    dataDir: runtimeDir
  });

  await expect(client.authenticate()).rejects.toThrow(/jsonrpc/i);
});

it("缺少全局 WebSocket 时应回退到 ws 模块", async () => {
  vi.stubGlobal("WebSocket", undefined as unknown as typeof WebSocket);

  const server = new WebSocketServer({
    host: "127.0.0.1",
    port: 0,
    path: "/ws"
  });

  try {
    await waitForWebSocketServer(server);

    const address = server.address();
    if (!address || typeof address === "string") {
      throw new Error("无法获取测试 WebSocket 端口。");
    }

    const runtimeDir = await createRuntime({
      httpBaseUrl: `http://127.0.0.1:${address.port}`,
      wsUrl: `ws://127.0.0.1:${address.port}/ws`
    });

    const authenticateRequestPromise = new Promise<Record<string, unknown>>((resolve, reject) => {
      server.once("connection", (socket: any) => {
        socket.once("error", reject);
        socket.once("message", (data: Buffer) => {
          const request = JSON.parse(data.toString("utf-8")) as Record<string, unknown>;
          socket.send(JSON.stringify({
            jsonrpc: "2.0",
            id: String(request.id),
            result: {
              ok: true,
              protocolVersion: 1
            }
          }));
          resolve(request);
        });
      });
    });

    const client = await DevHubEventsClient.fromRuntime({
      clientId: "unit-events-ws-fallback-client",
      dataDir: runtimeDir
    });

    try {
      await client.authenticate();
      const request = await authenticateRequestPromise;
      expect(request.method).toBe("hub.ws.authenticate");
      expect(request.params).toMatchObject({
        token: "token-1",
        protocolVersion: 1,
        clientId: "unit-events-ws-fallback-client"
      });
    } finally {
      await client.dispose();
    }
  } finally {
    await closeWebSocketServer(server);
  }
});

it("缺少全局 WebSocket 且 ws 不可用时 authenticate 应返回可读错误", async () => {
  vi.stubGlobal("WebSocket", undefined as unknown as typeof WebSocket);
  vi.doMock("ws", () => {
    throw new Error("Cannot find package 'ws'.");
  });
  vi.resetModules();

  const { DevHubEventsClient: DynamicEventsClient } = await import("../../src/events.js");
  const client = await DynamicEventsClient.fromRuntime({
    clientId: "unit-events-missing-ws-client",
    dataDir: await createRuntime()
  });

  try {
    await expect(client.authenticate())
      .rejects
      .toThrow("WebSocket 不可用。请安装 ws，或提供全局 WebSocket 实现。");
  } finally {
    await client.dispose();
  }
});

it("fromRuntime 应拒绝空 options", async () => {
  await expect(
    DevHubEventsClient.fromRuntime(null as unknown as Parameters<typeof DevHubEventsClient.fromRuntime>[0])
  ).rejects.toThrow(/options/i);
});

it("event notifications should reject a null payload object", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createNullEventPayloadScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-null-payload-client",
    dataDir: runtimeDir
  });

  await client.authenticate();
  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await client.subscribe(["invocation.completed"]);

  await expect(iterator.next()).rejects.toThrow(/payload/i);
});

async function createRuntime(overrides?: {
  httpBaseUrl?: string;
  wsUrl?: string;
}): Promise<string> {
  const dataDir = await fsPromises.realpath(
    await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-events-unit-"))
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
      httpBaseUrl: overrides?.httpBaseUrl ?? "http://127.0.0.1:47231",
      wsUrl: overrides?.wsUrl ?? "ws://127.0.0.1:47231/ws",
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
      hubVersion: "0.7.0-test",
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

async function waitForWebSocketServer(server: any): Promise<void> {
  if (server.address()) {
    return;
  }

  await new Promise<void>((resolve, reject) => {
    const onListening = () => {
      cleanup();
      resolve();
    };
    const onError = (error: Error) => {
      cleanup();
      reject(error);
    };
    const cleanup = () => {
      server.off("listening", onListening);
      server.off("error", onError);
    };

    server.on("listening", onListening);
    server.on("error", onError);
  });
}

async function closeWebSocketServer(server: any): Promise<void> {
  for (const client of server.clients) {
    client.terminate();
  }

  await new Promise<void>((resolve, reject) => {
    server.close((error: Error | undefined) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });
}

function createStubEventSession(
  session: Omit<JsonRpcEventSession, "getAbandonedRequestCount" | "clearAbandonedRequests">
    & Partial<Pick<JsonRpcEventSession, "getAbandonedRequestCount" | "clearAbandonedRequests">>
): JsonRpcEventSession {
  return {
    getAbandonedRequestCount: () => 0,
    clearAbandonedRequests: () => 0,
    ...session
  };
}

class FakeInjectedWsSession {
  readonly requests: Array<{ method: string; params?: Record<string, unknown> }> = [];
  disposedReason: string | undefined;

  constructor(private readonly options: JsonRpcWsSessionOptions) {
  }

  async ensureConnected(): Promise<void> {
  }

  async sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>> {
    this.requests.push({ method, params });

    if (method === "hub.ws.authenticate") {
      return {
        ok: true,
        protocolVersion: 1
      };
    }

    if (method === "hub.events.subscribe") {
      const eventIndex = this.requests.filter((item) => item.method === "hub.events.subscribe").length;
      this.options.onEvent?.({
        subscriptionId: "sub-injected",
        type: "invocation.completed",
        timeUtc: "2026-03-09T00:00:00Z",
        payload: {
          invocationId: `invk-fake-${eventIndex}`
        }
      });

      return {
        ok: true,
        subscriptionId: "sub-injected"
      };
    }

    if (method === "hub.ping") {
      return {
        ok: true,
        serverTimeUtc: "2026-03-09T00:00:00Z",
        echo: params?.echo
      };
    }

    if (method === "hub.apps.listDefinitions") {
      return {
        ok: true,
        definitions: [
          {
            appId: "test.launch.app",
            scope: "",
            displayName: "Test Launch App"
          }
        ]
      };
    }

    if (method === "hub.apps.getDefinition") {
      return {
        ok: true,
        definition: {
          appId: "test.launch.app",
          scope: "",
          displayName: "Test Launch App",
          launch: {
            exePath: process.execPath
          }
        }
      };
    }

    if (method === "hub.apps.listInstances") {
      return {
        ok: true,
        instances: [
          {
            instanceId: "inst-1",
            appId: "test.launch.app",
            scope: "",
            pid: 12345,
            registeredAtUtc: "2026-03-09T00:00:00Z",
            lastSeenUtc: "2026-03-09T00:00:01Z",
            invoke: {
              poll: true,
              respond: true
            }
          }
        ]
      };
    }

    if (method === "hub.apps.getInstance") {
      return {
        ok: true,
        instance: {
          instanceId: "inst-1",
          appId: "test.launch.app",
          scope: "",
          pid: 12345,
          registeredAtUtc: "2026-03-09T00:00:00Z",
          lastSeenUtc: "2026-03-09T00:00:01Z",
          invoke: {
            poll: true,
            respond: true
          }
        }
      };
    }

    if (method === "hub.events.unsubscribe") {
      return {
        ok: true
      };
    }

    throw new Error(`unexpected method: ${method}`);
  }

  async disconnect(): Promise<void> {
  }

  getAbandonedRequestCount(): number {
    return 0;
  }

  clearAbandonedRequests(): number {
    return 0;
  }

  async dispose(reason = "client_dispose"): Promise<void> {
    this.disposedReason = reason;
  }

  terminate(error?: Error): void {
    this.options.onTerminate?.(error);
  }
}

type ServerFrame =
  | { type: "message"; payload: string | Uint8Array; isBinary?: boolean }
  | { type: "close"; code?: number; reason?: string };

type ScenarioFactory = (request: Record<string, unknown>) => ServerFrame[];

class FakeWebSocket {
  private readonly listeners = new Map<string, Set<(event: unknown, ...args: unknown[]) => void>>();
  private readonly closePromise: Promise<void>;
  private closeResolver: (() => void) | undefined;
  private closed = false;

  constructor(
    readonly url: string,
    sink: FakeWebSocket[],
    private readonly scenarioFactory: ScenarioFactory
  ) {
    sink.push(this);
    this.closePromise = new Promise<void>((resolve) => {
      this.closeResolver = resolve;
    });

    queueMicrotask(() => {
      this.emit("open", { type: "open" });
    });
  }

  addEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    if (!this.listeners.has(type)) {
      this.listeners.set(type, new Set());
    }

    this.listeners.get(type)!.add(listener);
  }

  removeEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  send(data: string): void {
    const request = JSON.parse(data) as Record<string, unknown>;
    for (const frame of this.scenarioFactory(request)) {
      queueMicrotask(() => {
        if (frame.type === "message") {
          this.emit("message", { data: frame.payload }, frame.isBinary ?? false);
          return;
        }

        this.close(frame.code ?? 1000, frame.reason ?? "server_close");
      });
    }
  }

  close(code?: number, reason?: string): void {
    if (this.closed) {
      return;
    }

    this.closed = true;
    this.emit("close", { code: code ?? 1000, reason: reason ?? "" });
    this.closeResolver?.();
  }

  async waitForClose(): Promise<void> {
    await this.closePromise;
  }

  private emit(type: string, event: unknown, ...args: unknown[]): void {
    const listeners = this.listeners.get(type);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(event, ...args);
    }
  }
}

function createDefaultScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            protocolVersion: 1
          }
        })
      }
    ];
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.event",
          params: {
            subscriptionId: "sub-1",
            type: "invocation.completed",
            timeUtc: "2026-03-09T00:00:00Z",
            payload: {
              invocationId: "invk-1"
            }
          }
        })
      },
      {
        type: "close",
        reason: "done"
      }
    ];
  }

  return [];
}

function createMalformedEventScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return createDefaultScenario(request);
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: "evt-1",
          method: "hub.event",
          params: {
            subscriptionId: "sub-1",
            type: "invocation.completed",
            timeUtc: "2026-03-09T00:00:00Z",
            payload: {
              invocationId: "invk-1"
            }
          }
        })
      }
    ];
  }

  return [];
}

function createBlankTextScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return createDefaultScenario(request);
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: "   "
      }
    ];
  }

  return [];
}

function createAuthenticateErrorScenario(request: Record<string, unknown>): ServerFrame[] {
  if (String(request.method) !== "hub.ws.authenticate") {
    return [];
  }

  return [
    {
      type: "message",
      payload: JSON.stringify({
        jsonrpc: "2.0",
        id: String(request.id),
        error: {
          code: DevHubRpcErrorCode.Unauthorized,
          message: "unauthorized",
          data: {
            reason: "invalid_token"
          }
        }
      })
    }
  ];
}

function createUnexpectedAuthenticateResponseIdScenario(request: Record<string, unknown>): ServerFrame[] {
  if (String(request.method) !== "hub.ws.authenticate") {
    return [];
  }

  return [
    {
      type: "message",
      payload: JSON.stringify({
        jsonrpc: "2.0",
        id: `${String(request.id)}-unexpected`,
        result: {
          ok: true,
          protocolVersion: 1
        }
      })
    }
  ];
}

function createInvalidAuthenticateEnvelopeScenario(request: Record<string, unknown>): ServerFrame[] {
  if (String(request.method) !== "hub.ws.authenticate") {
    return [];
  }

  return [
    {
      type: "message",
      payload: JSON.stringify({
        jsonrpc: "1.0",
        id: String(request.id),
        result: {
          ok: true,
          protocolVersion: 1
        }
      })
    }
  ];
}

function createUnknownNotificationScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return createDefaultScenario(request);
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.events.unknown",
          params: {
            subscriptionId: "sub-1"
          }
        })
      }
    ];
  }

  return [];
}

function createNullEventPayloadScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return createDefaultScenario(request);
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.event",
          params: {
            subscriptionId: "sub-1",
            type: "invocation.completed",
            timeUtc: "2026-03-09T00:00:00Z",
            payload: null
          }
        })
      }
    ];
  }

  return [];
}

function createUnknownEventTypeScenario(request: Record<string, unknown>): ServerFrame[] {
  const requestId = String(request.id);
  const method = String(request.method);

  if (method === "hub.ws.authenticate") {
    return createDefaultScenario(request);
  }

  if (method === "hub.events.subscribe") {
    return [
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          result: {
            ok: true,
            subscriptionId: "sub-1"
          }
        })
      },
      {
        type: "message",
        payload: JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.event",
          params: {
            subscriptionId: "sub-1",
            type: "unknown.type",
            timeUtc: "2026-03-09T00:00:00Z",
            payload: {
              invocationId: "invk-1"
            }
          }
        })
      }
    ];
  }

  return [];
}
