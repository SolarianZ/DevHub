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
                httpBaseUrl = connection.Runtime.HttpBaseUrl,
                wsUrl = connection.Runtime.WsUrl,
                tokenFile = connection.Runtime.TokenFile
            }
        };

        return new AdapterResult("dotnet", ReadString(vector, "id"), "discovery", null, "success", actual, null);
    }
    catch (Exception exception)
    {
        var reason = !string.IsNullOrWhiteSpace(explicitDataDir)
                     && string.Equals(Path.GetFileName(explicitDataDir), "runtime", StringComparison.OrdinalIgnoreCase)
            ? "runtime_subdirectory_rejected"
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

    var invokeRequest = BuildInvokeRequest(request.GetProperty("invokeRequest"));
    try
    {
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
                value = ConvertJToken(requestResult.Value)
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
}

async Task<AdapterResult> RunEventsAsync(JsonElement context, JsonElement vector, JsonElement request)
{
    var dataDir = ReadString(context, "dataDir");
    var steps = ReadRequiredArray(request, "steps");
    var eventClients = new Dictionary<string, DevHubEventsClient>(StringComparer.Ordinal);
    var eventEnumerators = new Dictionary<string, IAsyncEnumerator<DevHubEvent>>(StringComparer.Ordinal);
    var httpClients = new Dictionary<string, DevHubClient>(StringComparer.Ordinal);
    var captures = new Dictionary<string, object?>(StringComparer.Ordinal);
    var registeredInstances = new List<(string ClientName, string InstanceId, string Password)>();

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
                    await client.RegisterInstanceAsync(instance, password);
                    registeredInstances.Add((clientName, instance.InstanceId, password));
                    break;
                }
                case "unregister_instance":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var instanceId = Convert.ToString(ResolveCaptureValue(step, captures, index, "instanceId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].instanceId 不能为空。");
                    var password = Convert.ToString(ResolveCaptureValue(step, captures, index, "password"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].password 不能为空。");
                    await client.UnregisterInstanceAsync(instanceId, password);
                    break;
                }
                case "validate_definition":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var validation = await client.ValidateDefinitionAsync(BuildAppDefinition(step.GetProperty("definition")));
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] =
                            NormalizeDefinitionValidationResult(validation);
                    }
                    break;
                }
                case "upsert_definition":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var definition = await client.UpsertDefinitionAsync(BuildAppDefinition(step.GetProperty("definition")));
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] =
                            NormalizeDefinition(definition);
                    }
                    break;
                }
                case "delete_definition":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var appId = Convert.ToString(ResolveCaptureValue(step, captures, index, "appId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].appId 不能为空。");
                    await client.DeleteDefinitionAsync(appId);
                    if (step.TryGetProperty("captureAs", out var captureElement))
                    {
                        captures[ReadRequiredString(captureElement, $"request.steps[{index}].captureAs")] = new { ok = true };
                    }
                    break;
                }
                case "get_definition":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var appId = Convert.ToString(ResolveCaptureValue(step, captures, index, "appId"))
                        ?? throw new InvalidOperationException($"request.steps[{index}].appId 不能为空。");
                    var definition = await client.GetDefinitionAsync(appId);
                    captures[ReadString(step, "captureAs")] = NormalizeDefinition(definition);
                    break;
                }
                case "list_definitions":
                {
                    var client = RequireValue(httpClients, ReadString(step, "client"), index, "http client");
                    var definitions = await client.ListDefinitionsAsync();
                    captures[ReadString(step, "captureAs")] = definitions.Select(NormalizeDefinition).ToArray();
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
                await client.UnregisterInstanceAsync(registered.InstanceId, registered.Password);
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
            request.Target = new InvocationTarget
            {
                Scope = ReadOptionalString(targetElement, "scope"),
                InstanceId = ReadOptionalString(targetElement, "instanceId")
            };
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

AppDefinition BuildAppDefinition(JsonElement payload)
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("definition 必须为对象。");
    }

    return JsonSerializer.Deserialize<AppDefinition>(payload.GetRawText(), jsonOptions)
           ?? throw new InvalidOperationException("definition 无法解析为 AppDefinition。");
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

    return new AppInstanceRegistration
    {
        InstanceId = ReadString(payload, "instanceId"),
        AppId = ReadString(payload, "appId"),
        Scope = ReadOptionalString(payload, "scope"),
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
}

object NormalizeDefinition(AppDefinition definition)
{
    return JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(definition, jsonOptions), jsonOptions)
           ?? new Dictionary<string, object?>();
}

object NormalizeDefinitionValidationResult(DefinitionValidationResult result)
{
    return JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(result, jsonOptions), jsonOptions)
           ?? new Dictionary<string, object?>();
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

    if (exception.CalleeError is { } calleeError)
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
