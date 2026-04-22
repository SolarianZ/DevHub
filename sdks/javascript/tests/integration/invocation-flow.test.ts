import { afterAll, beforeAll, expect, it } from "vitest";
import { DevHubClient } from "../../src/client.js";
import { DevHubRpcError, DevHubRpcErrorCode } from "../../src/errors.js";
import { DevHubHostFixture } from "./host.js";

const INSTANCE_PASSWORD = "invoke-flow-password";

let host: DevHubHostFixture | undefined;

beforeAll(async () => {
  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "invoke.notify.app",
    scope: "",
    displayName: "invoke.notify.app"
  });
  await host.writeDefinition({
    appId: "invoke.request.app",
    scope: "",
    displayName: "invoke.request.app"
  });
  await host.writeDefinition({
    appId: "invoke.error.app",
    scope: "",
    displayName: "invoke.error.app"
  });
  await host.writeDefinition({
    appId: "invoke.timeout.app",
    scope: "",
    displayName: "invoke.timeout.app"
  });
  await host.writeDefinition({
    appId: "invoke.scope.app",
    scope: "",
    displayName: "invoke.scope.app"
  });
  await host.writeDefinition({
    appId: "invoke.scope.app",
    scope: "scope-a",
    displayName: "invoke.scope.app.scope-a"
  });
  await host.writeDefinition({
    appId: "invoke.scope.app",
    scope: "global",
    displayName: "invoke.scope.app.literal-global"
  });
  await host.writeDefinition({
    appId: "invoke.rpc-disabled.app",
    scope: "",
    displayName: "invoke.rpc-disabled.app",
    capabilities: {
      rpc: false
    }
  });
  await host.writeDefinition({
    appId: "invoke.poll-disabled.app",
    scope: "",
    displayName: "invoke.poll-disabled.app"
  });
  await host.writeDefinition({
    appId: "invoke.respond-disabled.app",
    scope: "",
    displayName: "invoke.respond-disabled.app"
  });
}, 120_000);

afterAll(async () => {
  await host?.close();
});

it("notify + poll 应完成调用往返", async () => {
  const client = await createClient("invoke-notify-client");
  await registerInstance(client, "invoke.notify.app", "notify-inst-1");

  const notifyResult = await client.notify({
    appId: "invoke.notify.app",
    method: "test.notify",
    target: {
      scope: ""
    },
    args: {
      message: "hello"
    }
  });

  expect(notifyResult.ok).toBe(true);

  const invocation = await waitForSingleInvocation(client, "notify-inst-1");
  expect(invocation.invocationId).toBe(notifyResult.invocationId);
  expect(invocation.kind).toBe("notify");
  expect((invocation.args as { message: string }).message).toBe("hello");

  await client.dispose();
});

it("request/respond 成功后再次 respond 应返回 delivery_conflict", async () => {
  const client = await createClient("invoke-request-client");
  await registerInstance(client, "invoke.request.app", "request-inst-1");

  const requestTask = client.request({
    appId: "invoke.request.app",
    method: "test.request",
    target: {
      scope: ""
    },
    args: {
      input: 1
    },
    options: {
      ttlMs: 5_000,
      waitTimeoutMs: 3_000
    }
  });

  const invocation = await waitForSingleInvocation(client, "request-inst-1");

  await client.respond({
    instanceId: "request-inst-1",
    invocationId: invocation.invocationId,
    value: {
      ok: true,
      value: 2
    }
  });

  const requestResult = await requestTask;
  expect(requestResult.ok).toBe(true);
  expect(requestResult.value).toEqual({
    ok: true,
    value: 2
  });

  await expect(client.respond({
    instanceId: "request-inst-1",
    invocationId: invocation.invocationId,
    value: {
      ok: true
    }
  })).rejects.toMatchObject({
    code: DevHubRpcErrorCode.DeliveryConflict
  });

  await client.dispose();
});

it("request/respond 错误应映射为 invocation_failed", async () => {
  const client = await createClient("invoke-error-client");
  await registerInstance(client, "invoke.error.app", "error-inst-1");

  const requestTask = client.request({
    appId: "invoke.error.app",
    method: "test.request",
    target: {
      scope: ""
    },
    options: {
      ttlMs: 5_000,
      waitTimeoutMs: 3_000
    }
  }).then(
    (value) => ({ ok: true as const, value }),
    (error) => ({ ok: false as const, error })
  );

  const invocation = await waitForSingleInvocation(client, "error-inst-1");

  await client.respond({
    instanceId: "error-inst-1",
    invocationId: invocation.invocationId,
    error: {
      code: 1001,
      message: "app_error",
      data: {
        reason: "boom"
      }
    }
  });

  const requestOutcome = await requestTask;
  expect(requestOutcome.ok).toBe(false);
  if (requestOutcome.ok) {
    throw new Error("request should fail when respond returns error.");
  }
  const capturedError = requestOutcome.error;

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(DevHubRpcErrorCode.InvocationFailed);
  expect(rpcError.invocationId).toBe(invocation.invocationId);
  expect(rpcError.calleeError).toEqual({
    code: 1001,
    message: "app_error",
    data: {
      reason: "boom"
    }
  });

  await client.dispose();
});

