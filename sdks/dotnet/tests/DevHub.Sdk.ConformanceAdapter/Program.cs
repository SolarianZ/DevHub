using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var payloadJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

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
        : string.Equals(kind, "sdk.notify", StringComparison.Ordinal) ||
          string.Equals(kind, "sdk.request", StringComparison.Ordinal)
            ? await RunInvocationAsync(root, vector, request)
            : string.Equals(kind, "sdk.events", StringComparison.Ordinal)
                ? await RunEventsAsync(root, vector, request)
                : string.Equals(kind, "raw.http", StringComparison.Ordinal)
                    ? await RunHttpAsync(root, vector, request)
                : string.Equals(kind, "raw.ws", StringComparison.Ordinal) ||
                  string.Equals(ReadString(vector, "transport"), "ws", StringComparison.Ordinal)
                    ? await RunWsAsync(root, vector, request)
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
                tokenFile = connection.Runtime.TokenFile
                ,
                startedAtUtc = connection.Runtime.StartedAtUtc,
                runtimeTuning = connection.Runtime.RuntimeTuning,
                hubVersion = connection.Runtime.HubVersion
            }
        };

        return new AdapterResult("dotnet", ReadString(vector, "id"), "discovery", null, "success", actual, null);
    }
    catch (Exception exception)
    {
        var reason = ClassifyDiscoveryFailure(explicitDataDir, exception);
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
    var vectorId = ReadString(vector, "id");
    var operation = string.Equals(ReadString(request, "kind"), "sdk.notify", StringComparison.Ordinal)
        ? "notify"
        : "request";
    try
    {
        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = ReadOptionalString(request, "clientId") ?? "ConformanceInvocation",
            DataDir = ReadString(context, "dataDir")
        });

        var invokeRequest = BuildInvokeRequest(request.GetProperty("invokeRequest"));
        if (string.Equals(operation, "notify", StringComparison.Ordinal))
        {
            var result = await client.NotifyAsync(invokeRequest);
            return new AdapterResult(
                "dotnet",
                vectorId,
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
            vectorId,
            "sdk-invocation",
            operation,
            "success",
            new
            {
                ok = requestResult.Ok,
                invocationId = requestResult.InvocationId,
                value = ConvertJToken(requestResult.Value)
            },
            null);
    }
    catch (DevHubRpcException exception)
    {
        return new AdapterResult(
            "dotnet",
            vectorId,
            "sdk-invocation",
            operation,
            "error",
            NormalizeInvocationError(exception),
            null);
    }
    catch (ArgumentException exception)
    {
        return CreateLocalValidationError(vectorId, operation, exception);
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
                    var registered = await client.RegisterInstanceAsync(instance, password);
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
                    var validationResponse = await CallRawRpcAsync(
                        rawRpcConnection,
                        $"sdk-events-validate-definition-{index}",
                        "hub.apps.validateDefinition",
                        new Dictionary<string, object?>
                        {
                            ["definition"] = DeserializeToObject(step.GetProperty("definition"))
                        });
                    var validation = ReadRawResultOrThrow(validationResponse, $"request.steps[{index}].definition");
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] =
                            ConvertJsonElement(validation);
                    }
                    break;
                }
                case "upsert_definition":
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
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] =
                            ConvertJsonElement(
                                EnsureJsonProperty(
                                    upsertResult,
                                    $"request.steps[{index}].captureAs",
                                    "definition",
                                    JsonValueKind.Object));
                    }
                    break;
                }
                case "delete_definition":
                {
                    var deleteResponse = await CallRawRpcAsync(
                        rawRpcConnection,
                        $"sdk-events-delete-definition-{index}",
                        "hub.apps.deleteDefinition",
                        BuildDefinitionIdentityParams(step, captures, index));
                    ReadRawResultOrThrow(deleteResponse, $"request.steps[{index}].appId");
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = new { ok = true };
                    }
                    break;
                }
                case "get_definition":
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
                    var listResponse = await CallRawRpcAsync(
                        rawRpcConnection,
                        $"sdk-events-list-definitions-{index}",
                        "hub.apps.listDefinitions",
                        new Dictionary<string, object?>());
                    var listResult = ReadRawResultOrThrow(listResponse, $"request.steps[{index}].captureAs");
                    captures[ReadString(step, "captureAs")] = ConvertJsonElement(
                        EnsureJsonProperty(
                            listResult,
                            $"request.steps[{index}].captureAs",
                            "definitions",
                            JsonValueKind.Array));
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
    var resolver = new FileSystemDevHubRuntimeResolver();
    var connection = await resolver.ResolveAsync(new DevHubClientOptions
    {
        ClientId = "ConformanceRawHttpAdapter",
        DataDir = dataDir
    });

    var method = new HttpMethod(ReadString(request, "method"));
    var requestUri = new Uri(new Uri(connection.Runtime.HttpBaseUrl, UriKind.Absolute), ReadString(request, "path"));

    using var client = new HttpClient();
    using var requestMessage = new HttpRequestMessage(method, requestUri);

    StringContent? content = null;
    if (request.TryGetProperty("body", out var bodyElement))
    {
        content = new StringContent(NormalizeRawRequestBody(bodyElement), Encoding.UTF8);
    }

    if (request.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
    {
        foreach (var header in headersElement.EnumerateObject())
        {
            var value = ReadRequiredString(header.Value, $"request.headers.{header.Name}");
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) && content is not null)
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }

            if (!(content?.Headers.TryAddWithoutValidation(header.Name, value) ?? false))
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }
    }

    if (content is not null)
    {
        if (content.Headers.ContentType is null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        }

        requestMessage.Content = content;
    }

    using var responseMessage = await client.SendAsync(requestMessage);
    var bodyText = await responseMessage.Content.ReadAsStringAsync();
    object? bodyJson = null;
    if (!string.IsNullOrWhiteSpace(bodyText))
    {
        try
        {
            using var responseDocument = JsonDocument.Parse(bodyText);
            bodyJson = ConvertJsonElement(responseDocument.RootElement);
        }
        catch (JsonException)
        {
        }
    }

    return new AdapterResult(
        "dotnet",
        ReadString(vector, "id"),
        "http",
        null,
        "success",
        new
        {
            statusCode = (int)responseMessage.StatusCode,
            headers = NormalizeHeaders(responseMessage),
            bodyText,
            bodyJson
        },
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
    var appId = Convert.ToString(ResolveCaptureValue(step, captures, stepIndex, "appId"))
        ?? throw new InvalidOperationException($"request.steps[{stepIndex}].appId 不能为空。");

    return new Dictionary<string, object?>
    {
        ["appId"] = appId,
        ["scope"] = ResolveCaptureValue(step, captures, stepIndex, "scope")
    };
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
        ["payload"] = ConvertJToken(@event.Payload)
    };
}

