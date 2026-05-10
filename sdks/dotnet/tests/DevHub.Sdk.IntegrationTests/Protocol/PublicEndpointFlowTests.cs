using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;

namespace DevHub.Sdk.IntegrationTests.Protocol;

/// <summary>
/// 公开协议入口黑盒测试。
/// </summary>
public sealed class PublicEndpointFlowTests
{
    [Fact]
    public async Task PublicEndpoints_ShouldAcceptRpcPreflightAndWebSocketAuthentication()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var runtime = await ReadRuntimeAsync(host.RuntimeDirectory);
        var token = await File.ReadAllTextAsync(runtime.TokenFile);

        using var httpClient = new HttpClient();
        using var rpcResponse = await httpClient.SendAsync(CreateRpcRequest(
            runtime,
            token,
            new
            {
                jsonrpc = "2.0",
                id = "public-rpc-ping",
                method = "hub.ping",
                @params = new { echo = "public-entry" }
            }));

        Assert.Equal(HttpStatusCode.OK, rpcResponse.StatusCode);
        using var rpcDocument = JsonDocument.Parse(await rpcResponse.Content.ReadAsStringAsync());
        Assert.True(rpcDocument.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal("public-entry", rpcDocument.RootElement.GetProperty("result").GetProperty("echo").GetString());

        using var preflightRequest = new HttpRequestMessage(HttpMethod.Options, BuildRpcUri(runtime));
        preflightRequest.Headers.TryAddWithoutValidation("Origin", "tauri://localhost");
        preflightRequest.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        using var preflightResponse = await httpClient.SendAsync(preflightRequest);

        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal("tauri://localhost", preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(preflightResponse.Headers.Vary, value => string.Equals(value, "Origin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("POST", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.OrdinalIgnoreCase);
        var allowHeaders = preflightResponse.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("Authorization", allowHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-DevHub-ClientSessionId", allowHeaders, StringComparison.OrdinalIgnoreCase);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(runtime.WsUrl), CancellationToken.None);
        await SendWebSocketJsonAsync(socket, new
        {
            jsonrpc = "2.0",
            id = "public-ws-auth",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = token.Trim(),
                protocolVersion = 1,
                clientId = "public-endpoint-ws-client",
                clientSessionId = "00000000-0000-0000-0000-000000000201"
            }
        });

        using var wsAuthDocument = JsonDocument.Parse(await ReceiveWebSocketTextAsync(socket));
        Assert.True(wsAuthDocument.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(1, wsAuthDocument.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task PublicEndpoints_ShouldEnforceTransportBoundaries()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var runtime = await ReadRuntimeAsync(host.RuntimeDirectory);
        var token = await File.ReadAllTextAsync(runtime.TokenFile);

        using var httpClient = new HttpClient();
        using var httpResponse = await httpClient.SendAsync(CreateRpcRequest(
            runtime,
            token,
            new
            {
                jsonrpc = "2.0",
                id = "http-ws-only-method",
                method = "hub.events.subscribe",
                @params = new { types = new[] { "invocation.completed" } }
            }));
        using var httpDocument = JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync());
        AssertTransportMismatch(httpDocument.RootElement, "http-ws-only-method", "ws");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(runtime.WsUrl), CancellationToken.None);
        await SendWebSocketJsonAsync(socket, new
        {
            jsonrpc = "2.0",
            id = "transport-ws-auth",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = token.Trim(),
                protocolVersion = 1,
                clientId = "transport-boundary-ws-client",
                clientSessionId = "00000000-0000-0000-0000-000000000202"
            }
        });
        _ = await ReceiveWebSocketTextAsync(socket);

        await SendWebSocketJsonAsync(socket, new
        {
            jsonrpc = "2.0",
            id = "ws-http-only-method",
            method = "hub.apps.validateDefinition",
            @params = new
            {
                definition = new
                {
                    appId = "transport.boundary.app",
                    scope = "",
                    displayName = "Transport Boundary App"
                }
            }
        });

        using var wsDocument = JsonDocument.Parse(await ReceiveWebSocketTextAsync(socket));
        AssertTransportMismatch(wsDocument.RootElement, "ws-http-only-method", "http");
    }

    [Fact]
    public async Task PublicRpcEndpoint_ShouldValidateEnvelopeAndTransportBeforeDispatch()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var runtime = await ReadRuntimeAsync(host.RuntimeDirectory);
        var token = await File.ReadAllTextAsync(runtime.TokenFile);

        using var httpClient = new HttpClient();

        using var invalidJson = await httpClient.SendAsync(CreateRawRpcRequest(
            runtime,
            token,
            "{\"jsonrpc\":\"2.0\",\"id\":\"bad-json-id\",\"method\":\"hub.ping\",\"params\":",
            contentType: "application/json"));
        using (var document = JsonDocument.Parse(await invalidJson.Content.ReadAsStringAsync()))
        {
            AssertRpcError(document.RootElement, expectedCode: -32700, expectedMessage: "parse_error", expectedId: null);
        }

        using var batchRoot = await httpClient.SendAsync(CreateRawRpcRequest(
            runtime,
            token,
            "[{\"jsonrpc\":\"2.0\",\"id\":\"batch-root\",\"method\":\"hub.ping\",\"params\":{}}]",
            contentType: "application/json"));
        using (var document = JsonDocument.Parse(await batchRoot.Content.ReadAsStringAsync()))
        {
            AssertRpcError(document.RootElement, expectedCode: -32600, expectedMessage: "invalid_request", expectedId: null);
        }

        using var missingContentType = await httpClient.SendAsync(CreateRawRpcRequest(
            runtime,
            token,
            "{\"jsonrpc\":\"2.0\",\"id\":\"missing-content-type\",\"method\":\"hub.ping\",\"params\":{}}",
            contentType: null));
        using (var document = JsonDocument.Parse(await missingContentType.Content.ReadAsStringAsync()))
        {
            var error = AssertRpcError(document.RootElement, expectedCode: -32600, expectedMessage: "invalid_request", expectedId: null);
            Assert.Equal("invalid_content_type", error.GetProperty("data").GetProperty("reason").GetString());
        }

        using var missingSessionIdRequest = CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            id = "missing-session-id",
            method = "hub.ping",
            @params = new { }
        });
        missingSessionIdRequest.Headers.Remove("X-DevHub-ClientSessionId");
        using var missingSessionId = await httpClient.SendAsync(missingSessionIdRequest);
        using (var document = JsonDocument.Parse(await missingSessionId.Content.ReadAsStringAsync()))
        {
            var error = AssertRpcError(document.RootElement, expectedCode: -32600, expectedMessage: "invalid_request", expectedId: null);
            var data = error.GetProperty("data");
            Assert.Equal("missing_header", data.GetProperty("reason").GetString());
            Assert.Equal("X-DevHub-ClientSessionId", data.GetProperty("header").GetString());
        }

