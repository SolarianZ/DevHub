import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstanceRegistration,
  InvokeRequest,
  LaunchRequest,
  ListInstancesRequest,
  PollRequest,
  RespondRequest
} from "./models.js";
import {
  ensureAppId,
  ensureDefinitionScope,
  ensureInputBoolean,
  ensureInstanceId,
  ensureInvocationId,
  ensureJsonObject,
  ensureJsonValue,
  ensureOptionalInputBoolean,
  ensureOptionalInputIntegerAtLeast,
  ensureOptionalInputIntegerInRange,
  ensureOptionalInputRecord,
  ensureOptionalDefinitionScope,
  ensureOptionalInputString,
  ensureOptionalInputStringOrNull,
  ensureRequiredInputString,
  ensureRequiredInputStringValue
} from "./validation.js";

export function buildGetDefinitionParams(identity: AppDefinitionIdentity): Record<string, unknown> {
  return buildDefinitionIdentityPayload(identity, "identity");
}

export function buildValidateDefinitionParams(definition: AppDefinition): Record<string, unknown> {
  return {
    definition: buildDefinitionPayload(definition)
  };
}

export function buildUpsertDefinitionParams(definition: AppDefinition): Record<string, unknown> {
  return {
    definition: buildDefinitionPayload(definition)
  };
}

export function buildDeleteDefinitionParams(identity: AppDefinitionIdentity): Record<string, unknown> {
  return buildDefinitionIdentityPayload(identity, "identity");
}

export function buildRegisterInstanceParams(
  instance: AppInstanceRegistration,
  password: string
): Record<string, unknown> {
  if (!instance) {
    throw new Error("instance cannot be empty.");
  }

  const instanceId = ensureInstanceId(instance.instanceId, "instanceId");
  const appId = ensureAppId(instance.appId, "appId");
  const scope = ensureOptionalInputStringOrNull(instance.scope, "scope");
  const normalizedPassword = ensureRequiredInputString(password, "password");

  if (!instance.invoke) {
    throw new Error("invoke cannot be empty.");
  }

  const poll = ensureInputBoolean(instance.invoke.poll, "invoke.poll");
  const respond = ensureInputBoolean(instance.invoke.respond, "invoke.respond");

  if (!Number.isInteger(instance.pid) || instance.pid < 1) {
    throw new Error("pid must be an integer greater than or equal to 1.");
  }

  const payload: Record<string, unknown> = {
    password: normalizedPassword,
    instance: {
      instanceId,
      appId,
      pid: instance.pid,
      invoke: {
        poll,
        respond
      }
    }
  };

  if (scope !== undefined && scope !== null) {
    (payload.instance as Record<string, unknown>).scope = scope;
  }

  if (instance.meta !== undefined) {
    (payload.instance as Record<string, unknown>).meta = ensureJsonObject(instance.meta, "meta");
  }

  return payload;
}

export function buildHeartbeatParams(instanceId: string): Record<string, unknown> {
  return {
    instanceId: ensureInstanceId(instanceId, "instanceId")
  };
}

export function buildUnregisterParams(instanceId: string, password: string): Record<string, unknown> {
  return {
    instanceId: ensureInstanceId(instanceId, "instanceId"),
    password: ensureRequiredInputString(password, "password")
  };
}

export function buildListInstancesParams(request?: ListInstancesRequest): Record<string, unknown> | undefined {
  if (!request) {
    return undefined;
  }

  const payload: Record<string, unknown> = {};
  const appIdRaw = ensureOptionalInputString(request.appId, "appId", false);
  const appId = appIdRaw === undefined ? undefined : ensureAppId(appIdRaw, "appId");
  if (appId !== undefined) {
    payload.appId = appId;
  }

  const scope = ensureOptionalInputStringOrNull(request.scope, "scope");
  if (scope !== undefined) {
    payload.scope = scope;
  }

  const includeAllScopes = ensureOptionalInputBoolean(request.includeAllScopes, "includeAllScopes");
  if (includeAllScopes !== undefined) {
    payload.includeAllScopes = includeAllScopes;
  }

  const includeOffline = ensureOptionalInputBoolean(request.includeOffline, "includeOffline");
  if (includeOffline !== undefined) {
    payload.includeOffline = includeOffline;
  }

  return Object.keys(payload).length > 0 ? payload : undefined;
}

