import type {
  AppDefinition,
  AppInstance,
  DevHubEvent,
  Invocation,
  JsonObject,
  JsonValue,
  LaunchResult,
  NotifyResult,
  PingResult,
  PollResult,
  RequestResult
} from "./models.js";
import {
  ensureRecord,
  readAppId,
  readArray,
  readBoolean,
  readDate,
  readInstanceId,
  readInvocationId,
  readNumber,
  readObject,
  readOptionalBoolean,
  readOptionalInstanceIdOrNull,
  readOptionalIntAtLeast,
  readOptionalString,
  readOptionalStringOrNull,
  readPositiveInt,
  readString
} from "./validation.js";

export function parsePingResult(payload: unknown): PingResult {
  const record = ensureRecord(payload, "hub.ping.result");
  ensureOk(record, "hub.ping.result");

  return {
    ok: true,
    serverTimeUtc: readDate(record, "hub.ping.result", "serverTimeUtc"),
    echo: record.echo as JsonValue | undefined
  };
}

export function parseDefinitionsResult(payload: unknown): AppDefinition[] {
  const record = ensureRecord(payload, "hub.apps.listDefinitions.result");
  ensureOk(record, "hub.apps.listDefinitions.result");
  const definitions = readArray(record, "hub.apps.listDefinitions.result", "definitions");
  return definitions.map((item, index) => parseAppDefinition(item, `hub.apps.listDefinitions.result.definitions[${index}]`));
}

export function parseDefinitionResult(payload: unknown): AppDefinition {
  const record = ensureRecord(payload, "hub.apps.getDefinition.result");
  ensureOk(record, "hub.apps.getDefinition.result");
  return parseAppDefinition(
    readObject(record, "hub.apps.getDefinition.result", "definition"),
    "hub.apps.getDefinition.result.definition"
  );
}

export function parseRegisterInstanceResult(payload: unknown): AppInstance {
  const record = ensureRecord(payload, "hub.apps.registerInstance.result");
  ensureOk(record, "hub.apps.registerInstance.result");
  return parseAppInstance(
    readObject(record, "hub.apps.registerInstance.result", "instance"),
    "hub.apps.registerInstance.result.instance"
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
    value: record.value as JsonValue
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
  const displayName = readString(record, location, "displayName");
  const description = readOptionalString(record, location, "description");

  let capabilities: AppDefinition["capabilities"] = {
    rpc: true
  };
  if ("capabilities" in record && record.capabilities !== null && record.capabilities !== undefined) {
    const capabilitiesPayload = ensureRecord(record.capabilities, `${location}.capabilities`);
    const rpc = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "rpc");
    const events = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "events");
    capabilities = {
      rpc: rpc ?? true,
      ...(events !== undefined ? { events } : {})
    };
  }

  let launch: AppDefinition["launch"] | undefined;
  if ("launch" in record && record.launch !== null && record.launch !== undefined) {
    const launchPayload = ensureRecord(record.launch, `${location}.launch`);
    launch = {
      exePath: readString(launchPayload, `${location}.launch`, "exePath"),
      argsTemplate: readOptionalString(launchPayload, `${location}.launch`, "argsTemplate"),
      workingDirectory: readOptionalString(launchPayload, `${location}.launch`, "workingDirectory"),
      dedupeKeyTemplate: readOptionalString(launchPayload, `${location}.launch`, "dedupeKeyTemplate")
    };
  }

  return {
    appId,
    displayName,
    description,
    capabilities,
    launch
  };
}

export function parseAppInstance(payload: unknown, location: string): AppInstance {
  const record = ensureRecord(payload, location);
  const invokePayload = readObject(record, location, "invoke");

  let meta: JsonObject | undefined;
  if ("meta" in record && record.meta !== null && record.meta !== undefined) {
    meta = ensureRecord(record.meta, `${location}.meta`) as JsonObject;
  }

  return {
    instanceId: readInstanceId(record, location, "instanceId"),
    appId: readAppId(record, location, "appId"),
    scope: readOptionalStringOrNull(record, location, "scope"),
    pid: readPositiveInt(record, location, "pid"),
    registeredAtUtc: readDate(record, location, "registeredAtUtc"),
    lastSeenUtc: readDate(record, location, "lastSeenUtc"),
    invoke: {
      poll: readBoolean(invokePayload, `${location}.invoke`, "poll"),
      respond: readBoolean(invokePayload, `${location}.invoke`, "respond")
    },
    meta
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
  if ("options" in record && record.options !== null && record.options !== undefined) {
    const optionsPayload = ensureRecord(record.options, `${location}.options`);
    options = {
      ttlMs: readOptionalIntAtLeast(optionsPayload, `${location}.options`, "ttlMs", 1000),
      waitTimeoutMs: readOptionalIntAtLeast(optionsPayload, `${location}.options`, "waitTimeoutMs", 1),
      queueIfOffline: readOptionalBoolean(optionsPayload, `${location}.options`, "queueIfOffline"),
      autoLaunch: readOptionalBoolean(optionsPayload, `${location}.options`, "autoLaunch")
    };
  }

  let delivery: Invocation["delivery"] | undefined;
  if ("delivery" in record && record.delivery !== null && record.delivery !== undefined) {
    const deliveryPayload = ensureRecord(record.delivery, `${location}.delivery`);
    delivery = {
      leaseSeconds: readPositiveInt(deliveryPayload, `${location}.delivery`, "leaseSeconds"),
      attempt: readPositiveInt(deliveryPayload, `${location}.delivery`, "attempt")
    };
  }

  return {
    invocationId: readInvocationId(record, location, "invocationId"),
    appId: readAppId(record, location, "appId"),
    target: {
      scope: readOptionalStringOrNull(targetPayload, `${location}.target`, "scope"),
      instanceId: readOptionalInstanceIdOrNull(targetPayload, `${location}.target`, "instanceId")
    },
    method: readString(record, location, "method"),
    args: record.args as JsonValue | undefined,
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

  let parsedPayload: JsonObject | undefined;
  if ("payload" in record && record.payload !== null && record.payload !== undefined) {
    parsedPayload = ensureRecord(record.payload, `${location}.payload`) as JsonObject;
  }

  return {
    subscriptionId: readString(record, location, "subscriptionId"),
    type: readString(record, location, "type"),
    timeUtc: readDate(record, location, "timeUtc"),
    payload: parsedPayload
  };
}

function ensureOk(payload: Record<string, unknown>, location: string): void {
  const ok = readBoolean(payload, location, "ok");
  if (!ok) {
    throw new Error(`${location}.ok must be true.`);
  }
}
