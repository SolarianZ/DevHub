using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;

namespace DevHub.Sdk;

internal sealed class JsonRpcHttpTransport : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DevHubClientOptions _options;
    private readonly DevHubRuntimeConnectionInfo _connectionInfo;
    private readonly Func<string> _requestIdFactory;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// 初始化 HTTP transport。
    /// </summary>
    /// <param name="httpClient">底层 HTTP 客户端。</param>
    /// <param name="options">客户端选项。</param>
    /// <param name="connectionInfo">运行时连接信息。</param>
    /// <param name="requestIdFactory">请求标识工厂。</param>
    /// <param name="ownsHttpClient">当前 transport 是否负责释放 <paramref name="httpClient"/>。</param>
    internal JsonRpcHttpTransport(
        HttpClient httpClient,
        DevHubClientOptions options,
        DevHubRuntimeConnectionInfo connectionInfo,
        Func<string>? requestIdFactory = null,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _requestIdFactory = requestIdFactory ?? CreateRequestId;
        _ownsHttpClient = ownsHttpClient;
    }

    internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        return httpClient;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var requestId = _requestIdFactory();
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, _connectionInfo.RpcEndpoint);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _connectionInfo.Token);
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-Protocol", _options.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientId", _options.ClientId);
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", _options.ClientSessionId.ToString("D"));

        var payload = JsonSerializer.Serialize(
            new JsonRpcRequestEnvelope
            {
                Id = requestId,
                Method = method,
                Params = parameters
            },
            DevHubJson.SerializerOptions);

        requestMessage.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var linkedCts = CreateLinkedTokenSource(cancellationToken);
        using var responseMessage = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        var body = await responseMessage.Content.ReadAsStringAsync(linkedCts.Token);

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP 请求失败：{(int)responseMessage.StatusCode} {responseMessage.ReasonPhrase}，响应体：{body}");
        }

        using var document = JsonDocument.Parse(body);
        return ValidateResponseEnvelope(document.RootElement, requestId);
    }

    internal HttpClient HttpClient => _httpClient;

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private CancellationTokenSource CreateLinkedTokenSource(CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout is { } requestTimeout)
        {
            linkedCts.CancelAfter(requestTimeout);
        }

        return linkedCts;
    }

    private static string CreateRequestId()
    {
        return $"req-{Guid.NewGuid():N}";
    }

    private static JsonElement ValidateResponseEnvelope(JsonElement root, string requestId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("JSON-RPC 响应根必须为对象。");
        }

        if (!root.TryGetProperty("jsonrpc", out var jsonRpcElement) ||
            jsonRpcElement.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON-RPC 响应的 jsonrpc 版本非法。");
        }

        var responseId = ReadResponseId(root);
        if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON-RPC 响应的 id 与请求不匹配。");
        }

        var hasResult = root.TryGetProperty("result", out var resultElement);
        var hasError = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null;
        if (hasResult == hasError)
        {
            throw new InvalidOperationException("JSON-RPC 响应必须且只能包含 result 或 error。");
        }

        if (hasError)
        {
            ThrowRpcException(errorElement, requestId);
        }

        if (resultElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("JSON-RPC result 必须为对象。");
        }

        return resultElement.Clone();
    }

    private static string ReadResponseId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            throw new InvalidOperationException("JSON-RPC 响应缺少 id 字段。");
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => idElement.GetRawText(),
            _ => throw new InvalidOperationException("JSON-RPC 响应的 id 类型非法。")
        };
    }

    private static void ThrowRpcException(JsonElement errorElement, string requestId)
    {
        if (errorElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("JSON-RPC error 对象非法。");
        }

        var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode
            : throw new InvalidOperationException("JSON-RPC error.code 非法。");

        var message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()!
            : throw new InvalidOperationException("JSON-RPC error.message 非法。");

        JsonElement? data = null;
        if (errorElement.TryGetProperty("data", out var dataElement))
        {
            if (dataElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("JSON-RPC error.data 非法。");
            }

            data = dataElement.Clone();
        }

        throw new DevHubRpcException(code, message, data, requestId);
    }

    private sealed class JsonRpcRequestEnvelope
    {
        public string Jsonrpc { get; set; } = "2.0";

        public string Id { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }
    }
}
