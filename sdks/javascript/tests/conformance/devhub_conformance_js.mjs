#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import {
  DevHubClient,
  DevHubEventsClient,
  DevHubRpcError
} from "../../dist/index.js";
import { discoverRuntime } from "../../dist/runtime.js";

const WebSocketCtor = globalThis.WebSocket;
const CANONICAL_IDENTIFIER_PATTERN = "^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$";
const CANONICAL_IDENTIFIER_REGEX = new RegExp(CANONICAL_IDENTIFIER_PATTERN);

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

function normalizeDiscoveryError(explicitDataDir, error) {
  let reason = "discovery_failed";
  if (typeof explicitDataDir === "string" && path.basename(explicitDataDir).toLowerCase() === "runtime") {
    reason = "runtime_subdirectory_rejected";
  }

  return {
    reason,
    message: error instanceof Error ? error.message : String(error)
  };
}

async function runDiscovery(context) {
  const vector = context.vector;
  const request = vector.request ?? {};
  const explicitDataDir = typeof request.dataDir === "string" && request.dataDir.trim()
    ? request.dataDir
    : undefined;
  const environmentDataDir = typeof context.environmentDataDir === "string" && context.environmentDataDir.trim()
    ? context.environmentDataDir
    : undefined;
  const originalDataDirEnv = process.env.DEVHUB_DATA_DIR;

  try {
    if (environmentDataDir) {
      process.env.DEVHUB_DATA_DIR = environmentDataDir;
    }

    const connection = await discoverRuntime(explicitDataDir);
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "discovery",
      outcome: "success",
      actual: {
        runtimeDirectory: connection.runtimeDirectory,
        token: connection.token,
        runtime: {
          protocolVersion: connection.runtime.protocolVersion,
          httpBaseUrl: connection.runtime.httpBaseUrl,
          wsUrl: connection.runtime.wsUrl,
          tokenFile: connection.runtime.tokenFile
        }
      },
      error: null
    };
  } catch (error) {
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "discovery",
      outcome: "error",
      actual: normalizeDiscoveryError(explicitDataDir, error),
      error: null
    };
  } finally {
    if (originalDataDirEnv === undefined) {
      delete process.env.DEVHUB_DATA_DIR;
    } else {
      process.env.DEVHUB_DATA_DIR = originalDataDirEnv;
    }
  }
}

async function runInvocation(context) {
  const vector = context.vector;
  const request = vector.request ?? {};
  const operation = request.kind === "sdk.notify" ? "notify" : "request";
  const client = await DevHubClient.fromRuntime({
    clientId: typeof request.clientId === "string" && request.clientId.trim()
      ? request.clientId
      : "ConformanceInvocation",
    dataDir: context.dataDir
  });

  try {
    const invokeRequest = buildInvokeRequest(request.invokeRequest);
    if (operation === "notify") {
      const result = await client.notify(invokeRequest);
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "sdk-invocation",
        operation,
        outcome: "success",
        actual: {
          ok: result.ok,
          invocationId: result.invocationId
        },
        error: null
      };
    }

    const result = await client.request(invokeRequest);
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "sdk-invocation",
      operation,
      outcome: "success",
      actual: {
        ok: result.ok,
        invocationId: result.invocationId,
        value: result.value
      },
      error: null
    };
  } catch (error) {
    if (error instanceof DevHubRpcError) {
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "sdk-invocation",
        operation,
        outcome: "error",
        actual: normalizeInvocationError(error),
        error: null
      };
    }

    throw error;
  } finally {
    await client.dispose();
  }
}

async function runRpc(context) {
  const vector = context.vector;
  const connection = await discoverRuntime(context.dataDir);
  const response = await fetch(`${connection.runtime.httpBaseUrl}/rpc`, {
    method: "POST",
    headers: vector.http?.headers ?? {},
    body: normalizeRawRequestBody(vector.request)
  });
  const body = await response.text();
  return {
    sdk: "typescript",
    vectorId: vector.id,
    phase: "rpc",
    outcome: "success",
    actual: JSON.parse(body),
    error: null
  };
}

