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
    const responseSchema = await readSchema("rpc-response.json");
    const errorSchema = await readSchema("error-response.json");

    expect((responseSchema.not as { required?: string[] }).required).toEqual(["error"]);
    expect((errorSchema.not as { required?: string[] }).required).toEqual(["result"]);
  });

  it("invocation schema 应要求 caller.clientSessionId 为 UUID 字符串", async () => {
    const schema = await readSchema("invocation.json");
    const pattern = (
      ((schema.properties as Record<string, any>).caller.properties.clientSessionId as { pattern?: string }).pattern
    );
    expect(pattern).toBe("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
  });
});
