import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { DevHubEventsClient } from "../../src/client.js";

const tempRoots: string[] = [];

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();

  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("readEvents 与 subscribe 应在鉴权前拒绝访问", async () => {
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

async function createRuntime(): Promise<string> {
  const runtimeDir = await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-events-unit-"));
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
