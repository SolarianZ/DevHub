import type {
  AppDefinition,
  Invocation,
  InvokeRequest,
  RespondRequest
} from "../../src/index.js";

const definition: AppDefinition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App",
  launch: {
    exePath: "node"
  }
};

const invokeRequest: InvokeRequest = {
  appId: "sample.app",
  method: "sample.method",
  target: {
    scope: ""
  }
};

const invocation: Invocation = {
  invocationId: "invk-1",
  appId: "sample.app",
  target: {
    scope: ""
  },
  method: "sample.method",
  kind: "request",
  createdAtUtc: new Date("2026-03-15T00:00:00Z"),
  caller: {
    clientId: "caller-a",
    clientSessionId: "11111111-1111-4111-8111-111111111111"
  }
};

const respondWithValue: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  value: {
    ok: true
  }
};

const respondWithError: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  error: {
    code: 1001,
    message: "callee_failed"
  }
};

// @ts-expect-error InvokeRequest.target is required.
const missingInvokeTarget: InvokeRequest = {
  appId: "sample.app",
  method: "sample.method"
};

const missingLaunchExePath: AppDefinition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App",
  // @ts-expect-error AppDefinition.launch.exePath is required when launch is present.
  launch: {}
};

// @ts-expect-error Invocation.target is required.
const missingInvocationTarget: Invocation = {
  invocationId: "invk-1",
  appId: "sample.app",
  method: "sample.method",
  kind: "notify",
  createdAtUtc: new Date("2026-03-15T00:00:00Z"),
  caller: {
    clientId: "caller-a",
    clientSessionId: "11111111-1111-4111-8111-111111111111"
  }
};

// @ts-expect-error RespondRequest requires exactly one of value or error.
const missingRespondPayload: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1"
};

// @ts-expect-error RespondRequest forbids value and error together.
const invalidRespondPayload: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  value: {
    ok: true
  },
  error: {
    code: 1001,
    message: "callee_failed"
  }
};

void definition;
void invokeRequest;
void invocation;
void respondWithValue;
void respondWithError;
void missingInvokeTarget;
void missingLaunchExePath;
void missingInvocationTarget;
void missingRespondPayload;
void invalidRespondPayload;