it("request 超时与过期应映射为预期错误", async () => {
  const client = await createClient("invoke-timeout-client");
  await registerInstance(client, "invoke.timeout.app", "timeout-inst-1");

  await expect(client.request({
    appId: "invoke.timeout.app",
    method: "test.timeout",
    target: {
      scope: ""
    },
    options: {
      ttlMs: 1_500,
      waitTimeoutMs: 1_000
    }
  })).rejects.toMatchObject({
    code: DevHubRpcErrorCode.InvocationTimeout
  });

  await expect(client.request({
    appId: "invoke.timeout.app",
    method: "test.expired",
    target: {
      scope: ""
    },
    options: {
      ttlMs: 1_000,
      waitTimeoutMs: 1_000
    }
  })).rejects.toMatchObject({
    code: DevHubRpcErrorCode.InvocationExpired
  });

  await client.dispose();
});

it("notify/request 在 rpc_disabled 时应映射 forbidden", async () => {
  const client = await createClient("invoke-rpc-disabled-client");

  await expectRpcError(
    client.notify({
      appId: "invoke.rpc-disabled.app",
      method: "test.notify",
      target: {
        scope: ""
      }
    }),
    DevHubRpcErrorCode.Forbidden,
    "rpc_disabled"
  );

  await expectRpcError(
    client.request({
      appId: "invoke.rpc-disabled.app",
      method: "test.request",
      target: {
        scope: ""
      }
    }),
    DevHubRpcErrorCode.Forbidden,
    "rpc_disabled"
  );

  await client.dispose();
});

it("poll 在 poll_not_enabled 时应映射 forbidden", async () => {
  const client = await createClient("invoke-poll-disabled-client");
  await registerInstance(
    client,
    "invoke.poll-disabled.app",
    "poll-disabled-inst-1",
    "",
    {
      poll: false,
      respond: true
    }
  );

  await expectRpcError(
    client.poll({
      instanceId: "poll-disabled-inst-1",
      waitMs: 0
    }),
    DevHubRpcErrorCode.Forbidden,
    "poll_not_enabled"
  );

  await client.dispose();
});

it("respond 在 respond_not_enabled 时应映射 forbidden", async () => {
  const client = await createClient("invoke-respond-disabled-client");
  await registerInstance(
    client,
    "invoke.respond-disabled.app",
    "respond-disabled-inst-1",
    "",
    {
      poll: true,
      respond: false
    }
  );

  await expectRpcError(
    client.respond({
      instanceId: "respond-disabled-inst-1",
      invocationId: "invk-missing",
      value: {
        ok: true
      }
    }),
    DevHubRpcErrorCode.Forbidden,
    "respond_not_enabled"
  );

  await client.dispose();
});

it("scope 路由规则应命中正确实例", async () => {
  const client = await createClient("invoke-scope-client");
  await registerInstance(client, "invoke.scope.app", "scope-global-inst", "");
  await registerInstance(client, "invoke.scope.app", "scope-a-inst", "scope-a");
  await registerInstance(client, "invoke.scope.app", "scope-literal-global-inst", "global");

  await client.notify({
    appId: "invoke.scope.app",
    method: "test.default-global",
    target: {
      scope: ""
    }
  });
  expect((await waitForSingleInvocation(client, "scope-global-inst")).method).toBe("test.default-global");
  expect((await client.poll({ instanceId: "scope-a-inst", waitMs: 0 })).items).toHaveLength(0);

  await client.notify({
    appId: "invoke.scope.app",
    method: "test.scope-a",
    target: {
      scope: "scope-a"
    }
  });
  expect((await waitForSingleInvocation(client, "scope-a-inst")).method).toBe("test.scope-a");

  await client.notify({
    appId: "invoke.scope.app",
    method: "test.empty-scope",
    target: {
      scope: ""
    }
  });
  expect((await waitForSingleInvocation(client, "scope-global-inst")).method).toBe("test.empty-scope");

  await client.notify({
    appId: "invoke.scope.app",
    method: "test.literal-global",
    target: {
      scope: "global"
    }
  });
  expect((await waitForSingleInvocation(client, "scope-literal-global-inst")).method).toBe("test.literal-global");

  await client.dispose();
});

async function createClient(clientId: string): Promise<DevHubClient> {
  return await DevHubClient.fromRuntime({
    clientId,
    dataDir: getHost().dataDirectory
  });
}

async function registerInstance(
  client: DevHubClient,
  appId: string,
  instanceId: string,
  scope = "",
  invoke = {
    poll: true,
    respond: true
  }
): Promise<void> {
  await client.registerInstance({
    instanceId,
    appId,
    scope,
    pid: process.pid,
    invoke
  }, INSTANCE_PASSWORD);
}

async function waitForSingleInvocation(client: DevHubClient, instanceId: string) {
  const result = await client.poll({
    instanceId,
    maxCount: 1,
    waitMs: 5_000
  });

  expect(result.items).toHaveLength(1);
  return result.items[0];
}

async function expectRpcError<T>(
  promise: Promise<T>,
  code: DevHubRpcErrorCode,
  reason: string
): Promise<DevHubRpcError> {
  let capturedError: unknown;

  try {
    await promise;
  } catch (error) {
    capturedError = error;
  }

  expect(capturedError).toBeInstanceOf(DevHubRpcError);
  const rpcError = capturedError as DevHubRpcError;
  expect(rpcError.code).toBe(code);
  expect(rpcError.reason).toBe(reason);
  return rpcError;
}

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture not started.");
  }

  return host;
}
