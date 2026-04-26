import type {
  AppDefinition,
  AppInstance,
  DefinitionValidationResult,
  DevHubEvent,
  Invocation,
  JsonObject,
  JsonValue,
  LaunchResult,
  NotifyResult,
  PingResult,
  PollResult,
  RegisteredAppInstance,
  RequestResult,
  ValidationIssue
} from "./models.js";
import {
  APP_DEFINITION_DELETED,
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  ensureSupportedEventType
} from "./event-types.js";
import {
  ensureJsonObject,
  ensureJsonValue,
  ensureRecord,
  readAppId,
  readArray,
  readBoolean,
  readDate,
  readOptionalScopeString,
  readScopeString,
  readInstanceId,
  readInvocationId,
  readNumber,
  readObject,
  readOptionalBoolean,
  readOptionalInstanceIdOrNull,
  readOptionalIntAtLeast,
  readOptionalObject,
  readOptionalString,
  readPositiveInt,
  readStringValue,
  readString
} from "./validation.js";

export function parsePingResult(payload: unknown): PingResult {
  const record = ensureRecord(payload, "hub.ping.result");
  ensureOk(record, "hub.ping.result");

  return {
    ok: true,
    serverTimeUtc: readDate(record, "hub.ping.result", "serverTimeUtc"),
    echo: readOptionalJsonValue(record, "hub.ping.result", "echo")
  };
}

export function parseDefinitionsResult(payload: unknown): AppDefinition[] {
  const record = ensureRecord(payload, "hub.apps.listDefinitions.result");
  ensureOk(record, "hub.apps.listDefinitions.result");
  const definitions = readArray(record, "hub.apps.listDefinitions.result", "definitions");
  return definitions.map((item, index) => parseAppDefinition(item, `hub.apps.listDefinitions.result.definitions[${index}]`));
}

export function parseDefinitionResult(payload: unknown): AppDefinition {
  return parseDefinitionEnvelope(payload, "hub.apps.getDefinition.result");
}

export function parseInstanceResult(payload: unknown): AppInstance {
  const record = ensureRecord(payload, "hub.apps.getInstance.result");
  ensureOk(record, "hub.apps.getInstance.result");
  return parseAppInstance(
    readObject(record, "hub.apps.getInstance.result", "instance"),
    "hub.apps.getInstance.result.instance"
  );
}

export function parseDefinitionValidationResult(payload: unknown): DefinitionValidationResult {
  const record = ensureRecord(payload, "hub.apps.validateDefinition.result");
  ensureOk(record, "hub.apps.validateDefinition.result");
  const valid = readBoolean(record, "hub.apps.validateDefinition.result", "valid");
  const errors = readArray(record, "hub.apps.validateDefinition.result", "errors")
    .map((item, index) => parseValidationIssue(item, `hub.apps.validateDefinition.result.errors[${index}]`));

  if (valid && errors.length > 0) {
    throw new Error("hub.apps.validateDefinition.result.errors must be empty when valid is true.");
  }

  if (!valid && errors.length === 0) {
    throw new Error("hub.apps.validateDefinition.result.errors must contain at least one item when valid is false.");
  }

  return {
    ok: true,
    valid,
    errors
  };
}

export function parseUpsertDefinitionResult(payload: unknown): AppDefinition {
  return parseDefinitionEnvelope(payload, "hub.apps.upsertDefinition.result");
}

export function parseRegisterInstanceResult(payload: unknown): RegisteredAppInstance {
  const record = ensureRecord(payload, "hub.apps.registerInstance.result");
  ensureOk(record, "hub.apps.registerInstance.result");
  return {
    ...parseAppInstance(
      readObject(record, "hub.apps.registerInstance.result", "instance"),
      "hub.apps.registerInstance.result.instance"
    ),
    instanceSessionToken: readString(record, "hub.apps.registerInstance.result", "instanceSessionToken")
  };
}

function parseDefinitionEnvelope(payload: unknown, location: string): AppDefinition {
  const record = ensureRecord(payload, location);
  ensureOk(record, location);
  return parseAppDefinition(
    readObject(record, location, "definition"),
    `${location}.definition`
  );
}