async function runEvents(context) {
  const vector = context.vector;
  const request = ensureRecord(context.vector?.request, "request");
  const steps = ensureArray(request.steps, "request.steps");
  const dataDir = context.dataDir;
  const rawRpcConnection = await discoverRuntime(dataDir);

  const eventClients = new Map();
  const eventIterators = new Map();
  const httpClients = new Map();
  const captures = {};
  const registeredInstances = [];

  try {
    for (let index = 0; index < steps.length; index += 1) {
      const step = ensureRecord(steps[index], `request.steps[${index}]`);
      const action = ensureString(step.action, `request.steps[${index}].action`);

      if (action === "create_events_client") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const clientId = typeof step.clientId === "string" && step.clientId.trim()
          ? step.clientId
          : typeof request.clientId === "string" && request.clientId.trim()
            ? request.clientId
            : `ConformanceEvents-${clientName}`;
        const client = await DevHubEventsClient.fromRuntime({ clientId, dataDir });
        eventClients.set(clientName, client);
        continue;
      }

      if (action === "authenticate") {
        const client = requireMapValue(eventClients, ensureString(step.client, `request.steps[${index}].client`), index, "events client");
        await client.authenticate();
        continue;
      }

      if (action === "subscribe") {
        const client = requireMapValue(eventClients, ensureString(step.client, `request.steps[${index}].client`), index, "events client");
        const captureAs = ensureString(step.captureAs, `request.steps[${index}].captureAs`);
        captures[captureAs] = await client.subscribe(step.types);
        continue;
      }

      if (action === "unsubscribe") {
        const client = requireMapValue(eventClients, ensureString(step.client, `request.steps[${index}].client`), index, "events client");
        const subscriptionId = String(resolveCaptureValue(step, captures, index, "subscriptionId"));
        await client.unsubscribe(subscriptionId);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = { ok: true };
        }
        continue;
      }

      if (action === "create_http_client") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const clientId = typeof step.clientId === "string" && step.clientId.trim()
          ? step.clientId
          : typeof request.clientId === "string" && request.clientId.trim()
            ? request.clientId
            : `ConformanceHttp-${clientName}`;
        const client = await DevHubClient.fromRuntime({ clientId, dataDir });
        httpClients.set(clientName, client);
        continue;
      }

      if (action === "register_instance") {
        const client = requireMapValue(httpClients, ensureString(step.client, `request.steps[${index}].client`), index, "http client");
        const instance = buildAppInstanceRegistration(ensureRecord(step.instance, `request.steps[${index}].instance`));
        const password = ensureString(step.password, `request.steps[${index}].password`);
        const registered = await client.registerInstance(instance, password);
        registeredInstances.push({
          clientName: ensureString(step.client, `request.steps[${index}].client`),
          instanceId: instance.instanceId,
          instanceSessionToken: registered.instanceSessionToken
        });
        continue;
      }

      if (action === "unregister_instance") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const client = requireMapValue(httpClients, clientName, index, "http client");
        const instanceId = String(resolveCaptureValue(step, captures, index, "instanceId"));
        const instanceSessionToken = findRegisteredInstanceSessionToken(registeredInstances, clientName, instanceId, index);
        await client.unregisterInstance(instanceId, instanceSessionToken);
        removeRegisteredInstance(registeredInstances, clientName, instanceId);
        continue;
      }

      if (action === "validate_definition") {
        const response = await sendRawRpc(
          rawRpcConnection,
          `sdk-events-validate-definition-${index}`,
          "hub.apps.validateDefinition",
          {
            definition: ensureRecord(step.definition, `request.steps[${index}].definition`)
          }
        );
        const result = readRawResult(response, `request.steps[${index}]`);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = result;
        }
        continue;
      }

      if (action === "upsert_definition") {
        const response = await sendRawRpc(
          rawRpcConnection,
          `sdk-events-upsert-definition-${index}`,
          "hub.apps.upsertDefinition",
          {
            definition: ensureRecord(step.definition, `request.steps[${index}].definition`)
          }
        );
        const result = readRawResult(response, `request.steps[${index}]`);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = ensureRecord(
            result.definition,
            `request.steps[${index}].captureAs`
          );
        }
        continue;
      }

      if (action === "get_definition") {
        const response = await sendRawRpc(
          rawRpcConnection,
          `sdk-events-get-definition-${index}`,
          "hub.apps.getDefinition",
          buildDefinitionIdentityParams(step, captures, index)
        );
        const result = readRawResult(response, `request.steps[${index}]`);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = ensureRecord(
            result.definition,
            `request.steps[${index}].captureAs`
          );
        }
        continue;
      }

      if (action === "delete_definition") {
        const response = await sendRawRpc(
          rawRpcConnection,
          `sdk-events-delete-definition-${index}`,
          "hub.apps.deleteDefinition",
          buildDefinitionIdentityParams(step, captures, index)
        );
        readRawResult(response, `request.steps[${index}]`);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = { ok: true };
        }
        continue;
      }

      if (action === "list_definitions") {
        const response = await sendRawRpc(
          rawRpcConnection,
          `sdk-events-list-definitions-${index}`,
          "hub.apps.listDefinitions",
          {
            scope: null
          }
        );
        const result = readRawResult(response, `request.steps[${index}]`);
        captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = ensureArray(
          result.definitions,
          `request.steps[${index}].captureAs`
        );
        continue;
      }

      if (action === "get_instance") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const client = eventClients.get(clientName) ?? httpClients.get(clientName);
        if (!client) {
          throw new Error(`request.steps[${index}] 未找到 client：${clientName}`);
        }

        const instanceId = String(resolveCaptureValue(step, captures, index, "instanceId"));
        const instance = await client.getInstance(instanceId);
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = normalizeAppInstance(instance);
        }
        continue;
      }

      if (action === "read_event") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const captureAs = ensureString(step.captureAs, `request.steps[${index}].captureAs`);
        let iterator = eventIterators.get(clientName);
        if (!iterator) {
          iterator = requireMapValue(eventClients, clientName, index, "events client").readEvents()[Symbol.asyncIterator]();
          eventIterators.set(clientName, iterator);
        }
        const event = await nextWithTimeout(iterator, readTimeoutMs(step, index));
        if (event.done) {
          throw new Error(`request.steps[${index}] 事件流已结束。`);
        }
        captures[captureAs] = normalizeEvent(event.value);
        continue;
      }

      if (action === "expect_no_event") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const captureAs = ensureString(step.captureAs, `request.steps[${index}].captureAs`);
        let iterator = eventIterators.get(clientName);
        if (!iterator) {
          iterator = requireMapValue(eventClients, clientName, index, "events client").readEvents()[Symbol.asyncIterator]();
          eventIterators.set(clientName, iterator);
        }
        try {
          const event = await nextWithTimeout(iterator, readTimeoutMs(step, index));
          captures[captureAs] = event.done
            ? { status: "closed" }
            : { status: "received", event: normalizeEvent(event.value) };
        } catch (error) {
          if (String(error).includes("timeout")) {
            captures[captureAs] = { status: "timeout" };
            continue;
          }
          throw error;
        }
        continue;
      }

      if (action === "close_events_client") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const client = eventClients.get(clientName);
        eventIterators.delete(clientName);
        eventClients.delete(clientName);
        if (client) {
          await client.dispose();
        }
        continue;
      }

      if (action === "dispose_http_client") {
        const clientName = ensureString(step.client, `request.steps[${index}].client`);
        const client = httpClients.get(clientName);
        httpClients.delete(clientName);
        if (client) {
          await client.dispose();
        }
        continue;
      }

      if (action === "sleep") {
        await new Promise(resolve => setTimeout(resolve, readTimeoutMs(step, index)));
        continue;
      }

      throw new Error(`request.steps[${index}].action 不支持：${action}`);
    }

    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "sdk-events",
      outcome: "success",
      actual: captures,
      error: null
    };
  } catch (error) {
    if (error instanceof DevHubRpcError) {
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "sdk-events",
        outcome: "error",
        actual: normalizeInvocationError(error),
        error: null
      };
    }

    throw error;
  } finally {
    for (let index = registeredInstances.length - 1; index >= 0; index -= 1) {
      const registered = registeredInstances[index];
      const client = httpClients.get(registered.clientName);
      if (!client) {
        continue;
      }
      try {
        await client.unregisterInstance(registered.instanceId, registered.instanceSessionToken);
      } catch {
      }
    }
    for (const client of httpClients.values()) {
      try {
        await client.dispose();
      } catch {
      }
    }
    for (const client of eventClients.values()) {
      try {
        await client.dispose();
      } catch {
      }
    }
  }
}

