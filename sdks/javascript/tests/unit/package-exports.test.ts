import { expect, it } from "vitest";
import * as sdk from "../../src/index.js";
import { JsonRpcHttpTransport } from "../../src/http-transport.js";
import { FileSystemRuntimeResolver } from "../../src/runtime.js";
import { JsonRpcWsSession } from "../../src/ws-session.js";

it("顶层入口应导出高级扩展点", () => {
  expect(sdk.JsonRpcHttpTransport).toBe(JsonRpcHttpTransport);
  expect(sdk.JsonRpcWsSession).toBe(JsonRpcWsSession);
  expect(sdk.FileSystemRuntimeResolver).toBe(FileSystemRuntimeResolver);
});
