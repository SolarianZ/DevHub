using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using DevHub.Sdk.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    Task<JObject> SendAsync(string method, object? parameters, CancellationToken cancellationToken);
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
    public async Task<JObject> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(method, nameof(method));

        var requestId = _requestIdFactory();
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, _connectionInfo.RpcEndpoint);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _connectionInfo.Token);
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-Protocol", _options.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientId", _options.ClientId);
        requestMessage.Headers.TryAddWithoutValidation("X-DevHub-ClientSessionId", _options.ClientSessionId.ToString("D"));

        var payload = DevHubJson.Serialize(
            new JsonRpcRequestEnvelope
            {
                Id = requestId,
                Method = method,
                Params = parameters
            });

        requestMessage.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var linkedCts = CreateLinkedTokenSource(cancellationToken);
        using var responseMessage = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        var body = await CompatibilityIo.ReadAsStringAsync(responseMessage.Content, linkedCts.Token);

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP 请求失败：{(int)responseMessage.StatusCode} {responseMessage.ReasonPhrase}，响应体：{body}");
        }

        var root = DevHubJson.ParseObject(body);
        return ValidateResponseEnvelope(root, requestId);
    }

    internal HttpClient HttpClient => _httpClient;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return default;
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

    private static JObject ValidateResponseEnvelope(JObject root, string requestId)
    {
        if (!root.TryGetValue("jsonrpc", out var jsonRpcToken) ||
            jsonRpcToken.Type != JTokenType.String ||
            !string.Equals((string?)jsonRpcToken, "2.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON-RPC 响应的 jsonrpc 版本非法。");
        }

        var responseId = ReadResponseId(root);
        if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON-RPC 响应的 id 与请求不匹配。");
        }

        var hasResult = root.TryGetValue("result", out var resultToken);
        var hasError = root.TryGetValue("error", out var errorToken) && errorToken.Type != JTokenType.Null;
        if (hasResult == hasError)
        {
            throw new InvalidOperationException("JSON-RPC 响应必须且只能包含 result 或 error。");
        }

        if (hasError)
        {
            ThrowRpcException(errorToken!, requestId);
        }

        if (resultToken!.Type != JTokenType.Object)
        {
            throw new InvalidOperationException("JSON-RPC result 必须为对象。");
        }

        return (JObject)resultToken.DeepClone();
    }

    private static string ReadResponseId(JObject root)
    {
        if (!root.TryGetValue("id", out var idToken))
        {
            throw new InvalidOperationException("JSON-RPC 响应缺少 id 字段。");
        }

        return idToken.Type switch
        {
            JTokenType.String => (string?)idToken ?? string.Empty,
            JTokenType.Integer => Convert.ToString(((JValue)idToken).Value, CultureInfo.InvariantCulture) ?? string.Empty,
            JTokenType.Float => Convert.ToString(((JValue)idToken).Value, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => throw new InvalidOperationException("JSON-RPC 响应的 id 类型非法。")
        };
    }

    private static void ThrowRpcException(JToken errorToken, string requestId)
    {
        if (errorToken.Type != JTokenType.Object)
        {
            throw new InvalidOperationException("JSON-RPC error 对象非法。");
        }

        var errorObject = (JObject)errorToken;
        var code = errorObject.TryGetValue("code", out var codeToken) && TryReadInt32(codeToken, out var parsedCode)
            ? parsedCode
            : throw new InvalidOperationException("JSON-RPC error.code 非法。");

        if (!errorObject.TryGetValue("message", out var messageToken) || messageToken.Type != JTokenType.String)
        {
            throw new InvalidOperationException("JSON-RPC error.message 非法。");
        }

        var message = (string?)messageToken;
        if (message is null)
        {
            throw new InvalidOperationException("JSON-RPC error.message 非法。");
        }

        JToken? data = null;
        if (errorObject.TryGetValue("data", out var dataToken))
        {
            data = dataToken.DeepClone();
        }

        throw new DevHubRpcException(code, message, data, requestId);
    }

    private static bool TryReadInt32(JToken token, out int value)
    {
        if (token.Type == JTokenType.Integer)
        {
            var numericValue = ((JValue)token).Value;
            switch (numericValue)
            {
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                    value = (int)longValue;
                    return true;
                case short shortValue:
                    value = shortValue;
                    return true;
                case byte byteValue:
                    value = byteValue;
                    return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class JsonRpcRequestEnvelope
    {
        public string Jsonrpc { get; set; } = "2.0";

        public string Id { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object? Params { get; set; }
    }
}