async function runWs(context) {
  const vector = context.vector;
  const request = ensureRecord(vector.request, "request");
  const steps = ensureArray(request.steps, "request.steps");
  const connection = await discoverRuntime(context.dataDir);
  const socket = await createWebSocket(connection.runtime.wsUrl);
  const captures = {};

  try {
    for (let index = 0; index < steps.length; index += 1) {
      const step = ensureRecord(steps[index], `request.steps[${index}]`);
      const action = ensureString(step.action, `request.steps[${index}].action`);

      if (action === "send") {
        socket.send(normalizeRawRequestBody(step.message));
        continue;
      }

      if (action === "receive") {
        const captureAs = ensureString(step.captureAs, `request.steps[${index}].captureAs`);
        const payload = await receiveWsMessage(socket, readTimeoutMs(step, index));
        captures[captureAs] = parseWsPayload(payload);
        continue;
      }

      if (action === "wait_closed") {
        const captureAs = ensureString(step.captureAs, `request.steps[${index}].captureAs`);
        captures[captureAs] = { closed: await waitForSocketClose(socket, readTimeoutMs(step, index)) };
        continue;
      }

      if (action === "sleep") {
        await new Promise(resolve => setTimeout(resolve, readTimeoutMs(step, index)));
        continue;
      }

      throw new Error(`request.steps[${index}].action 不支持：${action}`);
    }

    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "ws",
      outcome: "success",
      actual: captures,
      error: null
    };
  } finally {
    if (socket.readyState === WebSocketCtor.OPEN || socket.readyState === WebSocketCtor.CONNECTING) {
      try {
        socket.close();
      } catch {
      }
    }
  }
}