        using var invalidSessionIdRequest = CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            id = "invalid-session-id",
            method = "hub.ping",
            @params = new { }
        });
        invalidSessionIdRequest.Headers.Remove("X-DevHub-ClientSessionId");
        invalidSessionIdRequest.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", "not-a-uuid");
        using var invalidSessionId = await httpClient.SendAsync(invalidSessionIdRequest);
        using (var document = JsonDocument.Parse(await invalidSessionId.Content.ReadAsStringAsync()))
        {
            var error = AssertRpcError(document.RootElement, expectedCode: -32600, expectedMessage: "invalid_request", expectedId: null);
            var data = error.GetProperty("data");
            Assert.Equal("invalid_header", data.GetProperty("reason").GetString());
            Assert.Equal("X-DevHub-ClientSessionId", data.GetProperty("header").GetString());
        }

        using var invalidTokenInvalidJson = CreateRawRpcRequest(
            runtime,
            token,
            "{\"jsonrpc\":\"2.0\",\"id\":\"invalid-token-invalid-json\",\"method\":\"hub.ping\",\"params\":",
            contentType: "application/json");
        invalidTokenInvalidJson.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bad-token");
        using var unauthorized = await httpClient.SendAsync(invalidTokenInvalidJson);
        using (var document = JsonDocument.Parse(await unauthorized.Content.ReadAsStringAsync()))
        {
            var error = AssertRpcError(document.RootElement, expectedCode: -32001, expectedMessage: "unauthorized", expectedId: null);
            Assert.Equal("invalid_token", error.GetProperty("data").GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task PublicRpcEndpoint_ShouldHandleNotificationsAndCors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var runtime = await ReadRuntimeAsync(host.RuntimeDirectory);
        var token = await File.ReadAllTextAsync(runtime.TokenFile);

        using var httpClient = new HttpClient();
        using var notification = await httpClient.SendAsync(CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            method = "hub.ping",
            @params = new { echo = "notify" }
        }));
        Assert.Equal(HttpStatusCode.OK, notification.StatusCode);
        Assert.Equal(string.Empty, await notification.Content.ReadAsStringAsync());

        using var failedNotificationRequest = CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            method = "hub.ping",
            @params = new { echo = "notify" }
        });
        failedNotificationRequest.Headers.Remove("Authorization");
        using var failedNotification = await httpClient.SendAsync(failedNotificationRequest);
        using (var document = JsonDocument.Parse(await failedNotification.Content.ReadAsStringAsync()))
        {
            var error = AssertRpcError(document.RootElement, expectedCode: -32001, expectedMessage: "unauthorized", expectedId: null);
            Assert.Equal("missing_token", error.GetProperty("data").GetProperty("reason").GetString());
        }

        const string successOrigin = "tauri://localhost";
        using var corsSuccessRequest = CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            id = "cors-success",
            method = "hub.ping",
            @params = new { }
        });
        corsSuccessRequest.Headers.TryAddWithoutValidation("Origin", successOrigin);
        using var corsSuccess = await httpClient.SendAsync(corsSuccessRequest);
        AssertOriginCorsHeaders(corsSuccess, successOrigin);

        const string errorOrigin = "http://localhost:1420";
        using var corsErrorRequest = CreateRpcRequest(runtime, token, new
        {
            jsonrpc = "2.0",
            id = "cors-error",
            method = "hub.ping",
            @params = new { }
        });
        corsErrorRequest.Headers.Remove("X-DevHub-Protocol");
        corsErrorRequest.Headers.TryAddWithoutValidation("Origin", errorOrigin);
        using var corsError = await httpClient.SendAsync(corsErrorRequest);
        AssertOriginCorsHeaders(corsError, errorOrigin);
        using (var document = JsonDocument.Parse(await corsError.Content.ReadAsStringAsync()))
        {
            AssertRpcError(document.RootElement, expectedCode: -32099, expectedMessage: "not_supported", expectedId: null);
        }
    }

    private static HttpRequestMessage CreateRpcRequest(RuntimeInfo runtime, string token, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildRpcUri(runtime))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.TryAddWithoutValidation("X-DevHub-Protocol", "1");
        request.Headers.TryAddWithoutValidation("X-DevHub-ClientId", "public-endpoint-http-client");
        request.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", "00000000-0000-0000-0000-000000000200");
        return request;
    }

    private static HttpRequestMessage CreateRawRpcRequest(RuntimeInfo runtime, string token, string body, string? contentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);

        var request = new HttpRequestMessage(HttpMethod.Post, BuildRpcUri(runtime))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.TryAddWithoutValidation("X-DevHub-Protocol", "1");
        request.Headers.TryAddWithoutValidation("X-DevHub-ClientId", "public-endpoint-http-client");
        request.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", "00000000-0000-0000-0000-000000000200");
        return request;
    }

    private static Uri BuildRpcUri(RuntimeInfo runtime)
    {
        return new Uri(new Uri(runtime.HttpBaseUrl), "/rpc");
    }

    private static async Task<RuntimeInfo> ReadRuntimeAsync(string runtimeDirectory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runtimeDirectory, "hub.json")));
        var root = document.RootElement;
        return new RuntimeInfo(
            root.GetProperty("httpBaseUrl").GetString()!,
            root.GetProperty("wsUrl").GetString()!,
            root.GetProperty("tokenFile").GetString()!);
    }

    private static async Task SendWebSocketJsonAsync(ClientWebSocket socket, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<string> ReceiveWebSocketTextAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket 在返回文本消息前关闭。");
            }

            if (result.Count > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static void AssertTransportMismatch(JsonElement root, string expectedId, string expectedTransport)
    {
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32099, error.GetProperty("code").GetInt32());
        Assert.Equal("not_supported", error.GetProperty("message").GetString());
        Assert.Equal("transport_mismatch", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(expectedTransport, error.GetProperty("data").GetProperty("expected").GetString());
    }

    private static JsonElement AssertRpcError(JsonElement root, int expectedCode, string expectedMessage, string? expectedId)
    {
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var id = root.GetProperty("id");
        if (expectedId is null)
        {
            Assert.Equal(JsonValueKind.Null, id.ValueKind);
        }
        else
        {
            Assert.Equal(expectedId, id.GetString());
        }

        var error = root.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedMessage, error.GetProperty("message").GetString());
        return error;
    }

    private static void AssertOriginCorsHeaders(HttpResponseMessage response, string origin)
    {
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(response.Headers.Vary, value => string.Equals(value, "Origin", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RuntimeInfo(string HttpBaseUrl, string WsUrl, string TokenFile);
}
