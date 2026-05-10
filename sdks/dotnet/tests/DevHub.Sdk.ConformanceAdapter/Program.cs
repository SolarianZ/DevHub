using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk;
using DevHub.Sdk.Models;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var sdkRpcMethods = new HashSet<string>(StringComparer.Ordinal)
{
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
    "hub.invoke.notify",
    "hub.invoke.request",
    "hub.invoke.poll",
    "hub.invoke.respond"
};

if (args.Length != 1)
{
    WritePayload(new AdapterResult("dotnet", null, null, null, "error", null, new { message = "用法错误：需要 execution-context.json 路径。" }));
    return 0;
}

try
{
    using var contextDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[0], Encoding.UTF8));
    var root = contextDocument.RootElement;
    var vector = root.GetProperty("vector");
    var request = vector.GetProperty("request");
    var kind = request.ValueKind == JsonValueKind.Object
        ? ReadOptionalString(request, "kind")
        : null;
    var result = vector.TryGetProperty("expectedDiscovery", out _)
        ? await RunDiscoveryAsync(root, vector)
        : string.Equals(kind, "raw.http", StringComparison.Ordinal)
            ? await RunHttpAsync(root, vector, request)
            : string.Equals(kind, "sdk.notify", StringComparison.Ordinal) ||
              string.Equals(kind, "sdk.request", StringComparison.Ordinal)
                ? await RunInvocationAsync(root, vector, request)
                : string.Equals(kind, "sdk.events", StringComparison.Ordinal)
                    ? await RunEventsAsync(root, vector, request)
                    : string.Equals(kind, "raw.ws", StringComparison.Ordinal) ||
                      string.Equals(ReadString(vector, "transport"), "ws", StringComparison.Ordinal)
                        ? await RunWsAsync(root, vector, request)
                        : ShouldUseSdkRpc(vector, request)
                            ? await RunSdkRpcAsync(root, vector, request)
                        : await RunRpcAsync(root, vector, request);
    WritePayload(result);
}
catch (Exception exception)
{
    WritePayload(new AdapterResult("dotnet", null, null, null, "error", null, new { message = exception.Message }));
}

return 0;

async Task<AdapterResult> RunDiscoveryAsync(JsonElement context, JsonElement vector)
{
    var request = vector.GetProperty("request");
    var explicitDataDir = ReadOptionalString(request, "dataDir");
    var environmentDataDir = ReadOptionalString(context, "environmentDataDir");
    var originalDataDirEnv = Environment.GetEnvironmentVariable("DEVHUB_DATA_DIR");

    try
    {
        if (!string.IsNullOrWhiteSpace(environmentDataDir))
        {
            Environment.SetEnvironmentVariable("DEVHUB_DATA_DIR", environmentDataDir);
        }

        var resolver = new FileSystemDevHubRuntimeResolver();
        var connection = await resolver.ResolveAsync(new DevHubClientOptions
        {
            ClientId = ReadOptionalString(request, "clientId") ?? "ConformanceDiscovery",
            DataDir = string.IsNullOrWhiteSpace(explicitDataDir) ? null : explicitDataDir
        });

        var actual = new
        {
            runtimeDirectory = connection.RuntimeDirectory,
            token = connection.Token,
            runtime = new
            {
                protocolVersion = connection.Runtime.ProtocolVersion,
                pid = connection.Runtime.Pid,
                httpBaseUrl = connection.Runtime.HttpBaseUrl,
                wsUrl = connection.Runtime.WsUrl,
                tokenFile = connection.Runtime.TokenFile,
                startedAtUtc = connection.Runtime.StartedAtUtc.ToString("O"),
                runtimeTuning = new
                {
                    leaseSeconds = connection.Runtime.RuntimeTuning.LeaseSeconds,
                    onlineThresholdSeconds = connection.Runtime.RuntimeTuning.OnlineThresholdSeconds,
                    launchDedupeWindowSeconds = connection.Runtime.RuntimeTuning.LaunchDedupeWindowSeconds,
                    launchRegisterTimeoutSeconds = connection.Runtime.RuntimeTuning.LaunchRegisterTimeoutSeconds
                },
                hubVersion = connection.Runtime.HubVersion
            }
        };

        return new AdapterResult("dotnet", ReadString(vector, "id"), "discovery", null, "success", actual, null);
    }
    catch (Exception exception)
    {
        var reason = !string.IsNullOrWhiteSpace(explicitDataDir)
                     && string.Equals(Path.GetFileName(explicitDataDir), "runtime", StringComparison.OrdinalIgnoreCase)
            ? "runtime_subdirectory_rejected"
            : exception.Message.Contains("hub.json", StringComparison.Ordinal)
                ? "invalid_runtime"
                : "discovery_failed";
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "discovery",
            null,
            "error",
            new
            {
                reason,
                message = exception.Message
            },
            null);
    }
    finally
    {
        Environment.SetEnvironmentVariable("DEVHUB_DATA_DIR", originalDataDirEnv);
    }
}

async Task<AdapterResult> RunInvocationAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var operation = string.Equals(ReadString(request, "kind"), "sdk.notify", StringComparison.Ordinal)
        ? "notify"
        : "request";
    await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
    {
        ClientId = ReadOptionalString(request, "clientId") ?? "ConformanceInvocation",
        DataDir = ReadString(context, "dataDir")
    });

    try
    {
        var invokeRequest = BuildInvokeRequest(request.GetProperty("invokeRequest"));
        if (string.Equals(operation, "notify", StringComparison.Ordinal))
        {
            var result = await client.NotifyAsync(invokeRequest);
            return new AdapterResult(
                "dotnet",
                ReadString(vector, "id"),
                "sdk-invocation",
                operation,
                "success",
                new
                {
                    ok = result.Ok,
                    invocationId = result.InvocationId
                },
                null);
        }

        var requestResult = await client.RequestAsync(invokeRequest);
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "sdk-invocation",
            operation,
            "success",
            new
            {
                ok = requestResult.Ok,
                invocationId = requestResult.InvocationId,
                value = ConvertJsonElement(requestResult.Value)
            },
            null);
    }
    catch (DevHubRpcException exception)
    {
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "sdk-invocation",
            operation,
            "error",
            NormalizeInvocationError(exception),
            null);
    }
    catch (ArgumentException exception)
    {
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "sdk-invocation",
            operation,
            "error",
            NormalizeLocalInvalidParamsError(exception),
            null);
    }
    catch (InvalidOperationException exception)
    {
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "sdk-invocation",
            operation,
            "error",
            NormalizeLocalInvalidParamsError(exception),
            null);
    }
}

async Task<AdapterResult> RunSdkRpcAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var requestId = ReadRequiredString(request.GetProperty("id"), "request.id");
    var method = ReadString(request, "method");
    var parameters = ReadJsonRpcParams(request);

    await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
    {
        ClientId = ReadVectorClientId(vector),
        DataDir = ReadString(context, "dataDir")
    });

    try
    {
        var result = await DispatchSdkRpcAsync(client, method, parameters);
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "rpc",
            null,
            "success",
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["result"] = NormalizeSdkRpcResult(method, result, TryGetExpectedResult(vector))
            },
            null);
    }
    catch (DevHubRpcException exception)
    {
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "rpc",
            null,
            "success",
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["error"] = NormalizeRpcError(exception)
            },
            null);
    }
    catch (Exception exception) when (IsExpectedLocalInvalidParamsError(vector, exception))
    {
        return new AdapterResult(
            "dotnet",
            ReadString(vector, "id"),
            "rpc",
            null,
            "success",
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = -32602,
                    ["message"] = "invalid_params"
                }
            },
            null);
    }
}

