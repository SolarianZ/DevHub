import { promises as fsPromises } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, expect, it } from "vitest";
import { WebSocketServer } from "ws";
import { DevHubEventsClient, INVOCATION_COMPLETED } from "../../src/events.js";

type WebSocketServerInstance = InstanceType<typeof WebSocketServer>;

const tempRoots: string[] = [];

afterEach(async () => {
  await Promise.all(tempRoots.splice(0).map(async (target) => {
    await fsPromises.rm(target, { recursive: true, force: true });
  }));
});

it("真实 WS 连接下同一个 events client 实例应拒绝并发 reader", async () => {
  const server = new WebSocketServer({
    host: "127.0.0.1",
    port: 0,
    path: "/ws"
  });

  try {
    await waitForWebSocketServer(server);
    const runtimeDir = await createRuntimeForServer(server);

    server.on("connection", (socket: any) => {
      socket.on("message", (data: Buffer) => {
        const request = JSON.parse(data.toString("utf-8")) as Record<string, unknown>;
        if (String(request.method) !== "hub.ws.authenticate") {
          return;
        }

        socket.send(JSON.stringify({
          jsonrpc: "2.0",
          id: String(request.id),
          result: {
            ok: true,
            protocolVersion: 1
          }
        }));
      });
    });

    const client = await DevHubEventsClient.fromRuntime({
      clientId: "events-ws-single-reader",
      dataDir: runtimeDir
    });

    try {
      await client.authenticate();

      const firstIterator = client.readEvents()[Symbol.asyncIterator]();
      expect(() => client.readEvents()[Symbol.asyncIterator]())
        .toThrow("Only one active readEvents() iterator is allowed per DevHubEventsClient instance.");

      await firstIterator.return?.();

      const secondIterator = client.readEvents()[Symbol.asyncIterator]();
      await secondIterator.return?.();
    } finally {
      await client.dispose();
    }
  } finally {
    await closeWebSocketServer(server);
  }
});

it("真实 WS 连接下断线后活动 reader 应排空缓冲并在重新认证后要求重新订阅", async () => {
  const server = new WebSocketServer({
    host: "127.0.0.1",
    port: 0,
    path: "/ws"
  });
  const requestsByConnection: Array<Array<string>> = [];

  try {
    await waitForWebSocketServer(server);
    const runtimeDir = await createRuntimeForServer(server);

    server.on("connection", (socket: any) => {
      const connectionIndex = requestsByConnection.length;
      requestsByConnection.push([]);

      socket.on("message", (data: Buffer) => {
        const request = JSON.parse(data.toString("utf-8")) as Record<string, unknown>;
        const method = String(request.method);
        requestsByConnection[connectionIndex]!.push(method);

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

        if (method !== "hub.events.subscribe") {
          return;
        }

        const subscriptionId = connectionIndex === 0 ? "sub-1" : "sub-2";
        const invocationId = connectionIndex === 0 ? "invk-1" : "invk-2";
        socket.send(JSON.stringify({
          jsonrpc: "2.0",
          id: String(request.id),
          result: {
            ok: true,
            subscriptionId
          }
        }));
        socket.send(JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.event",
          params: {
            subscriptionId,
            type: INVOCATION_COMPLETED,
            timeUtc: "2026-03-09T00:00:00Z",
            payload: {
              invocationId
            }
          }
        }));

        if (connectionIndex === 0) {
          socket.close(1000, "reconnect_required");
        }
      });
    });

    const client = await DevHubEventsClient.fromRuntime({
      clientId: "events-ws-reconnect-client",
      dataDir: runtimeDir
    });

    try {
      await client.authenticate();
      const firstIterator = client.readEvents()[Symbol.asyncIterator]();
      await client.subscribe([INVOCATION_COMPLETED]);

      const first = await firstIterator.next();
      expect(first.done).toBe(false);
      expect(first.value.type).toBe(INVOCATION_COMPLETED);
      expect(first.value.payload?.invocationId).toBe("invk-1");
      await expect(firstIterator.next()).resolves.toEqual({
        value: undefined,
        done: true
      });

      expect(() => client.readEvents())
        .toThrow("The event stream is unavailable. Re-authenticate and subscribe again.");

      await client.authenticate();
      await waitForCondition(() => requestsByConnection.length === 2);
      expect(requestsByConnection[1]?.[0]).toBe("hub.ws.authenticate");
      expect(requestsByConnection[1]).not.toContain("hub.events.subscribe");

      const secondIterator = client.readEvents()[Symbol.asyncIterator]();
      const secondEventTask = secondIterator.next();
      await client.subscribe([INVOCATION_COMPLETED]);

      const second = await secondEventTask;
      expect(second.done).toBe(false);
      expect(second.value.type).toBe(INVOCATION_COMPLETED);
      expect(second.value.payload?.invocationId).toBe("invk-2");
      expect(requestsByConnection[1]).toContain("hub.events.subscribe");
    } finally {
      await client.dispose();
    }
  } finally {
    await closeWebSocketServer(server);
  }
});