object NormalizeAppInstance(AppInstance instance)
{
    return ConvertJsonElement(JsonSerializer.SerializeToElement(instance, jsonOptions))!;
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

    if (exception.TryGetDataProperty("calleeError", out var calleeErrorToken) && calleeErrorToken is JObject calleeErrorObject)
    {
        var calleePayload = new Dictionary<string, object?>
        {
            ["code"] = calleeErrorObject.Value<int>("code"),
            ["message"] = calleeErrorObject.Value<string>("message")
        };

        if (calleeErrorObject.TryGetValue("data", out var dataToken))
        {
            calleePayload["data"] = dataToken.Type == JTokenType.Null ? null : ConvertJToken(dataToken);
        }

        payload["calleeError"] = calleePayload;
    }
    else if (exception.CalleeError is { } calleeError)
    {
        var calleePayload = new Dictionary<string, object?>
        {
            ["code"] = calleeError.Code,
            ["message"] = calleeError.Message
        };
        if (calleeError.Data is { } data)
        {
            calleePayload["data"] = ConvertJToken(data);
        }

        payload["calleeError"] = calleePayload;
    }

    return payload;
}

AdapterResult CreateLocalValidationError(string vectorId, string operation, ArgumentException exception)
{
    return new AdapterResult(
        "dotnet",
        vectorId,
        "sdk-invocation",
        operation,
        "error",
        new
        {
            code = -32602,
            message = "invalid_params",
            source = "sdk_local_validation",
            detail = exception.Message
        },
        null);
}

string ClassifyDiscoveryFailure(string? explicitDataDir, Exception exception)
{
    if (!string.IsNullOrWhiteSpace(explicitDataDir) &&
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(explicitDataDir)),
            "runtime",
            StringComparison.OrdinalIgnoreCase))
    {
        return "runtime_subdirectory_rejected";
    }

    return exception.Message.Contains("hub.json", StringComparison.Ordinal)
           || exception.Message.Contains("tokenFile", StringComparison.Ordinal)
           || exception.Message.Contains("token 文件", StringComparison.Ordinal)
        ? "invalid_runtime"
        : "discovery_failed";
}

Dictionary<string, string> NormalizeHeaders(HttpResponseMessage responseMessage)
{
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var header in responseMessage.Headers)
    {
        headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
    }

    foreach (var header in responseMessage.Content.Headers)
    {
        headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
    }

    return headers;
}

object? ConvertJsonElement(JsonElement? element)
{
    if (element is null)
    {
        return null;
    }

    return JsonSerializer.Deserialize<object>(element.Value.GetRawText(), jsonOptions);
}

object? ConvertJToken(JToken? token)
{
    if (token is null)
    {
        return null;
    }

    return JsonSerializer.Deserialize<object>(token.ToString(Newtonsoft.Json.Formatting.None), jsonOptions);
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
    var serialized = new Dictionary<string, object?>
    {
        ["sdk"] = payload.Sdk,
        ["outcome"] = payload.Outcome
    };

    if (payload.VectorId is not null)
    {
        serialized["vectorId"] = payload.VectorId;
    }

    if (payload.Phase is not null)
    {
        serialized["phase"] = payload.Phase;
    }

    if (payload.Operation is not null)
    {
        serialized["operation"] = payload.Operation;
    }

    if (payload.Actual is not null)
    {
        serialized["actual"] = payload.Actual;
    }

    if (payload.Error is not null)
    {
        serialized["error"] = payload.Error;
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(serialized, payloadJsonOptions));
}

internal sealed record AdapterResult(
    string Sdk,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VectorId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Operation,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Actual,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Error);

internal sealed record EventReadResult(EventReadStatus Status, object? Event);

internal enum EventReadStatus
{
    Received,
    Timeout,
    Closed
}