async Task<object?> DispatchSdkRpcAsync(DevHubClient client, string method, JsonElement parameters)
{
    switch (method)
    {
        case "hub.ping":
            return await client.PingAsync(parameters.TryGetProperty("echo", out var echoElement)
                ? DeserializeToObject(echoElement)
                : null);
        case "hub.getVersion":
            return await client.GetHostVersionAsync();
        case "hub.apps.listDefinitions":
            return await client.ListDefinitionsAsync(BuildSdkListDefinitionsRequest(parameters));
        case "hub.apps.getDefinition":
            return await client.GetDefinitionAsync(ReadString(parameters, "appId"), ReadString(parameters, "scope"));
        case "hub.apps.validateDefinition":
            return await client.ValidateDefinitionAsync(BuildAppDefinition(parameters.GetProperty("definition")));
        case "hub.apps.upsertDefinition":
            return await client.UpsertDefinitionAsync(BuildAppDefinition(parameters.GetProperty("definition")));
        case "hub.apps.deleteDefinition":
            await client.DeleteDefinitionAsync(ReadString(parameters, "appId"), ReadString(parameters, "scope"));
            return null;
        case "hub.apps.registerInstance":
            return await client.RegisterInstanceAsync(
                BuildAppInstanceRegistration(parameters.GetProperty("instance")),
                ReadString(parameters, "password"),
                parameters.TryGetProperty("launchId", out var launchIdElement)
                    ? ReadRequiredString(launchIdElement, "params.launchId")
                    : null);
        case "hub.apps.heartbeat":
            return await client.HeartbeatAsync(
                ReadString(parameters, "instanceId"),
                ReadString(parameters, "instanceSessionToken"));
        case "hub.apps.unregisterInstance":
            await client.UnregisterInstanceAsync(
                ReadString(parameters, "instanceId"),
                ReadString(parameters, "instanceSessionToken"));
            return null;
        case "hub.apps.listInstances":
            return await client.ListInstancesAsync(BuildListInstancesRequest(parameters));
        case "hub.apps.getInstance":
            return await client.GetInstanceAsync(ReadString(parameters, "instanceId"));
        case "hub.apps.launch":
            return await client.LaunchAsync(BuildLaunchRequest(parameters));
        case "hub.invoke.notify":
            return await client.NotifyAsync(BuildInvokeRequest(parameters));
        case "hub.invoke.request":
            return await client.RequestAsync(BuildInvokeRequest(parameters));
        case "hub.invoke.poll":
            return await client.PollAsync(BuildPollRequest(parameters));
        case "hub.invoke.respond":
            await client.RespondAsync(BuildRespondRequest(parameters));
            return null;
        default:
            throw new InvalidOperationException($"不支持通过 SDK RPC 分发的方法：{method}");
    }
}