it("真实 WS 连接下旧 iterator 未显式关闭时也应允许重新认证恢复", async () => {
  const server = new WebSocketServer({
    host: "127.0.0.1",
    port: 0,
    path: "/ws"
  });
  const requestsByConnection: Array<Array<string>> = [];

  try {
    await waitForWebSocketServer(server);
    const runtimeDir = await createRuntimeForServer(server);

    server.on("connection", (socket: any) => {
      const connectionIndex = requestsByConnection.length;
      requestsByConnection.push([]);

      socket.on("message", (data: Buffer) => {
        const request = JSON.parse(data.toString("utf-8")) as Record<string, unknown>;
        const method = String(request.method);
        requestsByConnection[connectionIndex]!.push(method);

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

        if (method !== "hub.events.subscribe") {
          return;
        }

        const subscriptionId = connectionIndex === 0 ? "sub-1" : "sub-2";
        const invocationId = connectionIndex === 0 ? "invk-1" : "invk-2";
        socket.send(JSON.stringify({
          jsonrpc: "2.0",
          id: String(request.id),
          result: {
            ok: true,
            subscriptionId
          }
        }));
        socket.send(JSON.stringify({
          jsonrpc: "2.0",
          method: "hub.event",
          params: {
            subscriptionId,
            type: INVOCATION_COMPLETED,
            timeUtc: "2026-03-09T00:00:00Z",
            payload: {
              invocationId
            }
          }
        }));

        if (connectionIndex === 0) {
          socket.close(1000, "reconnect_required");
        }
      });
    });

    const client = await DevHubEventsClient.fromRuntime({
      clientId: "events-ws-abandoned-iterator-reconnect-client",
      dataDir: runtimeDir
    });

    try {
      await client.authenticate();
      const abandonedIterator = client.readEvents()[Symbol.asyncIterator]();
      await client.subscribe([INVOCATION_COMPLETED]);
      await waitForCondition(() => isEventStreamUnavailable(client));

      await client.authenticate();
      await waitForCondition(() => requestsByConnection.length === 2);
      expect(requestsByConnection[1]?.[0]).toBe("hub.ws.authenticate");
      expect(requestsByConnection[1]).not.toContain("hub.events.subscribe");

      const recoveredIterator = client.readEvents()[Symbol.asyncIterator]();
      const recoveredEventTask = recoveredIterator.next();
      await client.subscribe([INVOCATION_COMPLETED]);

      const recovered = await recoveredEventTask;
      expect(recovered.done).toBe(false);
      expect(recovered.value.type).toBe(INVOCATION_COMPLETED);
      expect(recovered.value.payload?.invocationId).toBe("invk-2");
      expect(requestsByConnection[1]).toContain("hub.events.subscribe");

      await recoveredIterator.return?.();
      await abandonedIterator.return?.();
    } finally {
      await client.dispose();
    }
  } finally {
    await closeWebSocketServer(server);
  }
});

async function createRuntimeForServer(server: WebSocketServerInstance): Promise<string> {
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("无法获取测试 WebSocket 端口。");
  }

  const dataDir = await fsPromises.realpath(
    await fsPromises.mkdtemp(path.join(os.tmpdir(), "devhub-js-sdk-events-integration-"))
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
      httpBaseUrl: `http://127.0.0.1:${address.port}`,
      wsUrl: `ws://127.0.0.1:${address.port}/ws`,
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

async function waitForWebSocketServer(server: WebSocketServerInstance): Promise<void> {
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

async function closeWebSocketServer(server: WebSocketServerInstance): Promise<void> {
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

async function waitForCondition(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 50; attempt += 1) {
    if (condition()) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 10));
  }

  throw new Error("condition not met in time");
}

function isEventStreamUnavailable(client: DevHubEventsClient): boolean {
  try {
    client.readEvents();
    return false;
  } catch (error) {
    return error instanceof Error
      && error.message === "The event stream is unavailable. Re-authenticate and subscribe again.";
  }
}
