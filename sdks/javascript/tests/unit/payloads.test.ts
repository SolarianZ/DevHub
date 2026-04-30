import { expect, it } from "vitest";
import { buildRegisterInstanceParams } from "../../src/payloads.js";
import type { AppInstanceRegistration } from "../../src/index.js";

it("buildRegisterInstanceParams 应拒绝 instance.password", () => {
  const instance = {
    ...createRegistration(),
    password: "secret-1"
  } as unknown as AppInstanceRegistration;

  expect(() => buildRegisterInstanceParams(instance, "secret-1")).toThrow("instance.password must not be present.");
});

it("buildRegisterInstanceParams 应拒绝 instance.instanceSessionToken", () => {
  const instance = {
    ...createRegistration(),
    instanceSessionToken: "session-1"
  } as unknown as AppInstanceRegistration;

  expect(() => buildRegisterInstanceParams(instance, "secret-1"))
    .toThrow("instance.instanceSessionToken must not be present.");
});

function createRegistration(): AppInstanceRegistration {
  return {
    instanceId: "inst-1",
    appId: "sample.app",
    scope: "",
    pid: 12345,
    invoke: {
      poll: true,
      respond: true
    }
  };
}