async Task<AdapterResult> RunEventsAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var dataDir = ReadString(context, "dataDir");
    var rawRpcConnection = await new FileSystemDevHubRuntimeResolver().ResolveAsync(new DevHubClientOptions
    {
        ClientId = "ConformanceRawDefinitionRpc",
        DataDir = dataDir
    });
    var steps = ReadRequiredArray(request, "steps");
    var eventClients = new Dictionary<string, DevHubEventsClient>(StringComparer.Ordinal);
    var eventEnumerators = new Dictionary<string, IAsyncEnumerator<DevHubEvent>>(StringComparer.Ordinal);
    var httpClients = new Dictionary<string, DevHubClient>(StringComparer.Ordinal);
    var captures = new Dictionary<string, object?>(StringComparer.Ordinal);
    var registeredInstances = new List<(string ClientName, string InstanceId, string InstanceSessionToken)>();

    try
    {
        for (var index = 0; index < steps.GetArrayLength(); index++)
        {
            var step = steps[index];
            var action = ReadString(step, "action");
            switch (action)
            {
                case "create_events_client":
                {
                    var clientName = ReadString(step, "client");
                    var clientId = ReadOptionalString(step, "clientId")
                        ?? ReadOptionalString(request, "clientId")
                        ?? $"ConformanceEvents-{clientName}";
                    eventClients[clientName] = await DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
                    {
                        ClientId = clientId,
                        DataDir = dataDir
                    });
                    break;
                }
                case "authenticate":
                {
                    var client = RequireValue(eventClients, ReadString(step, "client"), index, "events client");
                    await client.AuthenticateAsync();
                    break;
                }
                case "subscribe":
                {
                    var client = RequireValue(eventClients, ReadString(step, "client"), index, "events client");
                    var captureAs = ReadString(step, "captureAs");
                    var subscriptionId = await client.SubscribeAsync(ReadOptionalEventTypes(step, "types"));
                    captures[captureAs] = subscriptionId;
                    break;
                }
                case "unsubscribe":
                {
                    var client = RequireValue(eventClients, ReadString(step, "client"), index, "events client");
                    var subscriptionId = Convert.ToString(ResolveCaptureValue(step, captures, index, "subscriptionId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].subscriptionId 不能为空。");
                    await client.UnsubscribeAsync(subscriptionId);
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = new { ok = true };
                    }
                    break;
                }
                case "create_http_client":
                {
                    var clientName = ReadString(step, "client");
                    var clientId = ReadOptionalString(step, "clientId")
                        ?? ReadOptionalString(request, "clientId")
                        ?? $"ConformanceHttp-{clientName}";
                    httpClients[clientName] = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
                    {
                        ClientId = clientId,
                        DataDir = dataDir
                    });
                    break;
                }
                case "register_instance":
                {
                    var clientName = ReadString(step, "client");
                    var client = RequireValue(httpClients, clientName, index, "http client");
                    var instance = BuildAppInstanceRegistration(step.GetProperty("instance"));
                    var password = Convert.ToString(ResolveCaptureValue(step, captures, index, "password"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].password 不能为空。");
                    var launchId = TryResolveCapturedString(step, captures, index, "launchId");
                    var registered = await client.RegisterInstanceAsync(instance, password, launchId);
                    var instanceSessionToken = registered.InstanceSessionToken;
                    registeredInstances.Add((clientName, registered.Instance.InstanceId, instanceSessionToken));
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = instanceSessionToken;
                    }
                    break;
                }
                case "unregister_instance":
                {
                    var clientName = ReadString(step, "client");
                    var client = RequireValue(httpClients, clientName, index, "http client");
                    var instanceId = Convert.ToString(ResolveCaptureValue(step, captures, index, "instanceId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].instanceId 不能为空。");
                    var instanceSessionToken = ResolveInstanceSessionToken(step, captures, registeredInstances, index, clientName, instanceId);
                    await client.UnregisterInstanceAsync(instanceId, instanceSessionToken);
                    RemoveRegisteredInstance(registeredInstances, clientName, instanceId);
                    break;
                }
                case "validate_definition":
                {
                    object? validation;
                    if (TryGetHttpClient(httpClients, step, out var client))
                    {
                        validation = NormalizeDefinitionValidationResult(
                            await client.ValidateDefinitionAsync(BuildAppDefinition(step.GetProperty("definition"))));
                    }
                    else
                    {
                        var validationResponse = await CallRawRpcAsync(
                            rawRpcConnection,
                            $"sdk-events-validate-definition-{index}",
                            "hub.apps.validateDefinition",
                            new Dictionary<string, object?>
                            {
                                ["definition"] = DeserializeToObject(step.GetProperty("definition"))
                            });
                        validation = ConvertJsonElement(ReadRawResultOrThrow(validationResponse, $"request.steps[{index}].definition"));
                    }

                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = validation;
                    }
                    break;
                }
                case "upsert_definition":
                {
                    object? upsertedDefinition;
                    if (TryGetHttpClient(httpClients, step, out var client))
                    {
                        upsertedDefinition = NormalizeAppDefinition(
                            await client.UpsertDefinitionAsync(BuildAppDefinition(step.GetProperty("definition"))));
                    }
                    else
                    {
                        var upsertResponse = await CallRawRpcAsync(
                            rawRpcConnection,
                            $"sdk-events-upsert-definition-{index}",
                            "hub.apps.upsertDefinition",
                            new Dictionary<string, object?>
                            {
                                ["definition"] = DeserializeToObject(step.GetProperty("definition"))
                            });
                        var upsertResult = ReadRawResultOrThrow(upsertResponse, $"request.steps[{index}].definition");
                        upsertedDefinition = ConvertJsonElement(
                            EnsureJsonProperty(
                                upsertResult,
                                $"request.steps[{index}].captureAs",
                                "definition",
                                JsonValueKind.Object));
                    }

                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = upsertedDefinition;
                    }
                    break;
                }
                case "delete_definition":
                {
                    if (TryGetHttpClient(httpClients, step, out var client))
                    {
                        var (appId, scope) = BuildDefinitionIdentity(step, captures, index);
                        await client.DeleteDefinitionAsync(appId, scope);
                    }
                    else
                    {
                        var deleteResponse = await CallRawRpcAsync(
                            rawRpcConnection,
                            $"sdk-events-delete-definition-{index}",
                            "hub.apps.deleteDefinition",
                            BuildDefinitionIdentityParams(step, captures, index));
                        ReadRawResultOrThrow(deleteResponse, $"request.steps[{index}].appId");
                    }

                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = new { ok = true };
                    }
                    break;
                }
                case "get_definition":
                {
                    var (appId, scope) = BuildDefinitionIdentity(step, captures, index);
                    if (TryGetNamedClient(eventClients, httpClients, step, out var eventsClient, out var httpClient))
                    {
                        captures[ReadString(step, "captureAs")] = NormalizeAppDefinition(eventsClient is not null
                            ? await eventsClient.GetDefinitionAsync(appId, scope)
                            : await httpClient!.GetDefinitionAsync(appId, scope));
                    }
                    else
                    {
                        var definitionResponse = await CallRawRpcAsync(
                            rawRpcConnection,
                            $"sdk-events-get-definition-{index}",
                            "hub.apps.getDefinition",
                            BuildDefinitionIdentityParams(step, captures, index));
                        var definitionResult = ReadRawResultOrThrow(definitionResponse, $"request.steps[{index}].appId");
                        captures[ReadString(step, "captureAs")] = ConvertJsonElement(
                            EnsureJsonProperty(
                                definitionResult,
                                $"request.steps[{index}].captureAs",
                                "definition",
                                JsonValueKind.Object));
                    }

                    break;
                }
                case "get_instance":
                {
                    var clientName = ReadString(step, "client");
                    var captureAs = ReadString(step, "captureAs");
                    var instanceId = Convert.ToString(ResolveCaptureValue(step, captures, index, "instanceId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].instanceId 不能为空。");

                    if (eventClients.TryGetValue(clientName, out var eventsClient))
                    {
                        captures[captureAs] = NormalizeAppInstance(await eventsClient.GetInstanceAsync(instanceId));
                    }
                    else
                    {
                        var httpClient = RequireValue(httpClients, clientName, index, "http client");
                        captures[captureAs] = NormalizeAppInstance(await httpClient.GetInstanceAsync(instanceId));
                    }

                    break;
                }
                case "list_definitions":
                {
                    if (TryGetNamedClient(eventClients, httpClients, step, out var eventsClient, out var httpClient))
                    {
                        var requestPayload = BuildListDefinitionsRequest(step, captures, index);
                        var definitions = eventsClient is not null
                            ? await eventsClient.ListDefinitionsAsync(requestPayload)
                            : await httpClient!.ListDefinitionsAsync(requestPayload);
                        captures[ReadString(step, "captureAs")] = definitions.Select(NormalizeAppDefinition).ToArray();
                    }
                    else
                    {
                        var listResponse = await CallRawRpcAsync(
                            rawRpcConnection,
                            $"sdk-events-list-definitions-{index}",
                            "hub.apps.listDefinitions",
                            new Dictionary<string, object?>
                            {
                                ["appId"] = ResolveCaptureValue(step, captures, index, "appId"),
                                ["scope"] = ResolveCaptureValue(step, captures, index, "scope")
                            });
                        var listResult = ReadRawResultOrThrow(listResponse, $"request.steps[{index}].captureAs");
                        captures[ReadString(step, "captureAs")] = ConvertJsonElement(
                            EnsureJsonProperty(
                                listResult,
                                $"request.steps[{index}].captureAs",
                                "definitions",
                                JsonValueKind.Array));
                    }

                    break;
                }
                case "read_event":
                {
                    var clientName = ReadString(step, "client");
                    var captureAs = ReadString(step, "captureAs");
                    var enumerator = GetOrCreateEventEnumerator(eventClients, eventEnumerators, clientName, index);
                    var nextEvent = await ReadEventWithTimeoutAsync(enumerator, ReadTimeoutMs(step, index), allowTimeout: false);
                    if (nextEvent.Status == EventReadStatus.Closed)
                    {
                        throw new InvalidOperationException($"request.steps[{index}] 事件流已结束。");
                    }

                    captures[captureAs] = nextEvent.Event;
                    break;
                }
                case "expect_no_event":
                {
                    var clientName = ReadString(step, "client");
                    var captureAs = ReadString(step, "captureAs");
                    var enumerator = GetOrCreateEventEnumerator(eventClients, eventEnumerators, clientName, index);
                    var nextEvent = await ReadEventWithTimeoutAsync(enumerator, ReadTimeoutMs(step, index), allowTimeout: true);
                    captures[captureAs] = nextEvent.Status switch
                    {
                        EventReadStatus.Timeout => new { status = "timeout" },
                        EventReadStatus.Closed => new { status = "closed" },
                        _ => new Dictionary<string, object?> { ["status"] = "received", ["event"] = nextEvent.Event }
                    };
                    break;
                }
                case "close_events_client":
                {
                    var clientName = ReadString(step, "client");
                    if (eventEnumerators.Remove(clientName, out var enumerator))
                    {
                        await enumerator.DisposeAsync();
                    }

                    if (eventClients.Remove(clientName, out var client))
                    {
                        await client.DisposeAsync();
                    }
                    break;
                }
                case "dispose_http_client":
                {
                    var clientName = ReadString(step, "client");
                    if (httpClients.Remove(clientName, out var client))
                    {
                        await client.DisposeAsync();
                    }
                    break;
                }
                case "sleep":
                    await Task.Delay(ReadTimeoutMs(step, index));
                    break;
                default:
                    throw new InvalidOperationException($"request.steps[{index}].action 不支持：{action}");
            }
        }

        return new AdapterResult("dotnet", ReadString(vector, "id"), "sdk-events", null, "success", captures, null);
    }
    finally
    {
        for (var index = registeredInstances.Count - 1; index >= 0; index--)
        {
            var registered = registeredInstances[index];
            if (!httpClients.TryGetValue(registered.ClientName, out var client))
            {
                continue;
            }

            try
            {
                await client.UnregisterInstanceAsync(registered.InstanceId, registered.InstanceSessionToken);
            }
            catch
            {
            }
        }

        foreach (var enumerator in eventEnumerators.Values)
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch
            {
            }
        }

        foreach (var client in httpClients.Values)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
            }
        }

        foreach (var client in eventClients.Values)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}

async Task<AdapterResult> RunWsAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var resolver = new FileSystemDevHubRuntimeResolver();
    var connection = await resolver.ResolveAsync(new DevHubClientOptions
    {
        ClientId = ReadOptionalString(request, "clientId") ?? "ConformanceWsAdapter",
        DataDir = ReadString(context, "dataDir")
    });

    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(connection.WebSocketEndpoint, CancellationToken.None);

    var steps = ReadRequiredArray(request, "steps");
    var captures = new Dictionary<string, object?>(StringComparer.Ordinal);
    try
    {
        for (var index = 0; index < steps.GetArrayLength(); index++)
        {
            var step = steps[index];
            var action = ReadString(step, "action");
            switch (action)
            {
                case "send":
                    await SendTextAsync(socket, NormalizeRawRequestBody(step.GetProperty("message")));
                    break;
                case "receive":
                    captures[ReadString(step, "captureAs")] = await ReceiveWsPayloadAsync(socket, ReadTimeoutMs(step, index));
                    break;
                case "wait_closed":
                    captures[ReadString(step, "captureAs")] = new { closed = await WaitForSocketCloseAsync(socket, ReadTimeoutMs(step, index)) };
                    break;
                case "sleep":
                    await Task.Delay(ReadTimeoutMs(step, index));
                    break;
                default:
                    throw new InvalidOperationException($"request.steps[{index}].action 不支持：{action}");
            }
        }

        return new AdapterResult("dotnet", ReadString(vector, "id"), "ws", null, "success", captures, null);
    }
    finally
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None);
            }
            catch
            {
            }
        }
    }
}

