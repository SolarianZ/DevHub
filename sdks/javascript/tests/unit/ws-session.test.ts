import { afterEach, expect, it, vi } from "vitest";

afterEach(() => {
  vi.useRealTimers();
  vi.doUnmock("ws");
  vi.unstubAllGlobals();
  vi.resetModules();
  vi.restoreAllMocks();
  ControlledWebSocket.instances = [];
});

it("全局 WebSocket 应优先处理文本帧且不加载 ws", async () => {
  vi.doMock("ws", () => {
    throw new Error("ws 模块不应在存在全局 WebSocket 时被加载。");
  });

  vi.stubGlobal("WebSocket", AutoOpenWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  });

  try {
    await expect(session.sendRequest("hub.ping")).resolves.toEqual({ ok: true });
  } finally {
    await session.dispose();
  }
});

it("缺少全局 WebSocket 时应按需懒加载 ws 模块", async () => {
  let loadCount = 0;
  vi.doMock("ws", () => {
    loadCount += 1;
    return {
      WebSocket: AutoOpenWebSocket,
      default: AutoOpenWebSocket
    };
  });

  vi.stubGlobal("WebSocket", undefined as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  });

  expect(loadCount).toBe(0);

  try {
    await expect(session.sendRequest("hub.ping")).resolves.toEqual({ ok: true });
    expect(loadCount).toBe(1);
  } finally {
    await session.dispose();
  }
});

it("缺少全局实现且无法加载 ws 时应返回可读错误", async () => {
  vi.doMock("ws", () => {
    throw new Error("Cannot find package 'ws'.");
  });

  vi.stubGlobal("WebSocket", undefined as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  });

  await expect(session.ensureConnected())
    .rejects
    .toThrow("WebSocket 不可用。请安装 ws，或提供全局 WebSocket 实现。");
});

it("并发首个 sendRequest 应共享同一建连等待过程且只在 open 后发送", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  });

  try {
    const firstRequest = session.sendRequest("hub.ping");
    const secondRequest = session.sendRequest("hub.ping");
    await flushMicrotasks();

    expect(ControlledWebSocket.instances).toHaveLength(1);
    const socket = ControlledWebSocket.instances[0]!;
    expect(socket.sentRequests).toHaveLength(0);

    socket.emitOpen();
    await waitForSentRequestCount(socket, 2);

    socket.respondWithResult(socket.sentRequests[0]!.id, { ok: true, seq: 1 });
    socket.respondWithResult(socket.sentRequests[1]!.id, { ok: true, seq: 2 });

    await expect(Promise.all([firstRequest, secondRequest])).resolves.toEqual([
      { ok: true, seq: 1 },
      { ok: true, seq: 2 }
    ]);
  } finally {
    await session.dispose();
  }
});

it("本地超时请求的迟到响应应被忽略且会话保持可用", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws",
    requestTimeoutMs: 20
  });

  try {
    const firstRequest = session.sendRequest("hub.timeout");
    await flushMicrotasks();

    const socket = ControlledWebSocket.instances[0]!;
    socket.emitOpen();
    await waitForSentRequestCount(socket, 1);
    const timedOutRequestId = socket.sentRequests[0]!.id;

    await expect(firstRequest).rejects.toThrow("WebSocket request timed out.");

    socket.respondWithResult(timedOutRequestId, { ok: true, late: true });
    await flushMicrotasks();

    const secondRequest = session.sendRequest("hub.ping");
    await waitForSentRequestCount(socket, 2);
    socket.respondWithResult(socket.sentRequests[1]!.id, { ok: true, seq: 2 });

    await expect(secondRequest).resolves.toEqual({ ok: true, seq: 2 });
  } finally {
    await session.dispose();
  }
});

