import { afterEach, expect, it, vi } from "vitest";

afterEach(() => {
  vi.unmock("ws");
  vi.unstubAllGlobals();
  vi.resetModules();
  vi.restoreAllMocks();
});

it("全局 WebSocket 应优先处理文本帧且不加载 ws", async () => {
  vi.doMock("ws", () => {
    throw new Error("ws 模块不应在存在全局 WebSocket 时被加载。");
  });

  vi.stubGlobal("WebSocket", FakeWebSocket as unknown as typeof WebSocket);

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

class FakeWebSocket {
  private readonly listeners = new Map<string, Set<(event: unknown, ...args: unknown[]) => void>>();

  constructor(_url: string) {
    queueMicrotask(() => {
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