export function buildLaunchParams(request: LaunchRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request cannot be empty.");
  }

  const appId = ensureAppId(request.appId, "appId");
  const scope = ensureOptionalDefinitionScope(request.scope, "scope");
  const dedupeKey = ensureOptionalInputString(request.dedupeKey, "dedupeKey", false);
  const waitForRegisterMs = ensureOptionalInputIntegerAtLeast(
    request.waitForRegisterMs,
    "waitForRegisterMs",
    0,
    "waitForRegisterMs 必须为大于等于 0 的整数。"
  );

  const payload: Record<string, unknown> = { appId };
  if (scope !== undefined) {
    payload.scope = scope;
  }

  if (dedupeKey !== undefined && dedupeKey !== null) {
    payload.dedupeKey = dedupeKey;
  }

  if (waitForRegisterMs !== undefined) {
    payload.waitForRegisterMs = waitForRegisterMs;
  }

  return payload;
}

export function buildInvokeParams(request: InvokeRequest, isRequest: boolean): Record<string, unknown> {
  if (!request) {
    throw new Error("request cannot be empty.");
  }

  const appId = ensureAppId(request.appId, "appId");
  const method = ensureRequiredInputString(request.method, "method");
  const target = ensureOptionalInputRecord(request.target, "target");
  const options = ensureOptionalInputRecord(request.options, "options");
  const targetScope = ensureOptionalInputStringOrNull(target?.scope, "target.scope");
  const targetInstanceIdRaw = ensureOptionalInputString(
    target?.instanceId,
    "target.instanceId",
    false,
    "target.instanceId cannot be blank.",
    true
  );
  const targetInstanceId = targetInstanceIdRaw === undefined || targetInstanceIdRaw === null
    ? targetInstanceIdRaw
    : ensureInstanceId(targetInstanceIdRaw, "target.instanceId");

  const ttlMs = ensureOptionalInputIntegerAtLeast(
    options?.ttlMs,
    "ttlMs",
    1000,
    "ttlMs 必须大于等于 1000。"
  ) ?? (isRequest ? 300000 : 60000);

  if (!isRequest && options?.waitTimeoutMs !== undefined) {
    throw new Error("hub.invoke.notify 不支持 waitTimeoutMs。");
  }

  const waitTimeoutMs = isRequest
    ? ensureOptionalInputIntegerAtLeast(
      options?.waitTimeoutMs,
      "waitTimeoutMs",
      1,
      "waitTimeoutMs 必须大于等于 1。"
    ) ?? 120000
    : undefined;

  const queueIfOffline = ensureOptionalInputBoolean(options?.queueIfOffline, "queueIfOffline") ?? true;
  const autoLaunch = ensureOptionalInputBoolean(options?.autoLaunch, "autoLaunch")
    ?? (targetInstanceId === undefined || targetInstanceId === null);

  if (waitTimeoutMs !== undefined && waitTimeoutMs > ttlMs) {
    throw new Error("waitTimeoutMs 不能大于 ttlMs。");
  }

  if (targetInstanceId !== undefined && targetInstanceId !== null && autoLaunch) {
    throw new Error("指定 target.instanceId 时不能启用 autoLaunch。");
  }

  if (autoLaunch && !queueIfOffline) {
    throw new Error("启用 autoLaunch 时 queueIfOffline 必须为 true。");
  }

  const payload: Record<string, unknown> = {
    appId,
    method,
    options: {
      ttlMs,
      queueIfOffline,
      autoLaunch
    }
  };

  if (request.args !== undefined) {
    payload.args = ensureJsonValue(request.args, "args");
  }

  if (isRequest) {
    (payload.options as Record<string, unknown>).waitTimeoutMs = waitTimeoutMs;
  }

  if (target !== undefined) {
    payload.target = {
      scope: targetScope ?? null,
      instanceId: targetInstanceId ?? null
    };
  }

  return payload;
}

export function buildPollParams(request: PollRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request cannot be empty.");
  }

  const instanceId = ensureInstanceId(request.instanceId, "instanceId");
  const maxCount = ensureOptionalInputIntegerInRange(
    request.maxCount,
    "maxCount",
    1,
    100,
    "maxCount 必须位于 1..100。"
  ) ?? 10;
  const waitMs = ensureOptionalInputIntegerAtLeast(
    request.waitMs,
    "waitMs",
    0,
    "waitMs 必须为大于等于 0 的整数。"
  ) ?? 25000;

  return {
    instanceId,
    maxCount,
    waitMs
  };
}

