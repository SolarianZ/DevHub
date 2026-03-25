using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevHub.Sdk;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (args.Length != 1)
{
    WritePayload(new AdapterResult("dotnet", null, null, "error", null, new { message = "用法错误：需要 execution-context.json 路径。" }));
    return 0;
}

try
{
    using var contextDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[0], Encoding.UTF8));
    var root = contextDocument.RootElement;
    var vector = root.GetProperty("vector");
    var result = vector.TryGetProperty("expectedDiscovery", out _)
        ? await RunDiscoveryAsync(root, vector)
        : await RunRpcAsync(root, vector);
    WritePayload(result);
}
catch (Exception exception)
{
    WritePayload(new AdapterResult("dotnet", null, null, "error", null, new { message = exception.Message }));
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

        return new AdapterResult("dotnet", ReadString(vector, "id"), "discovery", "success", actual, null);
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
        "success",
        responseDocument.RootElement.Clone(),
        null);
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

void WritePayload(AdapterResult payload)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
}

internal sealed record AdapterResult(
    string Sdk,
    string? VectorId,
    string? Phase,
    string Outcome,
    object? Actual,
    object? Error);
