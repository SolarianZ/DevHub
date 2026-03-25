using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevHub.Sdk;
using DevHub.Sdk.Models;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

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
    var result = vector.TryGetProperty("expectedDiscovery", out _)
        ? await RunDiscoveryAsync(root, vector)
        : request.TryGetProperty("kind", out var kindElement) &&
          kindElement.ValueKind == JsonValueKind.String &&
          (string.Equals(kindElement.GetString(), "sdk.notify", StringComparison.Ordinal) ||
           string.Equals(kindElement.GetString(), "sdk.request", StringComparison.Ordinal))
            ? await RunInvocationAsync(root, vector, request)
            : await RunRpcAsync(root, vector);
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
}

async Task<AdapterResult> RunRpcAsync(JsonElement context, JsonElement vector)
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
    using var content = new StringContent(vector.GetProperty("request").GetRawText(), Encoding.UTF8, "application/json");

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
            calleePayload["data"] = ConvertJsonElement(data);
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

string ReadString(JsonElement element, string propertyName)
{
    return element.GetProperty(propertyName).GetString()
           ?? throw new InvalidOperationException($"{propertyName} 不能为空。");
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
