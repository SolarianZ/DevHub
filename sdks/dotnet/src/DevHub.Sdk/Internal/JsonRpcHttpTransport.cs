using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;

namespace DevHub.Sdk;

/// <summary>
/// DevHub HTTP transport 抽象。
/// </summary>
public interface IDevHubHttpTransport : IAsyncDisposable
{
    /// <summary>
    /// 发送 JSON-RPC 请求并返回结果对象。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <param name="parameters">参数对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应中的 <c>result</c> 对象。</returns>
    Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken);
}

/// <summary>
/// DevHub HTTP transport 工厂。
/// </summary>
public interface IDevHubHttpTransportFactory
{
    /// <summary>
    /// 创建 HTTP transport。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="connectionInfo">运行时连接信息。</param>
    /// <returns>transport 实例。</returns>
    IDevHubHttpTransport Create(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo);
}

/// <summary>
/// 默认的 JSON-RPC HTTP transport 工厂。
/// </summary>
public sealed class JsonRpcHttpTransportFactory : IDevHubHttpTransportFactory
{
    /// <inheritdoc />
    public IDevHubHttpTransport Create(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
    {
        return JsonRpcHttpTransport.Create(options, connectionInfo);
    }
}

/// <summary>
/// 默认的 JSON-RPC HTTP transport 实现。
/// </summary>
public sealed class JsonRpcHttpTransport : IDevHubHttpTransport
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
    public JsonRpcHttpTransport(
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

    internal static JsonRpcHttpTransport Create(
        DevHubClientOptions options,
        DevHubRuntimeConnectionInfo connectionInfo,
        HttpMessageHandler? handler = null,
        Func<string>? requestIdFactory = null)
    {
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        return new JsonRpcHttpTransport(httpClient, options, connectionInfo, requestIdFactory, ownsHttpClient: true);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