function buildInvokeRequest(payload) {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    throw new Error("request.invokeRequest 必须为对象。");
  }

  const request = {
    appId: payload.appId,
    method: payload.method
  };

  if ("target" in payload && payload.target !== null) {
    request.target = payload.target;
  }

  if ("args" in payload) {
    request.args = payload.args;
  }

  if ("options" in payload && payload.options !== null) {
    request.options = payload.options;
  }

  return request;
}

function buildAppInstanceRegistration(payload) {
  const invoke = ensureRecord(payload.invoke, "instance.invoke");
  return {
    instanceId: ensureCanonicalIdentifier(payload.instanceId, "instance.instanceId"),
    appId: ensureCanonicalIdentifier(payload.appId, "instance.appId"),
    scope: ensureScopeString(payload.scope, "instance.scope"),
    pid: ensureInteger(payload.pid, "instance.pid"),
    invoke: {
      poll: ensureBoolean(invoke.poll, "instance.invoke.poll"),
      respond: ensureBoolean(invoke.respond, "instance.invoke.respond")
    },
    meta: payload.meta
  };
}

function buildAppDefinition(payload) {
  const definition = {
    appId: ensureCanonicalIdentifier(payload.appId, "definition.appId"),
    scope: ensureScopeString(payload.scope, "definition.scope"),
    displayName: ensureStringValue(payload.displayName, "definition.displayName")
  };

  if ("description" in payload && payload.description !== undefined) {
    definition.description = ensureStringValue(payload.description, "definition.description");
  }

  if ("capabilities" in payload && payload.capabilities !== undefined) {
    const capabilities = ensureRecord(payload.capabilities, "definition.capabilities");
    definition.capabilities = {};
    if ("rpc" in capabilities) {
      definition.capabilities.rpc = ensureBoolean(capabilities.rpc, "definition.capabilities.rpc");
    }
    if ("events" in capabilities) {
      definition.capabilities.events = ensureBoolean(capabilities.events, "definition.capabilities.events");
    }
  }

  if ("launch" in payload && payload.launch !== undefined) {
    const launch = ensureRecord(payload.launch, "definition.launch");
    definition.launch = {
      exePath: ensureStringValue(launch.exePath, "definition.launch.exePath")
    };
    if ("argsTemplate" in launch && launch.argsTemplate !== undefined) {
      definition.launch.argsTemplate = ensureStringValue(launch.argsTemplate, "definition.launch.argsTemplate");
    }
    if ("workingDirectory" in launch && launch.workingDirectory !== undefined) {
      definition.launch.workingDirectory = ensureStringValue(
        launch.workingDirectory,
        "definition.launch.workingDirectory"
      );
    }
    if ("dedupeKeyTemplate" in launch && launch.dedupeKeyTemplate !== undefined) {
      definition.launch.dedupeKeyTemplate = ensureStringValue(
        launch.dedupeKeyTemplate,
        "definition.launch.dedupeKeyTemplate"
      );
    }
  }

  return definition;
}

