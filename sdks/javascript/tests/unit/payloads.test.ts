import { expect, it } from "vitest";
import {
  buildInvokeParams,
  buildListInstancesParams,
  buildRegisterInstanceParams,
  buildValidateDefinitionParams
} from "../../src/payloads.js";
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

it("buildRegisterInstanceParams 应把 launchId 放入顶层 params", () => {
  expect(buildRegisterInstanceParams(createRegistration(), "secret-1", { launchId: "launch-1" })).toEqual({
    password: "secret-1",
    launchId: "launch-1",
    instance: {
      instanceId: "inst-1",
      appId: "sample.app",
      scope: "",
      pid: 12345,
      invoke: {
        poll: true,
        respond: true
      }
    }
  });
});

it("buildRegisterInstanceParams 应接受适合 Hub 全局注册的生成式 instanceId", () => {
  const generatedInstanceId = "sample.app.global.550e8400e29b41d4a716446655440000";

  expect(buildRegisterInstanceParams({
    ...createRegistration(),
    instanceId: generatedInstanceId
  }, "secret-1")).toMatchObject({
    instance: {
      instanceId: generatedInstanceId,
      appId: "sample.app",
      scope: ""
    }
  });
});

it("buildRegisterInstanceParams 应拒绝超过 256 字符的 instanceId", () => {
  expect(() => buildRegisterInstanceParams({
    ...createRegistration(),
    instanceId: "a".repeat(257)
  }, "secret-1")).toThrow(/instanceId/);
});

it("buildRegisterInstanceParams 应拒绝空 launchId", () => {
  expect(() => buildRegisterInstanceParams(createRegistration(), "secret-1", { launchId: "" }))
    .toThrow("launchId 不能为空。");
});

it.each([
  ["notify", false],
  ["request", true]
])("buildInvokeParams 应拒绝 %s 的超长 target.instanceId", (_name, isRequest) => {
  expect(() => buildInvokeParams({
    appId: "sample.app",
    method: "sample.method",
    target: {
      scope: "",
      instanceId: "a".repeat(257)
    },
    options: {
      autoLaunch: false
    }
  }, isRequest)).toThrow(/target\.instanceId/);
});

it("buildRegisterInstanceParams 应拒绝非对象 options", () => {
  expect(() => buildRegisterInstanceParams(createRegistration(), "secret-1", "launch-1" as any))
    .toThrow("options must be an object.");
});

it("buildValidateDefinitionParams 应允许缺少 launch 和 launch.exePath 并序列化结构化 args", () => {
  expect(buildValidateDefinitionParams({
    appId: "sample.app",
    scope: "",
    displayName: "Sample App"
  })).toEqual({
    definition: {
      appId: "sample.app",
      scope: "",
      displayName: "Sample App"
    }
  });

  expect(buildValidateDefinitionParams({
    appId: "sample.app",
    scope: "",
    displayName: "Sample App",
    launch: {
      args: ["./app.js", "--scope", "{scope}"]
    }
  })).toEqual({
    definition: {
      appId: "sample.app",
      scope: "",
      displayName: "Sample App",
      launch: {
        args: ["./app.js", "--scope", "{scope}"]
      }
    }
  });
});

it("buildListInstancesParams 应允许省略 appId 但保留显式 scope", () => {
  expect(buildListInstancesParams({
    scope: null
  })).toEqual({
    scope: null
  });
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
