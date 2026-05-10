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
const SDK_RPC_METHODS = new Set([
  "hub.ping",
  "hub.getVersion",
  "hub.apps.listDefinitions",
  "hub.apps.getDefinition",
  "hub.apps.validateDefinition",
  "hub.apps.upsertDefinition",
  "hub.apps.deleteDefinition",
  "hub.apps.registerInstance",
  "hub.apps.heartbeat",
  "hub.apps.unregisterInstance",
  "hub.apps.listInstances",
  "hub.apps.getInstance",
  "hub.apps.launch",
  "hub.invoke.poll",
  "hub.invoke.respond"
]);

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

function normalizeDiscoveryError(explicitDataDir, error) {
  let reason = "discovery_failed";
  if (typeof explicitDataDir === "string" && path.basename(explicitDataDir).toLowerCase() === "runtime") {
    reason = "runtime_subdirectory_rejected";
  } else if (String(error instanceof Error ? error.message : error).includes("hub.json")) {
    reason = "invalid_runtime";
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
          pid: connection.runtime.pid,
          httpBaseUrl: connection.runtime.httpBaseUrl,
          wsUrl: connection.runtime.wsUrl,
          tokenFile: connection.runtime.tokenFile,
          startedAtUtc: connection.runtime.startedAtUtc.toISOString(),
          runtimeTuning: {
            leaseSeconds: connection.runtime.runtimeTuning.leaseSeconds,
            onlineThresholdSeconds: connection.runtime.runtimeTuning.onlineThresholdSeconds,
            launchDedupeWindowSeconds: connection.runtime.runtimeTuning.launchDedupeWindowSeconds,
            launchRegisterTimeoutSeconds: connection.runtime.runtimeTuning.launchRegisterTimeoutSeconds
          },
          hubVersion: connection.runtime.hubVersion
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

    if (!expectsInvalidParams(vector)) {
      throw error;
    }

    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "sdk-invocation",
      operation,
      outcome: "error",
      actual: normalizeLocalInvalidParamsError(),
      error: null
    };
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

async function runSdkRpc(context) {
  const vector = context.vector;
  const request = ensureRecord(vector.request, "request");
  const method = ensureString(request.method, "request.method");
  const params = readJsonRpcParams(request);
  const client = await DevHubClient.fromRuntime({
    clientId: readVectorClientId(vector),
    dataDir: context.dataDir
  });

  try {
    const result = await dispatchSdkRpc(client, method, params);
    return {
      sdk: "typescript",
      vectorId: vector.id,
      phase: "rpc",
      outcome: "success",
      actual: {
        jsonrpc: "2.0",
        id: request.id,
        result: normalizeSdkRpcResult(method, result, vector.expectedResponse?.result)
      },
      error: null
    };
  } catch (error) {
    if (error instanceof DevHubRpcError) {
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "rpc",
        outcome: "success",
        actual: {
          jsonrpc: "2.0",
          id: request.id,
          error: normalizeRpcError(error)
        },
        error: null
      };
    }

    if (isExpectedLocalInvalidParamsError(vector, error)) {
      return {
        sdk: "typescript",
        vectorId: vector.id,
        phase: "rpc",
        outcome: "success",
        actual: {
          jsonrpc: "2.0",
          id: request.id,
          error: {
            code: -32602,
            message: "invalid_params"
          }
        },
        error: null
      };
    }

    throw error;
  } finally {
    await client.dispose();
  }
}