async Task<AdapterResult> RunRpcAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var dataDir = ReadString(context, "dataDir");
    var resolver = new FileSystemDevHubRuntimeResolver();
    var connection = await resolver.ResolveAsync(new DevHubClientOptions
    {
        ClientId = "ConformanceHttpAdapter",
        DataDir = dataDir
    });

    using var client = new HttpClient();
    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, connection.RpcEndpoint);
    using var content = new StringContent(NormalizeRawRequestBody(request), Encoding.UTF8);

    if (vector.TryGetProperty("http", out var httpElement)
        && httpElement.TryGetProperty("headers", out var headersElement)
        && headersElement.ValueKind == JsonValueKind.Object)
    {
        foreach (var header in headersElement.EnumerateObject())
        {
            var value = header.Value.GetString() ?? string.Empty;
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }

            requestMessage.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    if (content.Headers.ContentType is null)
    {
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
    }

    requestMessage.Content = content;

    using var responseMessage = await client.SendAsync(requestMessage);
    var body = await responseMessage.Content.ReadAsStringAsync();
    using var responseDocument = JsonDocument.Parse(body);
    return new AdapterResult(
        "dotnet",
        ReadString(vector, "id"),
        "rpc",
        null,
        "success",
        responseDocument.RootElement.Clone(),
        null);
}

async Task<AdapterResult> RunHttpAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var dataDir = ReadString(context, "dataDir");
    var connection = await new FileSystemDevHubRuntimeResolver().ResolveAsync(new DevHubClientOptions
    {
        ClientId = ReadOptionalString(request, "clientId") ?? "ConformanceHttpAdapter",
        DataDir = dataDir
    });

    using var client = new HttpClient();
    var method = new HttpMethod(ReadString(request, "method").ToUpperInvariant());
    var path = ReadOptionalString(request, "path") ?? "/rpc";
    if (!path.StartsWith("/", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("request.path 必须以 / 开头。");
    }

    using var requestMessage = new HttpRequestMessage(method, new Uri(new Uri(connection.Runtime.HttpBaseUrl), path));
    if (request.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind != JsonValueKind.Null)
    {
        requestMessage.Content = new StringContent(NormalizeRawRequestBody(bodyElement), Encoding.UTF8);
    }

    if (request.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
    {
        foreach (var header in headersElement.EnumerateObject())
        {
            var value = header.Value.GetString() ?? string.Empty;
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                requestMessage.Content ??= new StringContent(string.Empty, Encoding.UTF8);
                requestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }

            requestMessage.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    using var responseMessage = await client.SendAsync(requestMessage);
    var body = await responseMessage.Content.ReadAsStringAsync();
    var headers = NormalizeHttpHeaders(responseMessage);
    var actual = new Dictionary<string, object?>
    {
        ["statusCode"] = (int)responseMessage.StatusCode,
        ["headers"] = headers,
        ["bodyText"] = body,
        ["bodyJson"] = TryParseJsonBody(body)
    };

    return new AdapterResult(
        "dotnet",
        ReadString(vector, "id"),
        "http",
        null,
        "success",
        actual,
        null);
}

async Task<JsonElement> CallRawRpcAsync(
    DevHubRuntimeConnectionInfo connection,
    string requestId,
    string method,
    object? paramsPayload)
{
    using var client = new HttpClient();
    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, connection.RpcEndpoint);
    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    requestMessage.Headers.TryAddWithoutValidation("X-DevHub-Protocol", "1");
    requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientId", "ConformanceRawDefinitionRpc");
    requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", "00000000-0000-0000-0000-000000000099");

    var payload = new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = requestId,
        ["method"] = method,
        ["params"] = paramsPayload ?? new Dictionary<string, object?>()
    };
    requestMessage.Content = new StringContent(JsonSerializer.Serialize(payload, jsonOptions), Encoding.UTF8, "application/json");

    using var response = await client.SendAsync(requestMessage);
    response.EnsureSuccessStatusCode();
    using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return responseDocument.RootElement.Clone();
}

JsonElement ReadRawResultOrThrow(JsonElement response, string location)
{
    if (response.TryGetProperty("error", out var errorElement))
    {
        throw new InvalidOperationException($"{location} 定义 RPC 返回错误：{errorElement.GetRawText()}");
    }

    var result = EnsureJsonProperty(response, location, "result", JsonValueKind.Object);
    if (!result.TryGetProperty("ok", out var okElement) || okElement.ValueKind is not JsonValueKind.True)
    {
        throw new InvalidOperationException($"{location} 定义 RPC 缺少 result.ok=true：{response.GetRawText()}");
    }

    return result;
}

JsonElement EnsureJsonProperty(JsonElement element, string location, string propertyName, JsonValueKind expectedKind)
{
    if (!element.TryGetProperty(propertyName, out var propertyValue))
    {
        throw new InvalidOperationException($"{location}.{propertyName} 不能为空。");
    }

    if (propertyValue.ValueKind != expectedKind)
    {
        throw new InvalidOperationException($"{location}.{propertyName} 类型非法。");
    }

    return propertyValue;
}

Dictionary<string, object?> BuildDefinitionIdentityParams(
    JsonElement step,
    IReadOnlyDictionary<string, object?> captures,
    int stepIndex)
{
    var (appId, scope) = BuildDefinitionIdentity(step, captures, stepIndex);

    return new Dictionary<string, object?>
    {
        ["appId"] = appId,
        ["scope"] = scope
    };
}

(string AppId, string Scope) BuildDefinitionIdentity(
    JsonElement step,
    IReadOnlyDictionary<string, object?> captures,
    int stepIndex)
{
    var appId = Convert.ToString(ResolveCaptureValue(step, captures, stepIndex, "appId"))
        ?? throw new InvalidOperationException($"request.steps[{stepIndex}].appId 不能为空。");
    var scope = Convert.ToString(ResolveCaptureValue(step, captures, stepIndex, "scope"))
        ?? throw new InvalidOperationException($"request.steps[{stepIndex}].scope 不能为空。");
    return (appId, scope);
}

bool TryGetHttpClient(
    IReadOnlyDictionary<string, DevHubClient> httpClients,
    JsonElement step,
    out DevHubClient client)
{
    client = null!;
    var clientName = ReadOptionalString(step, "client");
    return clientName is not null && httpClients.TryGetValue(clientName, out client!);
}

bool TryGetNamedClient(
    IReadOnlyDictionary<string, DevHubEventsClient> eventClients,
    IReadOnlyDictionary<string, DevHubClient> httpClients,
    JsonElement step,
    out DevHubEventsClient? eventsClient,
    out DevHubClient? httpClient)
{
    eventsClient = null;
    httpClient = null;
    var clientName = ReadOptionalString(step, "client");
    if (clientName is null)
    {
        return false;
    }

    if (eventClients.TryGetValue(clientName, out eventsClient))
    {
        return true;
    }

    return httpClients.TryGetValue(clientName, out httpClient);
}

AppDefinition BuildAppDefinition(JsonElement payload)
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("definition 必须为对象。");
    }

    return JsonSerializer.Deserialize<AppDefinition>(payload.GetRawText(), jsonOptions)
        ?? throw new InvalidOperationException("definition 不能为空。");
}

ListDefinitionsRequest BuildListDefinitionsRequest(
    JsonElement step,
    IReadOnlyDictionary<string, object?> captures,
    int stepIndex)
{
    return new ListDefinitionsRequest
    {
        AppId = TryResolveCapturedString(step, captures, stepIndex, "appId"),
        Scope = ResolveCaptureValue(step, captures, stepIndex, "scope") is { } scopeValue
            ? Convert.ToString(scopeValue)
            : null
    };
}

ListDefinitionsRequest BuildSdkListDefinitionsRequest(JsonElement parameters)
{
    return new ListDefinitionsRequest
    {
        AppId = ReadOptionalString(parameters, "appId"),
        Scope = parameters.TryGetProperty("scope", out var scopeElement)
            ? ReadOptionalStringValue(scopeElement, "params.scope")
            : null
    };
}

