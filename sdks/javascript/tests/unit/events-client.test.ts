import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { WebSocketServer } from "ws";
import { DevHubEventsClient } from "../../src/events.js";
import { DevHubRpcError, DevHubRpcErrorCode } from "../../src/errors.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("应在认证前拒绝 subscribe 和 readEvents", async () => {
  const runtimeDir = await createRuntime();
  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-unauthenticated-client",
    runtimeDir
  });

  await expect(client.subscribe()).rejects.toThrow();
  expect(() => client.readEvents()).toThrow();
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
    runtimeDir
  });

  await client.authenticate();
  const subscriptionId = await client.subscribe(["invocation.completed"]);
  expect(sockets).toHaveLength(1);
  await sockets[0].waitForClose();

  const iterator = client.readEvents()[Symbol.asyncIterator]();
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

it("事件通知携带 id 时应使事件流报错", async () => {
  const runtimeDir = await createRuntime();
  vi.stubGlobal("WebSocket", class extends FakeWebSocket {
    constructor(url: string) {
      super(url, [], createMalformedEventScenario);
    }
  });

  const client = await DevHubEventsClient.fromRuntime({
    clientId: "unit-events-invalid-notification-client",
    runtimeDir
  });

  await client.authenticate();
  await client.subscribe(["invocation.completed"]);

  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await expect(iterator.next()).rejects.toThrow(/hub\.event/i);
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
    runtimeDir,
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
    runtimeDir
  });

  await client.authenticate();
  await client.subscribe(["invocation.completed"]);

  const iterator = client.readEvents()[Symbol.asyncIterator]();
  await expect(iterator.next()).rejects.toThrow(/supported response or hub\.event/i);
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
    runtimeDir
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
    runtimeDir
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
      server.once("connection", (socket) => {
        socket.once("error", reject);
        socket.once("message", (data) => {
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
      runtimeDir
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

it("fromRuntime 应拒绝空 options", async () => {
  await expect(
    DevHubEventsClient.fromRuntime(null as unknown as Parameters<typeof DevHubEventsClient.fromRuntime>[0])
  ).rejects.toThrow(/options/i);
});

async function createRuntime(overrides?: {
  httpBaseUrl?: string;
  wsUrl?: string;
}): Promise<string> {
  const runtimeDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-events-unit-"));
  tempRoots.push(runtimeDir);

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

  return runtimeDir;
}

async function waitForWebSocketServer(server: WebSocketServer): Promise<void> {
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

async function closeWebSocketServer(server: WebSocketServer): Promise<void> {
  for (const client of server.clients) {
    client.terminate();
  }

  await new Promise<void>((resolve, reject) => {
    server.close((error) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });
}

type ServerFrame =
  | { type: "message"; payload: string }
  | { type: "close"; code?: number; reason?: string };

type ScenarioFactory = (request: Record<string, unknown>) => ServerFrame[];

class FakeWebSocket {
  private readonly listeners = new Map<string, Set<(event: unknown) => void>>();
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

  addEventListener(type: string, listener: (event: unknown) => void): void {
    if (!this.listeners.has(type)) {
      this.listeners.set(type, new Set());
    }

    this.listeners.get(type)!.add(listener);
  }

  removeEventListener(type: string, listener: (event: unknown) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  send(data: string): void {
    const request = JSON.parse(data) as Record<string, unknown>;
    for (const frame of this.scenarioFactory(request)) {
      queueMicrotask(() => {
        if (frame.type === "message") {
          this.emit("message", { data: frame.payload });
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

  private emit(type: string, event: unknown): void {
    const listeners = this.listeners.get(type);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(event);
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