async function runHttp(context) {
  const vector = context.vector;
  const request = ensureRecord(vector.request, "request");
  const connection = await discoverRuntime(context.dataDir);
  const method = ensureString(request.method, "request.method").toUpperCase();
  const requestPath = typeof request.path === "string" && request.path.startsWith("/")
    ? request.path
    : "/rpc";
  const body = "body" in request && request.body !== null
    ? normalizeRawRequestBody(request.body)
    : undefined;
  const response = await fetch(`${connection.runtime.httpBaseUrl}${requestPath}`, {
    method,
    headers: request.headers ?? {},
    body
  });
  const bodyText = await response.text();
  const actual = {
    statusCode: response.status,
    headers: normalizeHttpHeaders(response.headers),
    bodyText
  };
  if (bodyText) {
    try {
      actual.bodyJson = JSON.parse(bodyText);
    } catch {
    }
  } else {
    actual.bodyJson = null;
  }
  return {
    sdk: "typescript",
    vectorId: vector.id,
    phase: "http",
    outcome: "success",
    actual,
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
        const launchId = tryResolveOptionalString(step, captures, index, "launchId");
        const registered = await client.registerInstance(
          instance,
          password,
          launchId === undefined ? undefined : { launchId }
        );
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
        const clientName = typeof step.client === "string" ? step.client : undefined;
        const client = clientName ? (eventClients.get(clientName) ?? httpClients.get(clientName)) : undefined;
        const result = client
          ? await client.getDefinition(buildDefinitionIdentityPayload(step, captures, index))
          : readRawResult(
            await sendRawRpc(
              rawRpcConnection,
              `sdk-events-get-definition-${index}`,
              "hub.apps.getDefinition",
              buildDefinitionIdentityParams(step, captures, index)
            ),
            `request.steps[${index}]`
          ).definition;
        if (step.captureAs !== undefined) {
          captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = client
            ? normalizeAppDefinition(result)
            : ensureRecord(result, `request.steps[${index}].captureAs`);
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
        const clientName = typeof step.client === "string" ? step.client : undefined;
        const client = clientName ? (eventClients.get(clientName) ?? httpClients.get(clientName)) : undefined;
        const definitions = client
          ? await client.listDefinitions({
            scope: null
          })
          : ensureArray(
            readRawResult(
              await sendRawRpc(
                rawRpcConnection,
                `sdk-events-list-definitions-${index}`,
                "hub.apps.listDefinitions",
                {
                  scope: null
                }
              ),
              `request.steps[${index}]`
            ).definitions,
            `request.steps[${index}].captureAs`
          );
        captures[ensureString(step.captureAs, `request.steps[${index}].captureAs`)] = client
          ? definitions.map((definition) => normalizeAppDefinition(definition))
          : definitions;
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
    displayName: ensureNonBlankStringValue(payload.displayName, "definition.displayName")
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
    definition.launch = {};
    if ("exePath" in launch && launch.exePath !== undefined) {
      definition.launch.exePath = ensureStringValue(launch.exePath, "definition.launch.exePath");
    }
    if ("args" in launch && launch.args !== undefined) {
      if (!Array.isArray(launch.args)) {
        throw new Error("definition.launch.args must be an array.");
      }
      definition.launch.args = launch.args.map((item, index) => ensureStringValue(
        item,
        `definition.launch.args[${index}]`
      ));
    }
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

function normalizeLocalInvalidParamsError() {
  return {
    code: -32602,
    message: "invalid_params"
  };
}

async function dispatchSdkRpc(client, method, params) {
  switch (method) {
    case "hub.ping":
      return await client.ping(params && "echo" in params ? params.echo : undefined);
    case "hub.getVersion":
      return await client.getHostVersion();
    case "hub.apps.listDefinitions":
      return await client.listDefinitions(params);
    case "hub.apps.getDefinition":
      return await client.getDefinition(params);
    case "hub.apps.validateDefinition":
      return await client.validateDefinition(ensureRecord(params.definition, "params.definition"));
    case "hub.apps.upsertDefinition":
      return await client.upsertDefinition(ensureRecord(params.definition, "params.definition"));
    case "hub.apps.deleteDefinition":
      await client.deleteDefinition(params);
      return undefined;
    case "hub.apps.registerInstance":
      return await client.registerInstance(
        ensureRecord(params.instance, "params.instance"),
        ensureString(params.password, "params.password"),
        "launchId" in params ? { launchId: ensureString(params.launchId, "params.launchId") } : undefined
      );
    case "hub.apps.heartbeat":
      return await client.heartbeat(
        ensureString(params.instanceId, "params.instanceId"),
        ensureString(params.instanceSessionToken, "params.instanceSessionToken")
      );
    case "hub.apps.unregisterInstance":
      await client.unregisterInstance(
        ensureString(params.instanceId, "params.instanceId"),
        ensureString(params.instanceSessionToken, "params.instanceSessionToken")
      );
      return undefined;
    case "hub.apps.listInstances":
      return await client.listInstances(params);
    case "hub.apps.getInstance":
      return await client.getInstance(ensureString(params.instanceId, "params.instanceId"));
    case "hub.apps.launch":
      return await client.launch(params);
    case "hub.invoke.poll":
      return await client.poll(params);
    case "hub.invoke.respond":
      await client.respond(params);
      return undefined;
    default:
      throw new Error(`不支持通过 SDK RPC 分发的方法：${method}`);
  }
}

function normalizeSdkRpcResult(method, result, expectedResult) {
  switch (method) {
    case "hub.ping":
      return normalizePingResult(result);
    case "hub.getVersion":
      return {
        ok: true,
        version: result
      };
    case "hub.apps.listDefinitions":
      return {
        ok: true,
        definitions: normalizeDefinitions(result, expectedResult?.definitions)
      };
    case "hub.apps.getDefinition":
      return {
        ok: true,
        definition: normalizeDefinition(result, expectedResult?.definition)
      };
    case "hub.apps.validateDefinition":
      return {
        ok: true,
        valid: result.valid,
        errors: result.errors
      };
    case "hub.apps.upsertDefinition":
      return {
        ok: true,
        definition: normalizeDefinition(result, expectedResult?.definition)
      };
    case "hub.apps.deleteDefinition":
    case "hub.apps.unregisterInstance":
    case "hub.invoke.respond":
      return { ok: true };
    case "hub.apps.registerInstance":
      return {
        ok: true,
        instance: normalizeInstance(result, expectedResult?.instance),
        instanceSessionToken: result.instanceSessionToken
      };
    case "hub.apps.heartbeat":
      return {
        ok: true,
        lastSeenUtc: normalizeDate(result)
      };
    case "hub.apps.listInstances":
      return {
        ok: true,
        instances: normalizeInstances(result, expectedResult?.instances)
      };
    case "hub.apps.getInstance":
      return {
        ok: true,
        instance: normalizeInstance(result, expectedResult?.instance)
      };
    case "hub.apps.launch":
      return normalizeLaunchResult(result);
    case "hub.invoke.poll":
      return normalizePollResult(result);
    default:
      throw new Error(`不支持归一化 SDK RPC 方法结果：${method}`);
  }
}

function normalizePingResult(result) {
  const normalized = {
    ok: true,
    serverTimeUtc: normalizeDate(result.serverTimeUtc)
  };
  if ("echo" in result) {
    normalized.echo = result.echo;
  }
  return normalized;
}

function normalizeDefinitions(definitions, expectedDefinitions) {
  const normalized = definitions.map((definition, index) => normalizeDefinition(definition, expectedDefinitions?.[index]));
  if (!Array.isArray(expectedDefinitions)) {
    return normalized;
  }

  return normalized.filter((definition) => expectedDefinitions.some((expected) => {
    return expected?.appId === definition.appId && expected?.scope === definition.scope;
  }));
}

function normalizeDefinition(definition, expectedDefinition) {
  const normalized = {
    appId: definition.appId,
    scope: definition.scope,
    displayName: definition.displayName
  };

  if (definition.description !== undefined) {
    normalized.description = definition.description;
  }

  const expectedCapabilities = expectedDefinition?.capabilities;
  if (expectedCapabilities !== undefined) {
    normalized.capabilities = {};
    for (const key of Object.keys(expectedCapabilities)) {
      normalized.capabilities[key] = definition.capabilities?.[key];
    }
  }

  const expectedLaunch = expectedDefinition?.launch;
  if (expectedLaunch !== undefined) {
    normalized.launch = {};
    for (const key of Object.keys(expectedLaunch)) {
      normalized.launch[key] = definition.launch?.[key];
    }
  }

  return normalized;
}

function normalizeInstances(instances, expectedInstances) {
  const normalized = instances.map((instance, index) => normalizeInstance(instance, expectedInstances?.[index]));
  if (!Array.isArray(expectedInstances)) {
    return normalized;
  }

  return normalized.filter((instance) => expectedInstances.some((expected) => expected?.instanceId === instance.instanceId));
}

function normalizeInstance(instance, expectedInstance) {
  const normalized = {
    instanceId: instance.instanceId,
    appId: instance.appId,
    scope: instance.scope,
    pid: instance.pid,
    registeredAtUtc: normalizeDate(instance.registeredAtUtc),
    lastSeenUtc: normalizeDate(instance.lastSeenUtc),
    invoke: {
      poll: instance.invoke.poll,
      respond: instance.invoke.respond
    }
  };

  if (expectedInstance?.meta !== undefined || instance.meta !== undefined) {
    normalized.meta = instance.meta;
  }

  return normalized;
}

function normalizeLaunchResult(result) {
  const normalized = {
    ok: true,
    status: result.status
  };
  if (result.pid !== undefined) {
    normalized.pid = result.pid;
  }
  if (result.launchId !== undefined) {
    normalized.launchId = result.launchId;
  }
  if (result.dedupeKey !== undefined) {
    normalized.dedupeKey = result.dedupeKey;
  }
  if (result.instanceId !== undefined) {
    normalized.instanceId = result.instanceId;
  }
  return normalized;
}

function normalizePollResult(result) {
  return {
    ok: true,
    serverTimeUtc: normalizeDate(result.serverTimeUtc),
    items: result.items.map(normalizeInvocation)
  };
}

function normalizeInvocation(invocation) {
  const normalized = {
    invocationId: invocation.invocationId,
    appId: invocation.appId,
    target: invocation.target,
    method: invocation.method,
    kind: invocation.kind,
    createdAtUtc: normalizeDate(invocation.createdAtUtc),
    caller: invocation.caller
  };
  if (invocation.args !== undefined) {
    normalized.args = invocation.args;
  }
  if (invocation.options !== undefined) {
    normalized.options = invocation.options;
  }
  if (invocation.delivery !== undefined) {
    normalized.delivery = invocation.delivery;
  }
  return normalized;
}

function normalizeRpcError(error) {
  const normalized = {
    code: error.code,
    message: error.message
  };
  if (error.data !== undefined) {
    normalized.data = error.data;
  }
  return normalized;
}

function normalizeDate(value) {
  return value instanceof Date ? value.toISOString() : value;
}

function expectsInvalidParams(vector) {
  const actual = vector?.expectedResponse?.actual;
  return actual?.code === -32602 && actual?.message === "invalid_params";
}

function isExpectedLocalInvalidParamsError(vector, error) {
  return expectedInvalidParamsFields(vector).some((field) => errorMessageIncludesField(error, field));
}

function expectedInvalidParamsFields(vector) {
  const expectedResponse = vector?.expectedResponse;
  const expectedError = expectedResponse?.error ?? expectedResponse?.actual;
  if (expectedError?.code !== -32602 || expectedError?.message !== "invalid_params") {
    return [];
  }

  const fields = new Set();
  collectRequestFieldNames(vector?.request?.params, fields, "");
  collectRequestFieldNames(vector?.request?.invokeRequest, fields, "");

  return [...fields].sort((left, right) => right.length - left.length);
}

function collectRequestFieldNames(value, fields, prefix) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return;
  }

  for (const [key, child] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key;
    fields.add(path);
    collectRequestFieldNames(child, fields, path);
  }
}

function errorMessageIncludesField(error, field) {
  if (!(error instanceof Error)) {
    return false;
  }

  if (!field) {
    return false;
  }

  const message = error.message.toLowerCase();
  const candidates = new Set([field.toLowerCase()]);
  const segments = field.split(".");
  for (const segment of segments) {
    candidates.add(segment.toLowerCase());
  }
  for (let index = 1; index < segments.length; index += 1) {
    candidates.add(segments.slice(index).join(".").toLowerCase());
  }

  return [...candidates].some((candidate) => candidate && message.includes(candidate));
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

function normalizeAppDefinition(definition) {
  const normalized = {
    appId: definition.appId,
    scope: definition.scope,
    displayName: definition.displayName
  };

  if (definition.description !== undefined) {
    normalized.description = definition.description;
  }

  if (definition.capabilities !== undefined) {
    const normalizedCapabilities = {};
    if (definition.capabilities.rpc !== true) {
      normalizedCapabilities.rpc = definition.capabilities.rpc;
    }
    if ("events" in definition.capabilities && definition.capabilities.events !== undefined) {
      normalizedCapabilities.events = definition.capabilities.events;
    }
    if (Object.keys(normalizedCapabilities).length > 0) {
      normalized.capabilities = normalizedCapabilities;
    }
  }

  if (definition.launch !== undefined) {
    normalized.launch = {};
    if ("exePath" in definition.launch && definition.launch.exePath !== undefined) {
      normalized.launch.exePath = definition.launch.exePath;
    }
    if ("args" in definition.launch && definition.launch.args !== undefined) {
      normalized.launch.args = definition.launch.args;
    }
    if ("argsTemplate" in definition.launch && definition.launch.argsTemplate !== undefined) {
      normalized.launch.argsTemplate = definition.launch.argsTemplate;
    }
    if ("workingDirectory" in definition.launch && definition.launch.workingDirectory !== undefined) {
      normalized.launch.workingDirectory = definition.launch.workingDirectory;
    }
    if ("dedupeKeyTemplate" in definition.launch && definition.launch.dedupeKeyTemplate !== undefined) {
      normalized.launch.dedupeKeyTemplate = definition.launch.dedupeKeyTemplate;
    }
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

function buildDefinitionIdentityPayload(step, captures, index) {
  return {
    appId: String(resolveCaptureValue(step, captures, index, "appId")),
    scope: String(resolveCaptureValue(step, captures, index, "scope"))
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

function shouldUseSdkRpc(vector) {
  const request = vector?.request;
  if (!request || typeof request !== "object" || Array.isArray(request)) {
    return false;
  }

  if (request.jsonrpc !== "2.0") {
    return false;
  }

  if (!("id" in request)) {
    return false;
  }

  if (!SDK_RPC_METHODS.has(request.method)) {
    return false;
  }

  const params = request.params;
  if (params !== undefined && (params === null || typeof params !== "object" || Array.isArray(params))) {
    return false;
  }

  return !isRawProtocolVector(vector);
}

function isRawProtocolVector(vector) {
  const tags = Array.isArray(vector?.tags) ? vector.tags : [];
  if (tags.some((tag) => tag === "transport")) {
    return true;
  }

  if (tags.some((tag) => tag === "auth") && vector?.expectedResponse?.error !== undefined) {
    return true;
  }

  const id = typeof vector?.id === "string" ? vector.id : "";
  return id.startsWith("errors.http.")
    || expectsStructuredHostInvalidParams(vector)
    || expectsHostDefinitionValidationFailure(vector);
}

function expectsStructuredHostInvalidParams(vector) {
  const expectedError = vector?.expectedResponse?.error;
  return expectedError?.code === -32602
    && expectedError?.message === "invalid_params"
    && expectedError?.data !== undefined;
}

function expectsHostDefinitionValidationFailure(vector) {
  return vector?.request?.method === "hub.apps.validateDefinition"
    && vector?.expectedResponse?.result?.valid === false;
}

function readVectorClientId(vector) {
  const headerValue = vector?.http?.headers?.["X-DevHub-ClientId"];
  if (typeof headerValue === "string" && headerValue.trim()) {
    return headerValue;
  }

  return "ConformanceSdkRpc";
}

function readJsonRpcParams(request) {
  if (!("params" in request) || request.params === undefined || request.params === null) {
    return {};
  }

  return ensureRecord(request.params, "request.params");
}

function normalizeRawRequestBody(request) {
  if (typeof request === "string") {
    return request;
  }

  return JSON.stringify(request);
}

function normalizeHttpHeaders(headers) {
  const normalized = {};
  for (const [key, value] of headers.entries()) {
    normalized[key.toLowerCase()] = value;
  }
  return normalized;
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

function tryResolveOptionalString(step, captures, index, fieldName) {
  const referenceField = `${fieldName}Ref`;
  if (!(fieldName in step) && !(referenceField in step)) {
    return undefined;
  }

  return ensureString(resolveCaptureValue(step, captures, index, fieldName), `request.steps[${index}].${fieldName}`);
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

function ensureNonBlankStringValue(value, pathLabel) {
  const parsed = ensureStringValue(value, pathLabel);
  if (!parsed.trim()) {
    throw new Error(`${pathLabel} 必须为非空白字符串。`);
  }
  return parsed;
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
      : kind === "raw.http"
        ? await runHttp(context)
        : kind === "sdk.notify" || kind === "sdk.request"
          ? await runInvocation(context)
          : kind === "sdk.events"
            ? await runEvents(context)
            : kind === "raw.ws" || vector.transport === "ws"
              ? await runWs(context)
              : shouldUseSdkRpc(vector)
                ? await runSdkRpc(context)
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