ListInstancesRequest BuildListInstancesRequest(JsonElement parameters)
{
    return new ListInstancesRequest
    {
        AppId = ReadOptionalString(parameters, "appId"),
        Scope = parameters.TryGetProperty("scope", out var scopeElement)
            ? ReadOptionalStringValue(scopeElement, "params.scope")
            : null,
        IncludeOffline = ReadOptionalBoolean(parameters, "includeOffline") ?? false
    };
}

LaunchRequest BuildLaunchRequest(JsonElement parameters)
{
    return new LaunchRequest
    {
        AppId = ReadString(parameters, "appId"),
        Scope = ReadString(parameters, "scope"),
        DedupeKey = ReadOptionalString(parameters, "dedupeKey"),
        WaitForRegisterMs = ReadOptionalInt32(parameters, "waitForRegisterMs")
    };
}

PollRequest BuildPollRequest(JsonElement parameters)
{
    return new PollRequest
    {
        InstanceId = ReadString(parameters, "instanceId"),
        InstanceSessionToken = ReadString(parameters, "instanceSessionToken"),
        MaxCount = ReadOptionalInt32(parameters, "maxCount"),
        WaitMs = ReadOptionalInt32(parameters, "waitMs")
    };
}

RespondRequest BuildRespondRequest(JsonElement parameters)
{
    var request = new RespondRequest
    {
        InstanceId = ReadString(parameters, "instanceId"),
        InstanceSessionToken = ReadString(parameters, "instanceSessionToken"),
        InvocationId = ReadString(parameters, "invocationId"),
        LeaseToken = ReadString(parameters, "leaseToken")
    };

    if (parameters.TryGetProperty("value", out var valueElement))
    {
        request.Value = DeserializeToObject(valueElement);
    }

    if (parameters.TryGetProperty("error", out var errorElement))
    {
        request.Error = BuildCalleeError(errorElement);
    }

    return request;
}

DevHubCalleeError BuildCalleeError(JsonElement payload)
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("params.error 必须为对象。");
    }

    return DevHubCalleeError.Create(
        ReadInt32(payload, "code"),
        ReadString(payload, "message"),
        payload.TryGetProperty("data", out var dataElement) ? DeserializeToObject(dataElement) : null);
}

object? NormalizeDefinitionValidationResult(DefinitionValidationResult result)
{
    return ConvertJsonElement(JsonSerializer.SerializeToElement(result, jsonOptions));
}

object? NormalizeSdkRpcResult(string method, object? result, JsonElement? expectedResult)
{
    switch (method)
    {
        case "hub.ping":
            return NormalizePingResult((PingResult)result!);
        case "hub.getVersion":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["version"] = result
            };
        case "hub.apps.listDefinitions":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["definitions"] = ((IReadOnlyList<AppDefinition>)result!).Select((definition, index) =>
                    NormalizeSdkAppDefinition(definition, TryGetExpectedArrayItem(expectedResult, "definitions", index))).ToArray()
            };
        case "hub.apps.getDefinition":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["definition"] = NormalizeSdkAppDefinition((AppDefinition)result!, TryGetExpectedProperty(expectedResult, "definition"))
            };
        case "hub.apps.validateDefinition":
            return NormalizeDefinitionValidationResult((DefinitionValidationResult)result!);
        case "hub.apps.upsertDefinition":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["definition"] = NormalizeSdkAppDefinition((AppDefinition)result!, TryGetExpectedProperty(expectedResult, "definition"))
            };
        case "hub.apps.deleteDefinition":
        case "hub.apps.unregisterInstance":
        case "hub.invoke.respond":
            return new Dictionary<string, object?> { ["ok"] = true };
        case "hub.apps.registerInstance":
            var registration = (RegisterInstanceResult)result!;
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["instance"] = NormalizeSdkAppInstance(registration.Instance, TryGetExpectedProperty(expectedResult, "instance")),
                ["instanceSessionToken"] = registration.InstanceSessionToken
            };
        case "hub.apps.heartbeat":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["lastSeenUtc"] = NormalizeDate((DateTimeOffset)result!)
            };
        case "hub.apps.listInstances":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["instances"] = ((IReadOnlyList<AppInstance>)result!).Select((instance, index) =>
                    NormalizeSdkAppInstance(instance, TryGetExpectedArrayItem(expectedResult, "instances", index))).ToArray()
            };
        case "hub.apps.getInstance":
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["instance"] = NormalizeSdkAppInstance((AppInstance)result!, TryGetExpectedProperty(expectedResult, "instance"))
            };
        case "hub.apps.launch":
            return NormalizeLaunchResult((LaunchResult)result!);
        case "hub.invoke.notify":
            var notify = (NotifyResult)result!;
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["invocationId"] = notify.InvocationId
            };
        case "hub.invoke.request":
            var request = (RequestResult)result!;
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["invocationId"] = request.InvocationId,
                ["value"] = ConvertJsonElement(request.Value)
            };
        case "hub.invoke.poll":
            return NormalizePollResult((PollResult)result!);
        default:
            throw new InvalidOperationException($"不支持归一化 SDK RPC 方法结果：{method}");
    }
}

object NormalizePingResult(PingResult result)
{
    var payload = new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["serverTimeUtc"] = NormalizeDate(result.ServerTimeUtc)
    };
    if (result.Echo is { } echo)
    {
        payload["echo"] = ConvertJsonElement(echo);
    }

    return payload;
}

object? NormalizeAppDefinition(AppDefinition definition)
{
    return ConvertJsonElement(JsonSerializer.SerializeToElement(definition, jsonOptions));
}

object NormalizeSdkAppDefinition(AppDefinition definition, JsonElement? expectedDefinition)
{
    var payload = new Dictionary<string, object?>
    {
        ["appId"] = definition.AppId,
        ["scope"] = definition.Scope,
        ["displayName"] = definition.DisplayName
    };

    if (definition.Description is not null)
    {
        payload["description"] = definition.Description;
    }

    if (TryGetExpectedProperty(expectedDefinition, "capabilities") is { } expectedCapabilities)
    {
        payload["capabilities"] = NormalizeExpectedObjectFields(definition.Capabilities, expectedCapabilities);
    }

    if (TryGetExpectedProperty(expectedDefinition, "launch") is { } expectedLaunch)
    {
        payload["launch"] = NormalizeExpectedObjectFields(definition.Launch, expectedLaunch);
    }

    return payload;
}

string ResolveInstanceSessionToken(
    JsonElement step,
    IReadOnlyDictionary<string, object?> captures,
    IReadOnlyList<(string ClientName, string InstanceId, string InstanceSessionToken)> registeredInstances,
    int stepIndex,
    string clientName,
    string instanceId)
{
    var explicitToken = TryResolveCapturedString(step, captures, stepIndex, "instanceSessionToken")
        ?? TryResolveCapturedString(step, captures, stepIndex, "password");
    if (!string.IsNullOrWhiteSpace(explicitToken))
    {
        return explicitToken;
    }

    for (var index = registeredInstances.Count - 1; index >= 0; index--)
    {
        var registered = registeredInstances[index];
        if (string.Equals(registered.ClientName, clientName, StringComparison.Ordinal) &&
            string.Equals(registered.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return registered.InstanceSessionToken;
        }
    }

    throw new InvalidOperationException($"request.steps[{stepIndex}].instanceSessionToken 不能为空。");
}

void RemoveRegisteredInstance(
    List<(string ClientName, string InstanceId, string InstanceSessionToken)> registeredInstances,
    string clientName,
    string instanceId)
{
    for (var index = registeredInstances.Count - 1; index >= 0; index--)
    {
        var registered = registeredInstances[index];
        if (string.Equals(registered.ClientName, clientName, StringComparison.Ordinal) &&
            string.Equals(registered.InstanceId, instanceId, StringComparison.Ordinal))
        {
            registeredInstances.RemoveAt(index);
            return;
        }
    }
}

InvokeRequest BuildInvokeRequest(JsonElement payload)
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("request.invokeRequest 必须为对象。");
    }

    var request = new InvokeRequest
    {
        AppId = ReadString(payload, "appId"),
        Method = ReadString(payload, "method")
    };

    if (payload.TryGetProperty("target", out var targetElement))
    {
        if (targetElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new InvalidOperationException("request.invokeRequest.target 必须为对象或 null。");
        }

        if (targetElement.ValueKind == JsonValueKind.Null)
        {
            request.Target = null;
        }
        else
        {
            var target = new InvocationTarget
            {
                InstanceId = ReadOptionalString(targetElement, "instanceId")
            };

            if (targetElement.TryGetProperty("scope", out _))
            {
                target.Scope = ReadOptionalString(targetElement, "scope")!;
            }

            request.Target = target;
        }
    }

    if (payload.TryGetProperty("args", out var argsElement))
    {
        request.Args = JsonSerializer.Deserialize<object>(argsElement.GetRawText(), jsonOptions);
    }

    if (payload.TryGetProperty("options", out var optionsElement))
    {
        if (optionsElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new InvalidOperationException("request.invokeRequest.options 必须为对象或 null。");
        }

        if (optionsElement.ValueKind == JsonValueKind.Null)
        {
            request.Options = null;
        }
        else
        {
            request.Options = new InvocationOptions
            {
                TtlMs = ReadOptionalInt32(optionsElement, "ttlMs"),
                WaitTimeoutMs = ReadOptionalInt32(optionsElement, "waitTimeoutMs"),
                QueueIfOffline = ReadOptionalBoolean(optionsElement, "queueIfOffline"),
                AutoLaunch = ReadOptionalBoolean(optionsElement, "autoLaunch")
            };
        }
    }

    return request;
}