function normalizeInvocationError(error) {
  const actual = {
    code: error.code,
    message: error.message
  };

  if (error.reason !== null) {
    actual.reason = error.reason;
  }

  if (error.invocationId !== null) {
    actual.invocationId = error.invocationId;
  }

  if (error.calleeError !== null) {
    actual.calleeError = {
      code: error.calleeError.code,
      message: error.calleeError.message
    };
    if (error.calleeError.data !== undefined) {
      actual.calleeError.data = error.calleeError.data;
    }
  }

  return actual;
}

function normalizeEvent(event) {
  return {
    subscriptionId: event.subscriptionId,
    type: event.type,
    payload: event.payload
  };
}

function normalizeAppInstance(instance) {
  const normalized = {
    instanceId: instance.instanceId,
    appId: instance.appId,
    scope: instance.scope,
    pid: instance.pid,
    registeredAtUtc: instance.registeredAtUtc.toISOString(),
    lastSeenUtc: instance.lastSeenUtc.toISOString(),
    invoke: {
      poll: instance.invoke.poll,
      respond: instance.invoke.respond
    }
  };

  if (instance.meta !== undefined) {
    normalized.meta = instance.meta;
  }

  return normalized;
}

function findRegisteredInstanceSessionToken(registeredInstances, clientName, instanceId, index) {
  for (let registeredIndex = registeredInstances.length - 1; registeredIndex >= 0; registeredIndex -= 1) {
    const registered = registeredInstances[registeredIndex];
    if (registered.clientName === clientName && registered.instanceId === instanceId) {
      return registered.instanceSessionToken;
    }
  }

  throw new Error(`request.steps[${index}] 未找到实例会话凭据：${clientName}/${instanceId}`);
}

function removeRegisteredInstance(registeredInstances, clientName, instanceId) {
  for (let index = registeredInstances.length - 1; index >= 0; index -= 1) {
    const registered = registeredInstances[index];
    if (registered.clientName === clientName && registered.instanceId === instanceId) {
      registeredInstances.splice(index, 1);
      return;
    }
  }
}

function normalizeDefinitionValidationResult(result) {
  return {
    ok: result.ok,
    valid: result.valid,
    errors: result.errors
  };
}