it("超时很久后的迟到响应仍应被忽略且会话保持可用", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws",
    requestTimeoutMs: 20
  });

  try {
    const firstRequest = session.sendRequest("hub.timeout");
    await flushMicrotasks();

    const socket = ControlledWebSocket.instances[0]!;
    socket.emitOpen();
    await waitForSentRequestCount(socket, 1);
    const timedOutRequestId = socket.sentRequests[0]!.id;

    await expect(firstRequest).rejects.toThrow("WebSocket request timed out.");

    const dateNowSpy = vi.spyOn(Date, "now")
      .mockReturnValue(Date.now() + 10 * 60_000);
    socket.respondWithResult(timedOutRequestId, { ok: true, late: true });
    await flushMicrotasks();
    dateNowSpy.mockRestore();

    const secondRequest = session.sendRequest("hub.ping");
    await waitForSentRequestCount(socket, 2);
    socket.respondWithResult(socket.sentRequests[1]!.id, { ok: true, seq: 2 });

    await expect(secondRequest).resolves.toEqual({ ok: true, seq: 2 });
  } finally {
    await session.dispose();
  }
});

it("应统计并按过滤条件清理已放弃请求记录", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws",
    requestTimeoutMs: 20
  });

  try {
    const firstRequest = session.sendRequest("hub.apps.getDefinition", {
      appId: "app-a",
      scope: ""
    });
    await flushMicrotasks();

    const socket = ControlledWebSocket.instances[0]!;
    socket.emitOpen();
    await waitForSentRequestCount(socket, 1);

    await expect(firstRequest).rejects.toThrow("WebSocket request timed out.");
    await sleep(60);

    const secondRequest = session.sendRequest("hub.apps.listInstances", {
      appId: "app-b",
      scope: ""
    });
    await waitForSentRequestCount(socket, 2);
    await expect(secondRequest).rejects.toThrow("WebSocket request timed out.");

    expect(session.getAbandonedRequestCount()).toBe(2);
    expect(session.getAbandonedRequestCount({ appId: "app-a" })).toBe(1);
    expect(session.getAbandonedRequestCount({ method: "hub.apps.listInstances" })).toBe(1);
    expect(session.getAbandonedRequestCount({ olderThanMs: 30 })).toBe(1);
    expect(session.getAbandonedRequestCount({
      olderThanMs: 30,
      appId: "app-a",
      method: "hub.apps.getDefinition"
    })).toBe(1);
    expect(session.getAbandonedRequestCount({
      olderThanMs: 30,
      appId: "app-b"
    })).toBe(0);
    expect(socket.sentRequests).toHaveLength(2);

    expect(session.clearAbandonedRequests({ olderThanMs: 30 })).toBe(1);
    expect(session.getAbandonedRequestCount()).toBe(1);
    expect(session.clearAbandonedRequests({
      appId: "app-b",
      method: "hub.apps.listInstances"
    })).toBe(1);
    expect(session.getAbandonedRequestCount()).toBe(0);
    expect(session.clearAbandonedRequests()).toBe(0);
  } finally {
    await session.dispose();
  }
});

it("已放弃请求过滤器应拒绝非法参数", async () => {
  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws"
  });

  try {
    expect(() => session.getAbandonedRequestCount({ olderThanMs: -1 }))
      .toThrow("filter.olderThanMs 必须为大于等于 0 的有限数字。");
    expect(() => session.clearAbandonedRequests({ appId: "   " }))
      .toThrow("filter.appId 不能为空。");
    expect(() => session.getAbandonedRequestCount({ appId: ".app-a" }))
      .toThrow(/filter\.appId/);
    expect(() => session.getAbandonedRequestCount({ method: "" }))
      .toThrow("filter.method 必须为非空字符串。");
  } finally {
    await session.dispose();
  }
});

it("手动清理后迟到响应应恢复未知响应 id 故障语义", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const onTerminate = vi.fn();
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws",
    requestTimeoutMs: 20,
    onTerminate
  });

  try {
    const request = session.sendRequest("hub.timeout", {
      appId: "app-a"
    });
    await flushMicrotasks();

    const socket = ControlledWebSocket.instances[0]!;
    socket.emitOpen();
    await waitForSentRequestCount(socket, 1);
    const timedOutRequestId = socket.sentRequests[0]!.id;

    await expect(request).rejects.toThrow("WebSocket request timed out.");
    expect(session.getAbandonedRequestCount()).toBe(1);
    expect(session.clearAbandonedRequests({
      appId: "app-a",
      method: "hub.timeout"
    })).toBe(1);
    expect(session.getAbandonedRequestCount()).toBe(0);

    socket.respondWithResult(timedOutRequestId, { ok: true, late: true });
    await flushMicrotasks();

    expect(onTerminate).toHaveBeenCalledTimes(1);
    expect(socket.closeCalls.at(-1)).toEqual({
      code: 1000,
      reason: "connection_closed"
    });
  } finally {
    await session.dispose();
  }
});

