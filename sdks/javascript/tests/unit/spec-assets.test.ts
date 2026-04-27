import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "../../../..");
const schemaDir = path.join(repoRoot, "docs", "specification", "schema", "v1.0.1");

async function readSchema(name: string): Promise<Record<string, unknown>> {
  return JSON.parse(await readFile(path.join(schemaDir, name), "utf8")) as Record<string, unknown>;
}

describe("specification schema assets", () => {
  it("rpc-request 应允许 params 为 null", async () => {
    const schema = await readSchema("rpc-request.json");
    const params = (schema.properties as Record<string, { oneOf?: Array<{ type?: string }> }>).params;
    expect((params.oneOf ?? []).map((item) => item.type)).toEqual(["object", "array", "null"]);
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