async function sendRawRpc(connection, requestId, method, params) {
  const response = await fetch(`${connection.runtime.httpBaseUrl}/rpc`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${connection.token}`,
      "X-DevHub-Protocol": "1",
      "X-DevHub-ClientId": "ConformanceRawDefinitionRpc",
      "X-DevHub-ClientSessionId": "00000000-0000-0000-0000-000000000099"
    },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: requestId,
      method,
      params
    })
  });
  return JSON.parse(await response.text());
}

function readRawResult(response, pathLabel) {
  if (!response || typeof response !== "object" || Array.isArray(response)) {
    throw new Error(`${pathLabel} 定义 RPC 响应非法。`);
  }

  if ("error" in response) {
    throw new Error(`${pathLabel} 定义 RPC 返回错误：${JSON.stringify(response.error)}`);
  }

  const result = ensureRecord(response.result, `${pathLabel}.result`);
  if (result.ok !== true) {
    throw new Error(`${pathLabel} 定义 RPC 缺少 result.ok=true。`);
  }

  return result;
}

function buildDefinitionIdentityParams(step, captures, index) {
  const appId = ensureCanonicalIdentifier(
    String(resolveCaptureValue(step, captures, index, "appId")),
    `request.steps[${index}].appId`
  );
  const scope = resolveCaptureValue(step, captures, index, "scope");
  return {
    appId,
    scope: ensureScopeString(scope, `request.steps[${index}].scope`)
  };
}

function normalizeRawRequestBody(request) {
  if (typeof request === "string") {
    return request;
  }

  return JSON.stringify(request);
}

function resolveCaptureValue(step, captures, index, fieldName) {
  const referenceField = `${fieldName}Ref`;
  if (referenceField in step) {
    const key = ensureString(step[referenceField], `request.steps[${index}].${referenceField}`);
    if (!(key in captures)) {
      throw new Error(`request.steps[${index}].${referenceField} 引用不存在：${key}`);
    }
    return captures[key];
  }

  return step[fieldName];
}

function parseWsPayload(payload) {
  if (typeof payload !== "string") {
    return payload;
  }

  try {
    return JSON.parse(payload);
  } catch {
    return payload;
  }
}

function readTimeoutMs(step, index) {
  const value = step.timeoutMs ?? step.waitMs ?? 1000;
  return ensureNonNegativeInteger(value, `request.steps[${index}].timeoutMs`);
}

function ensureRecord(value, pathLabel) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${pathLabel} 必须为对象。`);
  }
  return value;
}

function ensureArray(value, pathLabel) {
  if (!Array.isArray(value)) {
    throw new Error(`${pathLabel} 必须为数组。`);
  }
  return value;
}

function ensureString(value, pathLabel) {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`${pathLabel} 必须为非空字符串。`);
  }
  return value;
}

function ensureStringValue(value, pathLabel) {
  if (typeof value !== "string") {
    throw new Error(`${pathLabel} 必须为字符串。`);
  }
  return value;
}

function ensureScopeString(value, pathLabel) {
  if (typeof value !== "string" || (value.length > 0 && !CANONICAL_IDENTIFIER_REGEX.test(value))) {
    throw new Error(`${pathLabel} 必须为 "" 或匹配 ${CANONICAL_IDENTIFIER_PATTERN}。`);
  }

  return value;
}

function ensureCanonicalIdentifier(value, pathLabel) {
  if (typeof value !== "string" || !CANONICAL_IDENTIFIER_REGEX.test(value)) {
    throw new Error(`${pathLabel} 必须匹配 ${CANONICAL_IDENTIFIER_PATTERN}。`);
  }

  return value;
}

function ensureInteger(value, pathLabel) {
  if (!Number.isInteger(value)) {
    throw new Error(`${pathLabel} 必须为整数。`);
  }
  return value;
}

function ensureNonNegativeInteger(value, pathLabel) {
  if (!Number.isInteger(value) || value < 0) {
    throw new Error(`${pathLabel} 必须为大于等于 0 的整数。`);
  }
  return value;
}

function ensureBoolean(value, pathLabel) {
  if (typeof value !== "boolean") {
    throw new Error(`${pathLabel} 必须为布尔值。`);
  }
  return value;
}

function requireMapValue(collection, key, index, label) {
  const value = collection.get(key);
  if (!value) {
    throw new Error(`request.steps[${index}].client 引用的 ${label} 不存在：${key}`);
  }
  return value;
}