AppInstanceRegistration BuildAppInstanceRegistration(JsonElement payload)
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("instance 必须为对象。");
    }

    var invokeElement = payload.GetProperty("invoke");
    if (invokeElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("instance.invoke 必须为对象。");
    }

    var instance = new AppInstanceRegistration
    {
        InstanceId = ReadString(payload, "instanceId"),
        AppId = ReadString(payload, "appId"),
        Pid = ReadInt32(payload, "pid"),
        Invoke = new InvokeCapability
        {
            Poll = ReadBoolean(invokeElement, "poll"),
            Respond = ReadBoolean(invokeElement, "respond")
        },
        Meta = payload.TryGetProperty("meta", out var metaElement)
            ? DeserializeToObject(metaElement)
            : null
    };

    if (payload.TryGetProperty("scope", out _))
    {
        instance.Scope = ReadOptionalString(payload, "scope")!;
    }

    return instance;
}

IAsyncEnumerator<DevHubEvent> GetOrCreateEventEnumerator(
    IDictionary<string, DevHubEventsClient> eventClients,
    IDictionary<string, IAsyncEnumerator<DevHubEvent>> eventEnumerators,
    string clientName,
    int stepIndex)
{
    if (eventEnumerators.TryGetValue(clientName, out var existing))
    {
        return existing;
    }

    var client = RequireValue(eventClients, clientName, stepIndex, "events client");
    var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
    eventEnumerators[clientName] = enumerator;
    return enumerator;
}

async Task<EventReadResult> ReadEventWithTimeoutAsync(IAsyncEnumerator<DevHubEvent> enumerator, int timeoutMs, bool allowTimeout)
{
    try
    {
        var hasEvent = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        if (!hasEvent)
        {
            return new EventReadResult(EventReadStatus.Closed, null);
        }

        return new EventReadResult(EventReadStatus.Received, NormalizeEvent(enumerator.Current));
    }
    catch (TimeoutException) when (allowTimeout)
    {
        return new EventReadResult(EventReadStatus.Timeout, null);
    }
}

object NormalizeEvent(DevHubEvent @event)
{
    return new Dictionary<string, object?>
    {
        ["subscriptionId"] = @event.SubscriptionId,
        ["type"] = @event.Type.Value,
        ["payload"] = ConvertJsonElement(@event.Payload)
    };
}

object NormalizeAppInstance(AppInstance instance)
{
    return ConvertJsonElement(JsonSerializer.SerializeToElement(instance, jsonOptions))!;
}

object NormalizeSdkAppInstance(AppInstance instance, JsonElement? expectedInstance)
{
    var payload = new Dictionary<string, object?>
    {
        ["instanceId"] = instance.InstanceId,
        ["appId"] = instance.AppId,
        ["scope"] = instance.Scope,
        ["pid"] = instance.Pid,
        ["registeredAtUtc"] = instance.RegisteredAtUtc is { } registeredAtUtc ? NormalizeDate(registeredAtUtc) : null,
        ["lastSeenUtc"] = instance.LastSeenUtc is { } lastSeenUtc ? NormalizeDate(lastSeenUtc) : null,
        ["invoke"] = instance.Invoke is null
            ? null
            : new Dictionary<string, object?>
            {
                ["poll"] = instance.Invoke.Poll,
                ["respond"] = instance.Invoke.Respond
            }
    };

    if (expectedInstance is { } expected && expected.TryGetProperty("meta", out _) || instance.Meta is not null)
    {
        payload["meta"] = ConvertJsonElement(instance.Meta);
    }

    return payload;
}

object NormalizeLaunchResult(LaunchResult result)
{
    var payload = new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["status"] = result.Status
    };
    if (result.Pid is not null)
    {
        payload["pid"] = result.Pid;
    }

    if (result.LaunchId is not null)
    {
        payload["launchId"] = result.LaunchId;
    }

    if (result.DedupeKey is not null)
    {
        payload["dedupeKey"] = result.DedupeKey;
    }

    if (result.InstanceId is not null)
    {
        payload["instanceId"] = result.InstanceId;
    }

    return payload;
}

object NormalizePollResult(PollResult result)
{
    return new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["serverTimeUtc"] = NormalizeDate(result.ServerTimeUtc),
        ["items"] = result.Items.Select(NormalizeInvocation).ToArray()
    };
}

object NormalizeInvocation(Invocation invocation)
{
    var payload = new Dictionary<string, object?>
    {
        ["invocationId"] = invocation.InvocationId,
        ["appId"] = invocation.AppId,
        ["target"] = ConvertJsonElement(JsonSerializer.SerializeToElement(invocation.Target, jsonOptions)),
        ["method"] = invocation.Method,
        ["kind"] = JsonSerializer.SerializeToElement(invocation.Kind, jsonOptions).GetString(),
        ["createdAtUtc"] = NormalizeDate(invocation.CreatedAtUtc),
        ["caller"] = ConvertJsonElement(JsonSerializer.SerializeToElement(invocation.Caller, jsonOptions))
    };

    if (invocation.Args is not null)
    {
        payload["args"] = ConvertJsonElement(invocation.Args);
    }

    if (invocation.Options is not null)
    {
        payload["options"] = ConvertJsonElement(JsonSerializer.SerializeToElement(invocation.Options, jsonOptions));
    }

    if (invocation.Delivery is not null)
    {
        payload["delivery"] = ConvertJsonElement(JsonSerializer.SerializeToElement(invocation.Delivery, jsonOptions));
    }

    return payload;
}

object NormalizeRpcError(DevHubRpcException exception)
{
    var payload = new Dictionary<string, object?>
    {
        ["code"] = exception.Code,
        ["message"] = exception.Message
    };
    if (exception.ErrorData is { } data)
    {
        payload["data"] = ConvertJsonElement(data);
    }

    return payload;
}

object? NormalizeExpectedObjectFields<T>(T value, JsonElement expected)
{
    var source = JsonSerializer.SerializeToElement(value, jsonOptions);
    if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        return null;
    }

    var payload = new Dictionary<string, object?>();
    foreach (var property in expected.EnumerateObject())
    {
        payload[property.Name] = source.TryGetProperty(property.Name, out var actual)
            ? ConvertJsonElement(actual)
            : null;
    }

    return payload;
}

string NormalizeDate(DateTimeOffset value)
{
    return value.ToUniversalTime().ToString("O");
}

async Task SendTextAsync(ClientWebSocket socket, string payload)
{
    var bytes = Encoding.UTF8.GetBytes(payload);
    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
}

async Task<object?> ReceiveWsPayloadAsync(ClientWebSocket socket, int timeoutMs)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
    var buffer = new byte[4096];
    using var stream = new MemoryStream();

    while (true)
    {
        WebSocketReceiveResult result;
        try
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("等待 WebSocket 消息超时。");
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
                }
                catch
                {
                }
            }

            return new Dictionary<string, object?> { ["closed"] = true };
        }

        stream.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            break;
        }
    }

    return ParseWsPayload(Encoding.UTF8.GetString(stream.ToArray()));
}