export function parseHeartbeatResult(payload: unknown): Date {
  const record = ensureRecord(payload, "hub.apps.heartbeat.result");
  ensureOk(record, "hub.apps.heartbeat.result");
  return readDate(record, "hub.apps.heartbeat.result", "lastSeenUtc");
}

export function parseVoidOkResult(payload: unknown, location: string): void {
  const record = ensureRecord(payload, location);
  ensureOk(record, location);
}

export function parseInstancesResult(payload: unknown): AppInstance[] {
  const record = ensureRecord(payload, "hub.apps.listInstances.result");
  ensureOk(record, "hub.apps.listInstances.result");
  const instances = readArray(record, "hub.apps.listInstances.result", "instances");
  return instances.map((item, index) => parseAppInstance(item, `hub.apps.listInstances.result.instances[${index}]`));
}

export function parseLaunchResult(payload: unknown): LaunchResult {
  const record = ensureRecord(payload, "hub.apps.launch.result");
  ensureOk(record, "hub.apps.launch.result");

  const status = readString(record, "hub.apps.launch.result", "status");
  if (status !== "started" && status !== "starting" && status !== "already_running") {
    throw new Error("hub.apps.launch.result.status is invalid.");
  }

  const pidValue = record.pid;
  if (
    pidValue !== undefined
    && pidValue !== null
    && (typeof pidValue !== "number" || Number.isNaN(pidValue) || !Number.isInteger(pidValue) || pidValue < 1)
  ) {
    throw new Error("hub.apps.launch.result.pid is invalid.");
  }

  return {
    ok: true,
    status,
    pid: pidValue === undefined ? undefined : (pidValue as number | null),
    launchId: readString(record, "hub.apps.launch.result", "launchId")
  };
}

export function parseNotifyResult(payload: unknown): NotifyResult {
  const record = ensureRecord(payload, "hub.invoke.notify.result");
  ensureOk(record, "hub.invoke.notify.result");
  return {
    ok: true,
    invocationId: readInvocationId(record, "hub.invoke.notify.result", "invocationId")
  };
}

export function parseRequestResult(payload: unknown): RequestResult {
  const record = ensureRecord(payload, "hub.invoke.request.result");
  ensureOk(record, "hub.invoke.request.result");
  if (!("value" in record)) {
    throw new Error("hub.invoke.request.result.value is required.");
  }

  return {
    ok: true,
    invocationId: readInvocationId(record, "hub.invoke.request.result", "invocationId"),
    value: ensureJsonValue(record.value, "hub.invoke.request.result.value")
  };
}

export function parsePollResult(payload: unknown): PollResult {
  const record = ensureRecord(payload, "hub.invoke.poll.result");
  ensureOk(record, "hub.invoke.poll.result");
  const items = readArray(record, "hub.invoke.poll.result", "items");
  return {
    ok: true,
    serverTimeUtc: readDate(record, "hub.invoke.poll.result", "serverTimeUtc"),
    items: items.map((item, index) => parseInvocation(item, `hub.invoke.poll.result.items[${index}]`))
  };
}

export function parseAuthenticateResult(payload: unknown): void {
  const record = ensureRecord(payload, "hub.ws.authenticate.result");
  const ok = readBoolean(record, "hub.ws.authenticate.result", "ok");
  const protocolVersion = readNumber(record, "hub.ws.authenticate.result", "protocolVersion");
  if (!ok || protocolVersion !== 1) {
    throw new Error("hub.ws.authenticate returned an invalid result.");
  }
}

export function parseSubscriptionResult(payload: unknown): string {
  const record = ensureRecord(payload, "hub.events.subscribe.result");
  const ok = readBoolean(record, "hub.events.subscribe.result", "ok");
  const subscriptionId = readString(record, "hub.events.subscribe.result", "subscriptionId");
  if (!ok || !subscriptionId.trim()) {
    throw new Error("hub.events.subscribe returned an invalid result.");
  }

  return subscriptionId;
}

