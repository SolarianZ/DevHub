import type {
  AppDefinition,
  AppInstanceRegistration,
  Invocation,
  InvokeRequest,
  RegisteredAppInstance,
  RespondRequest,
  VersionCompatibilityResult,
  VersionCompatibilityStatus
} from "../../src/index.js";
import { SDK_VERSION } from "../../src/index.js";

const definition: AppDefinition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App",
  launch: {
    exePath: "node",
    args: ["./app.js", "--scope", "{scope}"]
  }
};

const definitionWithoutLaunch: AppDefinition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App"
};

const definitionWithoutLaunchExePath: AppDefinition = {
  appId: "sample.app",
  scope: "",
  displayName: "Sample App",
  launch: {
    args: ["./app.js"]
  }
};

const invokeRequest: InvokeRequest = {
  appId: "sample.app",
  method: "sample.method",
  target: {
    scope: ""
  }
};

const instanceRegistration: AppInstanceRegistration = {
  instanceId: "inst-1",
  appId: "sample.app",
  scope: "",
  pid: 12345,
  invoke: {
    poll: true,
    respond: true
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
  delivery: {
    leaseToken: "lease-1",
    leaseSeconds: 30,
    attempt: 1
  },
  caller: {
    clientId: "caller-a",
    clientSessionId: "11111111-1111-4111-8111-111111111111"
  }
};

const respondWithValue: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  leaseToken: "lease-1",
  value: {
    ok: true
  }
};

const respondWithError: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  leaseToken: "lease-1",
  error: {
    code: 1001,
    message: "callee_failed"
  }
};

const compatibilityStatus: VersionCompatibilityStatus = "compatible";
const compatibilityResult: VersionCompatibilityResult = {
  sdkVersion: SDK_VERSION,
  hostVersion: "0.7.0",
  status: compatibilityStatus
};
const unknownCompatibilityResult: VersionCompatibilityResult = {
  sdkVersion: SDK_VERSION,
  hostVersion: null,
  status: "unknown"
};

const registeredInstance: RegisteredAppInstance = {
  ...instanceRegistration,
  registeredAtUtc: new Date("2026-03-15T00:00:00Z"),
  lastSeenUtc: new Date("2026-03-15T00:00:00Z"),
  instanceSessionToken: "session-1"
};

// @ts-expect-error InvokeRequest.target is required.
const missingInvokeTarget: InvokeRequest = {
  appId: "sample.app",
  method: "sample.method"
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
  leaseToken: "lease-1",
  invocationId: "invk-1"
};

// @ts-expect-error RespondRequest requires leaseToken.
const missingRespondLeaseToken: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  value: {
    ok: true
  }
};

// @ts-expect-error RespondRequest forbids value and error together.
const invalidRespondPayload: RespondRequest = {
  instanceId: "inst-1",
  instanceSessionToken: "session-1",
  invocationId: "invk-1",
  leaseToken: "lease-1",
  value: {
    ok: true
  },
  error: {
    code: 1001,
    message: "callee_failed"
  }
};

// @ts-expect-error VersionCompatibilityStatus must use a known literal.
const invalidCompatibilityStatus: VersionCompatibilityStatus = "outdated";

const invalidRegistrationWithPassword: AppInstanceRegistration = {
  ...instanceRegistration,
  // @ts-expect-error AppInstanceRegistration forbids password inside the instance payload.
  password: "secret-1"
};

const invalidRegistrationWithInstanceSessionToken: AppInstanceRegistration = {
  ...instanceRegistration,
  // @ts-expect-error AppInstanceRegistration forbids instanceSessionToken inside the instance payload.
  instanceSessionToken: "session-1"
};

// @ts-expect-error RegisteredAppInstance is not assignable to AppInstanceRegistration.
const invalidRegistrationFromRegistered: AppInstanceRegistration = registeredInstance;

void definition;
void definitionWithoutLaunch;
void definitionWithoutLaunchExePath;
void invokeRequest;
void instanceRegistration;
void invocation;
void respondWithValue;
void respondWithError;
void compatibilityStatus;
void compatibilityResult;
void unknownCompatibilityResult;
void registeredInstance;
void missingInvokeTarget;
void missingInvocationTarget;
void missingRespondPayload;
void missingRespondLeaseToken;
void invalidRespondPayload;
void invalidCompatibilityStatus;
void invalidRegistrationWithPassword;
void invalidRegistrationWithInstanceSessionToken;
void invalidRegistrationFromRegistered;