async Task<bool> WaitForSocketCloseAsync(ClientWebSocket socket, int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (socket.State is WebSocketState.Closed or WebSocketState.Aborted or WebSocketState.None)
        {
            return true;
        }

        if (socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
            }
            catch
            {
            }

            return true;
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var remainingMs = (int)Math.Min(250, Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds));
            if (remainingMs <= 0)
            {
                break;
            }

            try
            {
                await ReceiveWsPayloadAsync(socket, remainingMs);
            }
            catch (TimeoutException)
            {
            }
            catch (WebSocketException)
            {
                return true;
            }
        }
        else
        {
            await Task.Delay(50);
        }
    }

    return socket.State is WebSocketState.Closed or WebSocketState.Aborted or WebSocketState.CloseReceived;
}

IEnumerable<DevHubEventType>? ReadOptionalEventTypes(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    if (property.ValueKind == JsonValueKind.Null)
    {
        return null;
    }

    if (property.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException($"{propertyName} 必须为数组或 null。");
    }

    var items = new List<DevHubEventType>();
    foreach (var item in property.EnumerateArray())
    {
        items.Add(DevHubEventType.Parse(ReadRequiredString(item, propertyName)));
    }

    return items;
}

object? ResolveCaptureValue(JsonElement step, IReadOnlyDictionary<string, object?> captures, int stepIndex, string fieldName)
{
    var referenceFieldName = $"{fieldName}Ref";
    if (step.TryGetProperty(referenceFieldName, out var referenceElement))
    {
        var referenceKey = ReadRequiredString(referenceElement, $"request.steps[{stepIndex}].{referenceFieldName}");
        if (!captures.TryGetValue(referenceKey, out var captured))
        {
            throw new InvalidOperationException($"request.steps[{stepIndex}].{referenceFieldName} 引用不存在：{referenceKey}");
        }

        return captured;
    }

    return step.TryGetProperty(fieldName, out var fieldElement)
        ? DeserializeToObject(fieldElement)
        : null;
}

string? TryResolveCapturedString(JsonElement step, IReadOnlyDictionary<string, object?> captures, int stepIndex, string fieldName)
{
    var value = ResolveCaptureValue(step, captures, stepIndex, fieldName);
    return value is null ? null : Convert.ToString(value);
}

object? ParseWsPayload(string payload)
{
    try
    {
        using var document = JsonDocument.Parse(payload);
        return ConvertJsonElement(document.RootElement);
    }
    catch (JsonException)
    {
        return payload;
    }
}

string NormalizeRawRequestBody(JsonElement element)
{
    return element.ValueKind == JsonValueKind.String
        ? element.GetString() ?? string.Empty
        : element.GetRawText();
}

object? DeserializeToObject(JsonElement element)
{
    return element.ValueKind == JsonValueKind.Null
        ? null
        : JsonSerializer.Deserialize<object>(element.GetRawText(), jsonOptions);
}

Dictionary<string, string> NormalizeHttpHeaders(HttpResponseMessage response)
{
    var headers = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var header in response.Headers)
    {
        headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
    }

    foreach (var header in response.Content.Headers)
    {
        headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
    }

    return headers;
}

object? TryParseJsonBody(string body)
{
    if (string.IsNullOrEmpty(body))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(body);
        return ConvertJsonElement(document.RootElement);
    }
    catch (JsonException)
    {
        return null;
    }
}

