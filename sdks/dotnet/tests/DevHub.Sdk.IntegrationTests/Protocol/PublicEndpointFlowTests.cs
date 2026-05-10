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

    private sealed record RuntimeInfo(string HttpBaseUrl, string WsUrl, string TokenFile);
}
