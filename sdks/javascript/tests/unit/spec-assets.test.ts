import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Ajv } from "ajv";
import { describe, expect, it } from "vitest";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "../../../..");
const schemaDir = path.join(repoRoot, "docs", "specification", "schema", "v1.0.1");

async function readSchema(name: string): Promise<Record<string, unknown>> {
  return JSON.parse(await readFile(path.join(schemaDir, name), "utf8")) as Record<string, unknown>;
}

const ajv = new Ajv({ strict: false });

describe("specification schema assets", () => {
  it("rpc-request 应按方法收敛 params 形状", async () => {
    const schema = await readSchema("rpc-request.json");
    const validate = ajv.compile(schema);

    expect(validate({
      jsonrpc: "2.0",
      id: "ping-object",
      method: "hub.ping",
      params: {},
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      id: "ping-null",
      method: "hub.ping",
      params: null,
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      id: "ping-scalar",
      method: "hub.ping",
      params: "invalid",
    })).toBe(false);

    expect(validate({
      jsonrpc: "2.0",
      id: "get-version-object",
      method: "hub.getVersion",
      params: {},
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      id: "get-version-null",
      method: "hub.getVersion",
      params: null,
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      id: "get-version-scalar",
      method: "hub.getVersion",
      params: 1,
    })).toBe(false);

    expect(validate({
      jsonrpc: "2.0",
      id: "list-def-null",
      method: "hub.apps.listDefinitions",
      params: null,
    })).toBe(false);

    expect(validate({
      jsonrpc: "2.0",
      id: "any-array",
      method: "hub.ping",
      params: [],
    })).toBe(false);
  });

  it("rpc-notification 应按方法收敛 params 形状", async () => {
    const schema = await readSchema("rpc-notification.json");
    const validate = ajv.compile(schema);

    expect(validate({
      jsonrpc: "2.0",
      method: "hub.ping",
      params: null,
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      method: "hub.getVersion",
      params: null,
    })).toBe(true);

    expect(validate({
      jsonrpc: "2.0",
      method: "hub.apps.listDefinitions",
      params: null,
    })).toBe(false);

    expect(validate({
      jsonrpc: "2.0",
      method: "hub.ping",
      params: [],
    })).toBe(false);
  });

  it("rpc-response 与 error-response 应保持互斥", async () => {
    const validateResponse = ajv.compile(await readSchema("rpc-response.json"));
    const validateError = ajv.compile(await readSchema("error-response.json"));

    expect(validateResponse({
      jsonrpc: "2.0",
      id: "success-1",
      result: {
        ok: true
      }
    })).toBe(true);

    expect(validateError({
      jsonrpc: "2.0",
      id: "error-1",
      error: {
        code: -32602,
        message: "invalid_params"
      }
    })).toBe(true);

    const mixedPayload = {
      jsonrpc: "2.0",
      id: "mixed-1",
      result: {
        ok: true
      },
      error: {
        code: -32602,
        message: "invalid_params"
      }
    };

    expect(validateResponse(mixedPayload)).toBe(false);
    expect(validateError(mixedPayload)).toBe(false);
  });

  it("invocation schema 应要求 caller.clientSessionId 为 UUID 字符串", async () => {
    const validate = ajv.compile(await readSchema("invocation.json"));
    const validInvocation = createInvocationPayload();

    expect(validate(validInvocation)).toBe(true);
    expect(validate({
      ...validInvocation,
      caller: {
        clientId: "ClientA",
        clientSessionId: "not-a-uuid"
      }
    })).toBe(false);

    const { clientSessionId: _clientSessionId, ...callerWithoutSessionId } = validInvocation.caller;
    expect(validate({
      ...validInvocation,
      caller: callerWithoutSessionId
    })).toBe(false);
  });
});

function createInvocationPayload(): Record<string, any> {
  return {
    invocationId: "invk-1",
    appId: "sample.app",
    target: {
      scope: ""
    },
    method: "sample.method",
    kind: "request",
    createdAtUtc: "2026-03-09T00:00:00Z",
    caller: {
      clientId: "ClientA",
      clientSessionId: "00000000-0000-0000-0000-000000000000"
    }
  };
}