export function parseUnsubscribeResult(payload: unknown): void {
  const record = ensureRecord(payload, "hub.events.unsubscribe.result");
  const ok = readBoolean(record, "hub.events.unsubscribe.result", "ok");
  if (!ok) {
    throw new Error("hub.events.unsubscribe returned an invalid result.");
  }
}

export function parseAppDefinition(payload: unknown, location: string): AppDefinition {
  const record = ensureRecord(payload, location);
  const appId = readAppId(record, location, "appId");
  const displayName = readStringValue(record, location, "displayName");
  const description = readOptionalString(record, location, "description");

  let capabilities: AppDefinition["capabilities"] = {
    rpc: true
  };
  const capabilitiesPayload = readOptionalObject(record, location, "capabilities");
  if (capabilitiesPayload) {
    const rpc = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "rpc");
    const events = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "events");
    capabilities = {
      rpc: rpc ?? true,
      ...(events !== undefined ? { events } : {})
    };
  }

  let launch: AppDefinition["launch"] | undefined;
  const launchPayload = readOptionalObject(record, location, "launch");
  if (launchPayload) {
    launch = {
      exePath: readStringValue(launchPayload, `${location}.launch`, "exePath"),
      argsTemplate: readOptionalString(launchPayload, `${location}.launch`, "argsTemplate"),
      workingDirectory: readOptionalString(launchPayload, `${location}.launch`, "workingDirectory"),
      dedupeKeyTemplate: readOptionalString(launchPayload, `${location}.launch`, "dedupeKeyTemplate")
    };
  }

  return {
    appId,
    scope: readScopeString(record, location, "scope"),
    displayName,
    description,
    capabilities,
    launch
  };
}

export function parseAppInstance(payload: unknown, location: string): AppInstance {
  const record = ensureRecord(payload, location);
  ensureNoSensitiveInstanceFields(record, location);
  const invokePayload = readObject(record, location, "invoke");

  return {
    instanceId: readInstanceId(record, location, "instanceId"),
    appId: readAppId(record, location, "appId"),
    scope: readScopeString(record, location, "scope"),
    pid: readPositiveInt(record, location, "pid"),
    registeredAtUtc: readDate(record, location, "registeredAtUtc"),
    lastSeenUtc: readDate(record, location, "lastSeenUtc"),
    invoke: {
      poll: readBoolean(invokePayload, `${location}.invoke`, "poll"),
      respond: readBoolean(invokePayload, `${location}.invoke`, "respond")
    },
    meta: readOptionalJsonObject(record, location, "meta")
  };
}

export function parseInvocation(payload: unknown, location: string): Invocation {
  const record = ensureRecord(payload, location);
  const targetPayload = readObject(record, location, "target");
  const callerPayload = readObject(record, location, "caller");
  const kind = readString(record, location, "kind");
  if (kind !== "request" && kind !== "notify") {
    throw new Error(`${location}.kind is invalid.`);
  }

  let options: Invocation["options"] | undefined;
  const optionsPayload = readOptionalObject(record, location, "options");
  if (optionsPayload) {
    const ttlMs = readOptionalIntAtLeast(optionsPayload, `${location}.options`, "ttlMs", 1000);
    const waitTimeoutMs = readOptionalIntAtLeast(optionsPayload, `${location}.options`, "waitTimeoutMs", 1);
    const queueIfOffline = readOptionalBoolean(optionsPayload, `${location}.options`, "queueIfOffline");
    const autoLaunch = readOptionalBoolean(optionsPayload, `${location}.options`, "autoLaunch");
    options = {
      ttlMs,
      waitTimeoutMs,
      queueIfOffline,
      autoLaunch
    };

    if (
      waitTimeoutMs !== undefined
      && ttlMs !== undefined
      && waitTimeoutMs > ttlMs
    ) {
      throw new Error(`${location}.options.waitTimeoutMs must be less than or equal to ttlMs.`);
    }
  }

  let delivery: Invocation["delivery"] | undefined;
  const deliveryPayload = readOptionalObject(record, location, "delivery");
  if (deliveryPayload) {
    delivery = {
      leaseSeconds: readPositiveInt(deliveryPayload, `${location}.delivery`, "leaseSeconds"),
      attempt: readPositiveInt(deliveryPayload, `${location}.delivery`, "attempt")
    };
  }

  return {
    invocationId: readInvocationId(record, location, "invocationId"),
    appId: readAppId(record, location, "appId"),
    target: {
      scope: readScopeString(targetPayload, `${location}.target`, "scope"),
      instanceId: readOptionalInstanceIdOrNull(targetPayload, `${location}.target`, "instanceId")
    },
    method: readString(record, location, "method"),
    args: readOptionalJsonValue(record, location, "args"),
    kind,
    createdAtUtc: readDate(record, location, "createdAtUtc"),
    options,
    delivery,
    caller: {
      clientId: readString(callerPayload, `${location}.caller`, "clientId"),
      clientSessionId: readString(callerPayload, `${location}.caller`, "clientSessionId")
    }
  };
}

