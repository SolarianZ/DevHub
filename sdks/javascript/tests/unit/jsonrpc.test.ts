import { expect, it } from "vitest";
import { DevHubRpcError, DevHubRpcErrorCode } from "../../src/errors.js";
import { readResponseId, tryGetResponse, validateResponseEnvelope } from "../../src/jsonrpc.js";

it("readResponseId 应接受字符串与安全整数 id", () => {
  expect(readResponseId({ id: "req-1" }, "JSON-RPC response")).toBe("req-1");
  expect(readResponseId({ id: 42 }, "JSON-RPC response")).toBe("42");
});

it("validateResponseEnvelope 应拒绝小数 response id", () => {
  expect(() => validateResponseEnvelope({
    jsonrpc: "2.0",
    id: 1.5,
    result: {
      ok: true
    }
  }, "req-1")).toThrow(/id/i);
});

it("validateResponseEnvelope 应将 id 为 null 的鉴权错误映射为 DevHubRpcError", () => {
  try {
    validateResponseEnvelope({
      jsonrpc: "2.0",
      id: null,
      error: {
        code: DevHubRpcErrorCode.Unauthorized,
        message: "unauthorized",
        data: {
          reason: "invalid_token"
        }
      }
    }, "req-1");
  } catch (error) {
    expect(error).toBeInstanceOf(DevHubRpcError);
    const rpcError = error as DevHubRpcError;
    expect(rpcError.code).toBe(DevHubRpcErrorCode.Unauthorized);
    expect(rpcError.reason).toBe("invalid_token");
    expect(rpcError.requestId).toBe("req-1");
    return;
  }

  throw new Error("Expected validateResponseEnvelope to throw DevHubRpcError.");
});

it("tryGetResponse 应拒绝超出协议整数范围的 numeric id", () => {
  const payload = JSON.parse(
    "{\"jsonrpc\":\"2.0\",\"id\":9223372036854775808,\"result\":{\"ok\":true}}"
  ) as Record<string, unknown>;

  expect(() => tryGetResponse(payload)).toThrow(/id/i);
});
