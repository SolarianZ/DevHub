import { describe, expect, it } from "vitest";
import { validateRpcTestEnvelope } from "./rpc-test";

describe("rpc-test validation", () => {
  it("rejects malformed invocation target instance IDs before sending", () => {
    expect(validateRpcTestEnvelope({
      jsonrpc: "2.0",
      id: "req-1",
      method: "hub.invoke.notify",
      params: {
        appId: "demo.app",
        target: {
          scope: "",
          instanceId: "node:01",
        },
        method: "demo.notify",
      },
    })).toEqual({
      ok: false,
      error: "target.instanceId 必须匹配 ^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$，且长度不能超过 256。",
    });
  });

  it("rejects overlong invocation target instance IDs before sending", () => {
    expect(validateRpcTestEnvelope({
      jsonrpc: "2.0",
      id: "req-2",
      method: "hub.invoke.request",
      params: {
        appId: "demo.app",
        target: {
          scope: "",
          instanceId: "a".repeat(257),
        },
        method: "demo.request",
      },
    })).toMatchObject({
      ok: false,
    });
  });

  it("does not block unrelated raw JSON-RPC methods with target-shaped params", () => {
    expect(validateRpcTestEnvelope({
      jsonrpc: "2.0",
      id: "req-3",
      method: "hub.experimental.invoke",
      params: {
        target: {
          instanceId: "node:01",
        },
      },
    })).toMatchObject({
      ok: true,
    });
  });
});