export function parseEvent(payload: unknown, location: string): DevHubEvent {
  const record = ensureRecord(payload, location);
  const type = ensureSupportedEventType(readString(record, location, "type"), `${location}.type`);
  const eventPayload = readOptionalJsonObject(record, location, "payload");
  validateEventPayload(type, eventPayload, `${location}.payload`);

  return {
    subscriptionId: readString(record, location, "subscriptionId"),
    type,
    timeUtc: readDate(record, location, "timeUtc"),
    payload: eventPayload
  };
}

function ensureOk(payload: Record<string, unknown>, location: string): void {
  const ok = readBoolean(payload, location, "ok");
  if (!ok) {
    throw new Error(`${location}.ok must be true.`);
  }
}

function readOptionalJsonValue(
  payload: Record<string, unknown>,
  location: string,
  key: string
): JsonValue | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  return ensureJsonValue(payload[key], `${location}.${key}`);
}

function parseValidationIssue(payload: unknown, location: string): ValidationIssue {
  const record = ensureRecord(payload, location);
  return {
    path: readStringValue(record, location, "path"),
    code: readStringValue(record, location, "code"),
    message: readStringValue(record, location, "message")
  };
}

function validateEventPayload(type: string, payload: JsonObject | undefined, location: string): void {
  if (
    type === APP_DEFINITION_UPSERTED
    || type === APP_DEFINITION_DELETED
    || type === APP_INSTANCE_REGISTERED
    || type === APP_INSTANCE_UNREGISTERED
  ) {
    if (payload === undefined) {
      throw new Error(`${location} is required.`);
    }
  }

  if (payload === undefined) {
    return;
  }

  if (type === APP_DEFINITION_UPSERTED) {
    const appId = readAppId(payload, location, "appId");
    const scope = readScopeString(payload, location, "scope");
    const definition = parseAppDefinition(readObject(payload, location, "definition"), `${location}.definition`);
    if (definition.appId !== appId) {
      throw new Error(`${location}.definition.appId must match ${location}.appId.`);
    }
    if (definition.scope !== scope) {
      throw new Error(`${location}.definition.scope must match ${location}.scope.`);
    }
    return;
  }

  if (type === APP_DEFINITION_DELETED) {
    readAppId(payload, location, "appId");
    readScopeString(payload, location, "scope");
    return;
  }

  if (type === APP_INSTANCE_REGISTERED || type === APP_INSTANCE_UNREGISTERED) {
    readAppId(payload, location, "appId");
    readInstanceId(payload, location, "instanceId");
    readOptionalScopeString(payload, location, "scope");
    ensureNoSensitiveInstanceFields(payload, location);
  }
}

function ensureNoSensitiveInstanceFields(payload: Record<string, unknown>, location: string): void {
  if ("password" in payload) {
    throw new Error(`${location}.password must not be present.`);
  }

  if ("instanceSessionToken" in payload) {
    throw new Error(`${location}.instanceSessionToken must not be present.`);
  }
}

function readOptionalJsonObject(
  payload: Record<string, unknown>,
  location: string,
  key: string
): JsonObject | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  return ensureJsonObject(payload[key], `${location}.${key}`);
}