bool ShouldUseSdkRpc(JsonElement vector, JsonElement request)
{
    if (request.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    if (!request.TryGetProperty("jsonrpc", out var jsonrpcElement) ||
        jsonrpcElement.ValueKind != JsonValueKind.String ||
        !string.Equals(jsonrpcElement.GetString(), "2.0", StringComparison.Ordinal))
    {
        return false;
    }

    if (!request.TryGetProperty("id", out _))
    {
        return false;
    }

    if (!request.TryGetProperty("method", out var methodElement) ||
        methodElement.ValueKind != JsonValueKind.String ||
        methodElement.GetString() is not { } method ||
        !sdkRpcMethods.Contains(method))
    {
        return false;
    }

    if (request.TryGetProperty("params", out var paramsElement) &&
        paramsElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined)
    {
        return false;
    }

    return !IsRawProtocolVector(vector);
}

bool IsRawProtocolVector(JsonElement vector)
{
    if (VectorTagsContain(vector, "transport"))
    {
        return true;
    }

    if (VectorTagsContain(vector, "auth") &&
        vector.TryGetProperty("expectedResponse", out var authExpectedResponse) &&
        authExpectedResponse.ValueKind == JsonValueKind.Object &&
        authExpectedResponse.TryGetProperty("error", out _))
    {
        return true;
    }

    var id = ReadOptionalString(vector, "id") ?? string.Empty;
    return id.StartsWith("errors.http.", StringComparison.Ordinal) ||
           ExpectsStructuredHostInvalidParams(vector) ||
           ExpectsHostDefinitionValidationFailure(vector);
}

bool VectorTagsContain(JsonElement vector, string expectedTag)
{
    if (!vector.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
    {
        return false;
    }

    foreach (var tag in tags.EnumerateArray())
    {
        if (tag.ValueKind == JsonValueKind.String &&
            string.Equals(tag.GetString(), expectedTag, StringComparison.Ordinal))
        {
            return true;
        }
    }

    return false;
}

bool ExpectsStructuredHostInvalidParams(JsonElement vector)
{
    return TryGetExpectedError(vector, out var expectedError) &&
           expectedError.TryGetProperty("code", out var codeElement) &&
           codeElement.ValueKind == JsonValueKind.Number &&
           codeElement.TryGetInt32(out var code) &&
           code == -32602 &&
           expectedError.TryGetProperty("message", out var messageElement) &&
           string.Equals(messageElement.GetString(), "invalid_params", StringComparison.Ordinal) &&
           expectedError.TryGetProperty("data", out _);
}

bool ExpectsHostDefinitionValidationFailure(JsonElement vector)
{
    if (!vector.TryGetProperty("request", out var request) ||
        request.ValueKind != JsonValueKind.Object ||
        !request.TryGetProperty("method", out var methodElement) ||
        !string.Equals(methodElement.GetString(), "hub.apps.validateDefinition", StringComparison.Ordinal) ||
        !TryGetExpectedResultElement(vector, out var expectedResult) ||
        !expectedResult.TryGetProperty("valid", out var validElement))
    {
        return false;
    }

    return validElement.ValueKind == JsonValueKind.False;
}

bool IsExpectedLocalInvalidParamsError(JsonElement vector, Exception exception)
{
    return exception is ArgumentException or InvalidOperationException &&
           ExpectedInvalidParamsFields(vector).Any(field => ErrorMessageIncludesField(exception, field));
}

IEnumerable<string> ExpectedInvalidParamsFields(JsonElement vector)
{
    if (!TryGetExpectedError(vector, out var expectedError) ||
        !expectedError.TryGetProperty("code", out var codeElement) ||
        !codeElement.TryGetInt32(out var code) ||
        code != -32602 ||
        !expectedError.TryGetProperty("message", out var messageElement) ||
        !string.Equals(messageElement.GetString(), "invalid_params", StringComparison.Ordinal))
    {
        return Array.Empty<string>();
    }

    var fields = new HashSet<string>(StringComparer.Ordinal);
    if (vector.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
    {
        if (request.TryGetProperty("params", out var parameters))
        {
            CollectRequestFieldNames(parameters, fields, string.Empty);
        }

        if (request.TryGetProperty("invokeRequest", out var invokeRequest))
        {
            CollectRequestFieldNames(invokeRequest, fields, string.Empty);
        }
    }

    return fields
        .OrderByDescending(static field => field.Length)
        .ThenBy(static field => field, StringComparer.Ordinal);
}

void CollectRequestFieldNames(JsonElement value, ISet<string> fields, string prefix)
{
    if (value.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    foreach (var property in value.EnumerateObject())
    {
        var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
        fields.Add(path);
        CollectRequestFieldNames(property.Value, fields, path);
    }
}

bool ErrorMessageIncludesField(Exception exception, string field)
{
    if (string.IsNullOrWhiteSpace(field))
    {
        return false;
    }

    var message = exception.Message.ToLowerInvariant();
    var candidates = new HashSet<string>(StringComparer.Ordinal) { field.ToLowerInvariant() };
    var segments = field.Split('.', StringSplitOptions.RemoveEmptyEntries);
    foreach (var segment in segments)
    {
        candidates.Add(segment.ToLowerInvariant());
    }

    for (var index = 1; index < segments.Length; index++)
    {
        candidates.Add(string.Join('.', segments.Skip(index)).ToLowerInvariant());
    }

    if (exception is ArgumentException argumentException && !string.IsNullOrWhiteSpace(argumentException.ParamName))
    {
        candidates.Add(argumentException.ParamName.ToLowerInvariant());
    }

    return candidates.Any(candidate => candidate.Length > 0 && message.Contains(candidate, StringComparison.Ordinal));
}

JsonElement ReadJsonRpcParams(JsonElement request)
{
    if (!request.TryGetProperty("params", out var parameters) || parameters.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    if (parameters.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("request.params 必须为对象。");
    }

    return parameters.Clone();
}

string ReadVectorClientId(JsonElement vector)
{
    if (vector.TryGetProperty("http", out var http) &&
        http.ValueKind == JsonValueKind.Object &&
        http.TryGetProperty("headers", out var headers) &&
        headers.ValueKind == JsonValueKind.Object &&
        headers.TryGetProperty("X-DevHub-ClientId", out var clientIdElement) &&
        clientIdElement.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(clientIdElement.GetString()))
    {
        return clientIdElement.GetString()!;
    }

    return "ConformanceSdkRpc";
}

JsonElement? TryGetExpectedResult(JsonElement vector)
{
    return TryGetExpectedResultElement(vector, out var result) ? result : null;
}

bool TryGetExpectedResultElement(JsonElement vector, out JsonElement result)
{
    if (vector.TryGetProperty("expectedResponse", out var expectedResponse) &&
        expectedResponse.ValueKind == JsonValueKind.Object &&
        expectedResponse.TryGetProperty("result", out var resultElement))
    {
        result = resultElement;
        return true;
    }

    result = default;
    return false;
}

bool TryGetExpectedError(JsonElement vector, out JsonElement error)
{
    if (vector.TryGetProperty("expectedResponse", out var expectedResponse) &&
        expectedResponse.ValueKind == JsonValueKind.Object)
    {
        if (expectedResponse.TryGetProperty("error", out var errorElement))
        {
            error = errorElement;
            return true;
        }

        if (expectedResponse.TryGetProperty("actual", out var actualElement) &&
            actualElement.ValueKind == JsonValueKind.Object)
        {
            error = actualElement;
            return true;
        }
    }

    error = default;
    return false;
}

JsonElement? TryGetExpectedProperty(JsonElement? element, string propertyName)
{
    if (element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(propertyName, out var property))
    {
        return property;
    }

    return null;
}

JsonElement? TryGetExpectedArrayItem(JsonElement? element, string propertyName, int index)
{
    var property = TryGetExpectedProperty(element, propertyName);
    if (property is not { ValueKind: JsonValueKind.Array } array || index < 0 || index >= array.GetArrayLength())
    {
        return null;
    }

    return array[index];
}

object NormalizeInvocationError(DevHubRpcException exception)
{
    var payload = new Dictionary<string, object?>
    {
        ["code"] = exception.Code,
        ["message"] = exception.Message
    };

    if (!string.IsNullOrWhiteSpace(exception.Reason))
    {
        payload["reason"] = exception.Reason;
    }

    if (!string.IsNullOrWhiteSpace(exception.InvocationId))
    {
        payload["invocationId"] = exception.InvocationId;
    }

    if (exception.TryGetDataProperty("calleeError", out var calleeErrorElement) && calleeErrorElement.ValueKind == JsonValueKind.Object)
    {
        var calleePayload = new Dictionary<string, object?>
        {
            ["code"] = calleeErrorElement.GetProperty("code").GetInt32(),
            ["message"] = calleeErrorElement.GetProperty("message").GetString()
        };
        if (calleeErrorElement.TryGetProperty("data", out var dataElement))
        {
            calleePayload["data"] = ConvertJsonElement(dataElement);
        }

        payload["calleeError"] = calleePayload;
    }

    return payload;
}

object NormalizeLocalInvalidParamsError(Exception exception)
{
    var payload = new Dictionary<string, object?>
    {
        ["code"] = -32602,
        ["message"] = "invalid_params",
        ["source"] = "sdk_local_validation",
        ["exceptionType"] = exception.GetType().Name
    };

    if (exception is ArgumentException argumentException && !string.IsNullOrWhiteSpace(argumentException.ParamName))
    {
        payload["paramName"] = argumentException.ParamName;
    }

    return payload;
}

object? ConvertJsonElement(JsonElement? element)
{
    if (element is null)
    {
        return null;
    }

    return JsonSerializer.Deserialize<object>(element.Value.GetRawText(), jsonOptions);
}

JsonElement ReadRequiredArray(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException($"{propertyName} 必须为数组。");
    }

    return property;
}

T RequireValue<T>(IDictionary<string, T> values, string key, int stepIndex, string label) where T : class
{
    if (!values.TryGetValue(key, out var value))
    {
        throw new InvalidOperationException($"request.steps[{stepIndex}] 未找到 {label}：{key}");
    }

    return value;
}

string ReadString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        throw new InvalidOperationException($"{propertyName} 不能为空。");
    }

    return ReadRequiredString(property, propertyName);
}

string ReadRequiredString(JsonElement element, string propertyName)
{
    return element.ValueKind == JsonValueKind.String
        ? element.GetString() ?? throw new InvalidOperationException($"{propertyName} 不能为空。")
        : throw new InvalidOperationException($"{propertyName} 类型非法。");
}

int ReadInt32(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property) ||
        property.ValueKind != JsonValueKind.Number ||
        !property.TryGetInt32(out var value))
    {
        throw new InvalidOperationException($"{propertyName} 类型非法。");
    }

    return value;
}

bool ReadBoolean(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        throw new InvalidOperationException($"{propertyName} 类型非法。");
    }

    return property.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException($"{propertyName} 类型非法。")
    };
}

string? ReadOptionalString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    return property.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => property.GetString(),
        _ => throw new InvalidOperationException($"{propertyName} 类型非法。")
    };
}

string? ReadOptionalStringValue(JsonElement element, string propertyName)
{
    return element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        _ => throw new InvalidOperationException($"{propertyName} 类型非法。")
    };
}

int? ReadOptionalInt32(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    return property.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when property.TryGetInt32(out var value) => value,
        _ => throw new InvalidOperationException($"{propertyName} 类型非法。")
    };
}

bool? ReadOptionalBoolean(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    return property.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException($"{propertyName} 类型非法。")
    };
}

int ReadTimeoutMs(JsonElement step, int stepIndex)
{
    if (step.TryGetProperty("timeoutMs", out var timeoutElement))
    {
        return ReadNonNegativeInt32(timeoutElement, $"request.steps[{stepIndex}].timeoutMs");
    }

    if (step.TryGetProperty("waitMs", out var waitElement))
    {
        return ReadNonNegativeInt32(waitElement, $"request.steps[{stepIndex}].waitMs");
    }

    return 1000;
}

int ReadNonNegativeInt32(JsonElement element, string propertyName)
{
    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
    {
        throw new InvalidOperationException($"{propertyName} 必须为非负整数。");
    }

    return value;
}

void WritePayload(AdapterResult payload)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
}

internal sealed record AdapterResult(
    string Sdk,
    string? VectorId,
    string? Phase,
    string? Operation,
    string Outcome,
    object? Actual,
    object? Error);

internal sealed record EventReadResult(EventReadStatus Status, object? Event);

internal enum EventReadStatus
{
    Received,
    Timeout,
    Closed
}