async function nextWithTimeout(iterator, timeoutMs) {
  return await Promise.race([
    iterator.next(),
    new Promise((_, reject) => {
      setTimeout(() => reject(new Error("timeout")), timeoutMs);
    })
  ]);
}

async function createWebSocket(url) {
  if (typeof WebSocketCtor !== "function") {
    throw new Error("当前 Node 运行时不支持全局 WebSocket。");
  }

  const socket = new WebSocketCtor(url);
  await new Promise((resolve, reject) => {
    const cleanup = () => {
      socket.removeEventListener("open", onOpen);
      socket.removeEventListener("error", onError);
    };

    const onOpen = () => {
      cleanup();
      resolve();
    };

    const onError = (event) => {
      cleanup();
      reject(event.error ?? new Error("WebSocket 连接失败。"));
    };

    socket.addEventListener("open", onOpen);
    socket.addEventListener("error", onError);
  });
  return socket;
}

async function receiveWsMessage(socket, timeoutMs) {
  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error("timeout"));
    }, timeoutMs);

    const cleanup = () => {
      clearTimeout(timeout);
      socket.removeEventListener("message", onMessage);
      socket.removeEventListener("close", onClose);
      socket.removeEventListener("error", onError);
    };

    const onMessage = async (event) => {
      cleanup();
      try {
        resolve(await normalizeWsData(event.data));
      } catch (error) {
        reject(error);
      }
    };

    const onClose = () => {
      cleanup();
      reject(new Error("socket closed before message"));
    };

    const onError = (event) => {
      cleanup();
      reject(event.error ?? new Error("WebSocket 接收失败。"));
    };

    socket.addEventListener("message", onMessage);
    socket.addEventListener("close", onClose);
    socket.addEventListener("error", onError);
  });
}

async function waitForSocketClose(socket, timeoutMs) {
  if (socket.readyState === WebSocketCtor.CLOSED) {
    return true;
  }

  return await new Promise((resolve) => {
    const timeout = setTimeout(() => {
      cleanup();
      resolve(false);
    }, timeoutMs);

    const cleanup = () => {
      clearTimeout(timeout);
      socket.removeEventListener("close", onClose);
      socket.removeEventListener("error", onError);
    };

    const onClose = () => {
      cleanup();
      resolve(true);
    };

    const onError = () => {
      cleanup();
      resolve(false);
    };

    socket.addEventListener("close", onClose);
    socket.addEventListener("error", onError);
  });
}

async function normalizeWsData(data) {
  if (typeof data === "string") {
    return data;
  }

  if (data instanceof ArrayBuffer) {
    return Buffer.from(data).toString("utf-8");
  }

  if (ArrayBuffer.isView(data)) {
    return Buffer.from(data.buffer, data.byteOffset, data.byteLength).toString("utf-8");
  }

  if (typeof Blob !== "undefined" && data instanceof Blob) {
    return Buffer.from(await data.arrayBuffer()).toString("utf-8");
  }

  return String(data);
}

async function main() {
  if (process.argv.length !== 3) {
    emit({
      sdk: "typescript",
      vectorId: null,
      phase: null,
      outcome: "error",
      actual: null,
      error: { message: "用法错误：需要 execution-context.json 路径。" }
    });
    return;
  }

  try {
    const context = JSON.parse(await readFile(process.argv[2], "utf-8"));
    const vector = context.vector ?? {};
    const kind = vector.request?.kind;
    const result = vector.expectedDiscovery
      ? await runDiscovery(context)
      : kind === "sdk.notify" || kind === "sdk.request"
        ? await runInvocation(context)
        : kind === "sdk.events"
          ? await runEvents(context)
          : kind === "raw.ws" || vector.transport === "ws"
            ? await runWs(context)
            : await runRpc(context);
    emit(result);
  } catch (error) {
    emit({
      sdk: "typescript",
      vectorId: null,
      phase: null,
      outcome: "error",
      actual: null,
      error: {
        message: error instanceof Error ? error.message : String(error)
      }
    });
  }
}

await main();