it("真正未知的响应 id 应终止当前会话", async () => {
  vi.stubGlobal("WebSocket", ControlledWebSocket as unknown as typeof WebSocket);

  const { JsonRpcWsSession } = await import("../../src/ws-session.js");
  const onTerminate = vi.fn();
  const session = new JsonRpcWsSession({
    websocketEndpoint: "ws://127.0.0.1:47231/ws",
    onTerminate
  });

  try {
    const request = session.sendRequest("hub.ping");
    await flushMicrotasks();

    const socket = ControlledWebSocket.instances[0]!;
    socket.emitOpen();
    await waitForSentRequestCount(socket, 1);

    socket.respondWithResult("ws-unknown-response", { ok: true });

    await expect(request).rejects.toThrow("WebSocket JSON-RPC response id does not match any pending request.");
    expect(onTerminate).toHaveBeenCalledTimes(1);
    expect(socket.closeCalls.at(-1)).toEqual({
      code: 1000,
      reason: "connection_closed"
    });
  } finally {
    await session.dispose();
  }
});

class AutoOpenWebSocket {
  private readonly listeners = new Map<string, Set<(event: unknown, ...args: unknown[]) => void>>();
  readyState = 0;

  constructor(_url: string) {
    queueMicrotask(() => {
      this.readyState = 1;
      this.emit("open", {});
    });
  }

  send(data: string): void {
    const request = JSON.parse(data) as { id: string };
    const payload = JSON.stringify({
      jsonrpc: "2.0",
      id: request.id,
      result: {
        ok: true
      }
    });
    const frame = new TextEncoder().encode(payload);
    queueMicrotask(() => {
      this.emit("message", frame, false);
    });
  }

  close(): void {
    this.readyState = 3;
  }

  addEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    const listeners = this.listeners.get(type) ?? new Set<(event: unknown, ...args: unknown[]) => void>();
    listeners.add(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  private emit(type: string, event: unknown, ...args: unknown[]): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event, ...args);
    }
  }
}

class ControlledWebSocket {
  static instances: ControlledWebSocket[] = [];

  readonly sentRequests: Array<{ id: string; method: string; params?: Record<string, unknown> }> = [];
  readonly closeCalls: Array<{ code?: number; reason?: string }> = [];

  private readonly listeners = new Map<string, Set<(event: unknown, ...args: unknown[]) => void>>();
  readyState = 0;

  constructor(_url: string) {
    ControlledWebSocket.instances.push(this);
  }

  send(data: string): void {
    if (this.readyState !== 1) {
      throw new Error("send should not be called before the WebSocket is open.");
    }

    const request = JSON.parse(data) as {
      id: string;
      method: string;
      params?: Record<string, unknown>;
    };
    this.sentRequests.push(request);
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push({ code, reason });
    if (this.readyState === 3) {
      return;
    }

    this.readyState = 3;
    this.emit("close", { code: code ?? 1000, reason: reason ?? "" });
  }

  addEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    const listeners = this.listeners.get(type) ?? new Set<(event: unknown, ...args: unknown[]) => void>();
    listeners.add(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type: string, listener: (event: unknown, ...args: unknown[]) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  emitOpen(): void {
    if (this.readyState !== 0) {
      return;
    }

    this.readyState = 1;
    this.emit("open", {});
  }

  respondWithResult(requestId: string, result: Record<string, unknown>): void {
    this.emit("message", {
      data: JSON.stringify({
        jsonrpc: "2.0",
        id: requestId,
        result
      })
    }, false);
  }

  private emit(type: string, event: unknown, ...args: unknown[]): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event, ...args);
    }
  }
}

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

async function waitForSentRequestCount(socket: ControlledWebSocket, count: number): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    if (socket.sentRequests.length === count) {
      return;
    }

    await flushMicrotasks();
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  expect(socket.sentRequests).toHaveLength(count);
}

async function sleep(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}
