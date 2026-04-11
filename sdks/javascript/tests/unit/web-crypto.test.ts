import { afterEach, expect, it, vi } from "vitest";
import { createHttpRequestId, createWebSocketRequestId } from "../../src/jsonrpc.js";
import { normalizeClientOptions } from "../../src/models.js";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it("M6_TS_UT_006 默认标识符应通过 Web Crypto 生成", () => {
  const uuid = "11111111-1111-4111-8111-111111111111";
  const randomUUID = vi.fn(() => uuid);
  vi.stubGlobal("crypto", { randomUUID });

  expect(normalizeClientOptions({ clientId: "web-crypto-client" }).clientSessionId).toBe(uuid);
  expect(createHttpRequestId()).toBe(`req-${uuid.replace(/-/g, "")}`);
  expect(createWebSocketRequestId()).toBe(`ws-${uuid.replace(/-/g, "")}`);
  expect(randomUUID).toHaveBeenCalledTimes(3);
});
