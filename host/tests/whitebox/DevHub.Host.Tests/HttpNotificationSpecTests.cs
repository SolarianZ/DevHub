namespace DevHub.Host.Tests;

using System.Linq;
using System.Text;
using System.Text.Json;
using DevHub.Core.Models;
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
        _definitionsDirectory = Path.Combine(_tempRoot, "apps");

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
    [Trait("SpecRef", "6.3.1.1")]
    public async Task Spec_6_3_1_1_HttpHubGetVersion_WhenParamsIsNull_ShouldReturnVersion()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-get-version-null","method":"hub.getVersion","params":null}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-get-version-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-get-version-null", root.GetProperty("id").GetString());
        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Matches(
            "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:[-+][0-9A-Za-z.-]+)?$",
            result.GetProperty("version").GetString()!);
    }

    [Fact]
    [Trait("SpecRef", "6.3.1.1")]
    public async Task Spec_6_3_1_1_HttpHubGetVersion_WhenParamsContainUnexpectedField_ShouldReturnInvalidParams()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-get-version-extra","method":"hub.getVersion","params":{"verbose":true}}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-get-version-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-get-version-extra", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1.1")]
    public async Task Spec_6_3_1_1_HttpHubGetVersion_WhenParamsIsScalar_ShouldReturnInvalidParams()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-get-version-scalar","method":"hub.getVersion","params":1}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-get-version-client");
        var root = responseDocument.RootElement;

        Assert.Equal("http-get-version-scalar", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
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
    [InlineData("7", 7L)]
    [InlineData("9007199254740991", 9007199254740991L)]
    [InlineData("9223372036854775807", 9223372036854775807L)]
    public async Task Spec_6_1_HttpRequest_WhenIdIsSupportedInteger_ShouldKeepIdCorrelation(string requestIdLiteral, long expectedId)
    {
        using var harness = CreateHarness();

        var requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{requestIdLiteral},\"method\":\"hub.ping\",\"params\":{{}}}}";
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-numeric-id-client");
        var root = responseDocument.RootElement;
        var responseId = root.GetProperty("id");

        Assert.Equal(JsonValueKind.Number, responseId.ValueKind);
        Assert.Equal(expectedId, responseId.GetInt64());
        Assert.True(root.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_HttpRequest_WhenNumericIdLosesPrecision_ShouldReturnInvalidRequestWithNullId()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":9007199254740993.1,"method":"hub.ping","params":{}}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-lossy-id-client");
        var root = responseDocument.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        var error = root.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_HttpRequest_WhenNumericIdExceedsInt64_ShouldReturnInvalidRequestWithNullId()
    {
        using var harness = CreateHarness();

        const string requestJson = """
            {"jsonrpc":"2.0","id":9223372036854775808,"method":"hub.ping","params":{}}
            """;
        using var responseDocument = await ExecuteJsonRequestAsync(harness, requestJson, "http-out-of-range-id-client");
        var root = responseDocument.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        var error = root.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "5.1")]
    [Trait("SpecRef", "5.2")]
    public async Task Spec_5_1_And_5_2_HttpResponses_ShouldOmitOptionalNullFields()
    {
        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            """
            {
              "appId": "http-null-omit.app",
              "scope": "",
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
                "password": "http-notification-password",
                "instance": {
                  "instanceId": "http-null-omit-inst",
                  "appId": "http-null-omit.app",
                  "scope": "",
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
        var registeredInstance = registerResponse.RootElement.GetProperty("result").GetProperty("instance");
        Assert.False(registeredInstance.TryGetProperty("meta", out _));
        Assert.False(registeredInstance.TryGetProperty("endpoints", out _));

        using var definitionResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-get-definition",
              "method": "hub.apps.getDefinition",
              "params": {
                "appId": "http-null-omit.app",
                "scope": ""
              }
            }
            """);

        var definition = definitionResponse.RootElement.GetProperty("result").GetProperty("definition");
        Assert.Equal("http-null-omit.app", definition.GetProperty("appId").GetString());
        Assert.False(definition.TryGetProperty("description", out _));
        Assert.False(definition.TryGetProperty("launch", out _));
        Assert.False(definition.TryGetProperty("capabilities", out _));

        using var getInstanceResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-get-instance",
              "method": "hub.apps.getInstance",
              "params": {
                "instanceId": "http-null-omit-inst"
              }
            }
            """);

        var getInstanceResult = getInstanceResponse.RootElement.GetProperty("result");
        Assert.True(getInstanceResult.GetProperty("ok").GetBoolean());
        Assert.False(getInstanceResult.TryGetProperty("instanceSessionToken", out _));
        var exactInstance = getInstanceResult.GetProperty("instance");
        Assert.Equal("http-null-omit-inst", exactInstance.GetProperty("instanceId").GetString());
        Assert.False(exactInstance.TryGetProperty("meta", out _));
        Assert.False(exactInstance.TryGetProperty("endpoints", out _));

        using var listInstancesResponse = await ExecuteJsonRequestAsync(
            harness,
            """
            {
              "jsonrpc": "2.0",
              "id": "http-list-instances",
              "method": "hub.apps.listInstances",
              "params": {
                "appId": "http-null-omit.app",
                "scope": null,
                "includeOffline": true
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
        Assert.False(instance.TryGetProperty("endpoints", out _));
    }

    [Fact]
    [Trait("SpecRef", "3.1")]
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_3_1_And_6_3_10_HttpInvokeNotifyNotification_ShouldAcceptCanonicalIdentifiersAndQueueInvocation()
    {
        const string appId = "Sample.App_01";
        const string scope = "Workspace-A.v2";
        const string instanceId = "NODE_01.alpha";

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            $$"""
            {
              "appId": "{{appId}}",
              "scope": "{{scope}}",
              "displayName": "HTTP Canonical Notify App"
            }
            """);

        using var harness = CreateHarness();

        using var registerResponse = await ExecuteJsonRequestAsync(
            harness,
            $$"""
            {
              "jsonrpc": "2.0",
              "id": "http-register-canonical-notify",
              "method": "hub.apps.registerInstance",
              "params": {
                "password": "http-notification-password",
                "instance": {
                  "instanceId": "{{instanceId}}",
                  "appId": "{{appId}}",
                  "scope": "{{scope}}",
                  "pid": 7002,
                  "invoke": {
                    "poll": true,
                    "respond": true
                  }
                }
              }
            }
            """,
            "http-canonical-register-client");

        var instanceSessionToken = registerResponse.RootElement
            .GetProperty("result")
            .GetProperty("instanceSessionToken")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(instanceSessionToken));

        var notifyResponse = await ExecuteHttpRequestAsync(
            harness,
            $$"""
            {
              "jsonrpc": "2.0",
              "method": "hub.invoke.notify",
              "params": {
                "appId": "{{appId}}",
                "target": {
                  "scope": "{{scope}}",
                  "instanceId": null
                },
                "method": "sample.refresh",
                "args": {
                  "source": "http-notification"
                }
              }
            }
            """,
            "http-canonical-notify-client");

        Assert.Equal(StatusCodes.Status200OK, notifyResponse.StatusCode);
        Assert.True(string.IsNullOrEmpty(notifyResponse.BodyText));

        using var pollResponse = await ExecuteJsonRequestAsync(
            harness,
            $$"""
            {
              "jsonrpc": "2.0",
              "id": "http-poll-canonical-notify",
              "method": "hub.invoke.poll",
              "params": {
                "instanceId": "{{instanceId}}",
                "instanceSessionToken": "{{instanceSessionToken}}",
                "maxCount": 1,
                "waitMs": 0
              }
            }
            """,
            "http-canonical-poll-client");

        var item = Assert.Single(pollResponse.RootElement
            .GetProperty("result")
            .GetProperty("items")
            .EnumerateArray()
            .ToArray());
        Assert.Equal(appId, item.GetProperty("appId").GetString());
        Assert.Equal(scope, item.GetProperty("target").GetProperty("scope").GetString());
        Assert.Equal("sample.refresh", item.GetProperty("method").GetString());
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
        string? contentType = "application/json",
        int localPort = 0)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = localPort;
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

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, CancellationToken.None);
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