export function buildRespondParams(request: RespondRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request cannot be empty.");
  }

  const instanceId = ensureInstanceId(request.instanceId, "instanceId");
  const invocationId = ensureInvocationId(request.invocationId, "invocationId");

  const hasValue = request.value !== undefined;
  const hasError = request.error !== undefined;
  if (hasValue === hasError) {
    throw new Error("RespondRequest 必须且只能包含 value 或 error 之一。");
  }

  const payload: Record<string, unknown> = {
    instanceId,
    invocationId
  };

  if (hasValue) {
    payload.value = ensureJsonValue(request.value, "value");
  } else {
    const errorCode = request.error?.code;
    if (!Number.isInteger(errorCode)) {
      throw new Error("error.code 必须为整数。");
    }

    const errorPayload: Record<string, unknown> = {
      code: errorCode,
      message: ensureRequiredInputString(request.error?.message, "error.message")
    };

    if (request.error?.data !== undefined) {
      errorPayload.data = ensureJsonObject(request.error.data, "error.data");
    }

    payload.error = errorPayload;
  }

  return payload;
}

function buildDefinitionPayload(definition: AppDefinition): Record<string, unknown> {
  if (!definition || typeof definition !== "object" || Array.isArray(definition)) {
    throw new Error("definition cannot be empty.");
  }

  const payload: Record<string, unknown> = {
    appId: ensureAppId(definition.appId, "definition.appId"),
    scope: ensureDefinitionScope(definition.scope, "definition.scope"),
    displayName: ensureRequiredInputStringValue(definition.displayName, "definition.displayName")
  };

  if (definition.description !== undefined) {
    payload.description = ensureRequiredInputStringValue(definition.description, "definition.description");
  }

  if (definition.capabilities !== undefined) {
    if (!definition.capabilities || typeof definition.capabilities !== "object" || Array.isArray(definition.capabilities)) {
      throw new Error("definition.capabilities must be an object.");
    }

    const capabilitiesPayload: Record<string, unknown> = {};
    if (definition.capabilities.rpc !== undefined) {
      capabilitiesPayload.rpc = ensureInputBoolean(definition.capabilities.rpc, "definition.capabilities.rpc");
    }
    if (definition.capabilities.events !== undefined) {
      capabilitiesPayload.events = ensureInputBoolean(definition.capabilities.events, "definition.capabilities.events");
    }
    payload.capabilities = capabilitiesPayload;
  }

  if (definition.launch !== undefined) {
    if (!definition.launch || typeof definition.launch !== "object" || Array.isArray(definition.launch)) {
      throw new Error("definition.launch must be an object.");
    }

    const launchPayload: Record<string, unknown> = {
      exePath: ensureRequiredInputStringValue(definition.launch.exePath, "definition.launch.exePath")
    };

    if (definition.launch.argsTemplate !== undefined) {
      launchPayload.argsTemplate = ensureRequiredInputStringValue(
        definition.launch.argsTemplate,
        "definition.launch.argsTemplate"
      );
    }
    if (definition.launch.workingDirectory !== undefined) {
      launchPayload.workingDirectory = ensureRequiredInputStringValue(
        definition.launch.workingDirectory,
        "definition.launch.workingDirectory"
      );
    }
    if (definition.launch.dedupeKeyTemplate !== undefined) {
      launchPayload.dedupeKeyTemplate = ensureRequiredInputStringValue(
        definition.launch.dedupeKeyTemplate,
        "definition.launch.dedupeKeyTemplate"
      );
    }

    payload.launch = launchPayload;
  }

  return payload;
}

function buildDefinitionIdentityPayload(identity: AppDefinitionIdentity, propertyName: string): Record<string, unknown> {
  if (!identity || typeof identity !== "object" || Array.isArray(identity)) {
    throw new Error(`${propertyName} cannot be empty.`);
  }

  return {
    appId: ensureAppId(identity.appId, `${propertyName}.appId`),
    scope: ensureDefinitionScope(identity.scope, `${propertyName}.scope`)
  };
}
