namespace DevHub.Host.Tests;

using System.Linq;
using System.Text;
using System.Text.Json;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// HTTP 通知行为规范测试。
/// </summary>
[Trait("Category", "Spec")]
public class HttpNotificationSpecTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HttpNotificationSpecTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostHttpNotificationTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _definitionsDirectory = Path.Combine(_tempRoot, "apps", "definitions");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
    }

    [Fact]
    [Trait("SpecRef", "3.1")]
    public async Task Spec_3_1_HttpNotification_ShouldReturn200WithEmptyBody()
    {
        using var harness = CreateHarness();

        const string notificationJson = """
            {"jsonrpc":"2.0","method":"hub.ping","params":{"echo":"notify"}}
            """;
        var response = await ExecuteHttpRequestAsync(harness, notificationJson, "http-notify-client");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.BodyText));
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    public async Task Spec_6_2_HttpCallWsOnlyMethod_ShouldReturnNotSupported()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-ws-only","method":"hub.events.subscribe","params":{}}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-ws-only-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-ws-only", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32099, error.GetProperty("code").GetInt32());
        Assert.Equal("not_supported", error.GetProperty("message").GetString());
        Assert.Equal("transport_mismatch", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal("ws", error.GetProperty("data").GetProperty("expected").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_HttpHubMethod_WhenParamsIsArray_ShouldReturnInvalidParams()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-array-params","method":"hub.ping","params":[1,2,3]}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-array-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-array-params", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_HttpHubMethod_WhenParamsIsNull_ShouldReturnInvalidRequest()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-null-params","method":"hub.ping","params":null}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-null-params-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-null-params", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "3.2")]
    public async Task Spec_3_2_HttpInvalidContentType_ShouldBeRejectedBeforeJsonParse()
    {
        using var harness = CreateHarness();

        var response = await ExecuteHttpRequestAsync(
            harness,
            "{\"jsonrpc\":\"2.0\",\"id\":\"ignored\",\"method\":\"hub.ping\",\"params\":",
            "http-content-type-client",
            contentType: "text/plain");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);

        using var responseDocument = JsonDocument.Parse(response.BodyText);
        var root = responseDocument.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        var error = root.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
        Assert.Equal("invalid_content_type", error.GetProperty("data").GetProperty("reason").GetString());
    }

    [Theory]
    [Trait("SpecRef", "6.1")]
    [InlineData("7", 7d)]
    [InlineData("1.5", 1.5d)]
    public async Task Spec_6_1_HttpRequest_WhenIdIsNumber_ShouldKeepIdCorrelation(string requestIdLiteral, double expectedId)
    {
        using var harness = CreateHarness();

        var requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{requestIdLiteral},\"method\":\"hub.ping\",\"params\":{{}}}}";
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-numeric-id-client");
        var root = responseDocument.RootElement;
        var responseId = root.GetProperty("id");

        Assert.Equal(JsonValueKind.Number, responseId.ValueKind);
        Assert.Equal(expectedId, responseId.GetDouble(), precision: 6);
        Assert.True(root.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("SpecRef", "5.1")]
    [Trait("SpecRef", "5.2")]
    public async Task Spec_5_1_And_5_2_HttpResponses_ShouldOmitOptionalNullFields()
    {
        File.WriteAllText(
            Path.Combine(_definitionsDirectory, "http-null-omit.app.json"),
            """
            {
              "appId": "http-null-omit.app",
              "displayName": "HTTP Null Omit App"
            }
            """);

        using var harness = CreateHarness();

        using var registerResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-register-instance",
              "method": "hub.apps.registerInstance",
              "params": {
                "instance": {
                  "instanceId": "http-null-omit-inst",
                  "appId": "http-null-omit.app",
                  "pid": 7001,
                  "invoke": {
                    "poll": true,
                    "respond": true
                  }
                }
              }
            }
            """);
        Assert.True(registerResponse.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());

        using var definitionResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-get-definition",
              "method": "hub.apps.getDefinition",
              "params": {
                "appId": "http-null-omit.app"
              }
            }
            """);

        var definition = definitionResponse.RootElement.GetProperty("result").GetProperty("definition");
        Assert.Equal("http-null-omit.app", definition.GetProperty("appId").GetString());
        Assert.False(definition.TryGetProperty("description", out _));
        Assert.False(definition.TryGetProperty("launch", out _));
        Assert.False(definition.TryGetProperty("capabilities", out _));

        using var listInstancesResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-list-instances",
              "method": "hub.apps.listInstances",
              "params": {
                "appId": "http-null-omit.app",
                "includeOffline": true,
                "includeAllScopes": true
              }
            }
            """);

        var instances = listInstancesResponse.RootElement
            .GetProperty("result")
            .GetProperty("instances")
            .EnumerateArray()
            .ToArray();
        var instance = Assert.Single(instances);
        Assert.Equal("http-null-omit-inst", instance.GetProperty("instanceId").GetString());
        Assert.False(instance.TryGetProperty("meta", out _));
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private HostTransportTestHarness CreateHarness() => new(_tempRoot);

    private static async Task<(int StatusCode, string BodyText)> ExecuteHttpRequestAsync(
        HostTransportTestHarness harness,
        string requestJson,
        string clientId,
        string? contentType = "application/json")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = contentType;
        httpContext.Request.Headers["Authorization"] = $"Bearer {harness.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = clientId;
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        httpContext.Response.Body = new MemoryStream();

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, currentPort: null, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        var bodyText = await reader.ReadToEndAsync();
        return (httpContext.Response.StatusCode, bodyText);
    }

    private static async Task<JsonDocument> ExecuteJsonRequestAsync(
        HostTransportTestHarness harness,
        string requestJson,
        string clientId = "http-null-omit-client")
    {
        var response = await ExecuteHttpRequestAsync(harness, requestJson, clientId);
        return JsonDocument.Parse(response.BodyText);
    }
}
