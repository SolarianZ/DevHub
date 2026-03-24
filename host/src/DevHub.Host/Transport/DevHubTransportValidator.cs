using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// DevHub 传输层协议校验器。
/// 统一承载 HTTP/WS 的入站协议校验，确保错误码与消息文本符合 Spec 要求。
/// </summary>
public static class DevHubTransportValidator
{
    /// <summary>
    /// 创建标准 JSON-RPC 错误响应。
    /// </summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="id">请求 ID。</param>
    /// <param name="data">错误附加数据。</param>
    public static JsonRpcResponse CreateErrorResponse(int code, string message, object? id, object? data = null)
    {
        return RpcErrorFactory.Create(id, code, message, data);
    }

    /// <summary>
    /// 创建订阅成功响应。
    /// </summary>
    /// <param name="id">请求 ID。</param>
    /// <param name="subscriptionId">订阅 ID。</param>
    /// <returns>JSON-RPC 成功响应。</returns>
    public static JsonRpcResponse CreateSubscribeSuccessResponse(object? id, string subscriptionId)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Result = new
            {
                ok = true,
                subscriptionId
            }
        };
    }

    /// <summary>
    /// 创建取消订阅成功响应。
    /// </summary>
    /// <param name="id">请求 ID。</param>
    /// <returns>JSON-RPC 成功响应。</returns>
    public static JsonRpcResponse CreateUnsubscribeSuccessResponse(object? id)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Result = new
            {
                ok = true
            }
        };
    }

    /// <summary>
    /// 校验 HTTP 传输层头与认证信息。
    /// </summary>
    /// <param name="contentType">请求 Content-Type。</param>
    /// <param name="headers">请求头集合（键不区分大小写）。</param>
    /// <param name="tokenProvider">当前有效 token 提供器。</param>
    /// <param name="requestId">请求 ID。</param>
    /// <param name="errorResponse">校验失败的错误响应。</param>
    /// <param name="validatedClientId">校验后的客户端 ID。</param>
    /// <param name="validatedClientSessionId">校验后的客户端会话 ID。</param>
    /// <returns>校验通过返回 true。</returns>
    public static bool TryValidateHttpHeaders(
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        Func<string> tokenProvider,
        object? requestId,
        out JsonRpcResponse errorResponse,
        out string? validatedClientId,
        out string? validatedClientSessionId)
    {
        validatedClientId = null;
        validatedClientSessionId = null;

        if (!IsValidJsonContentType(contentType))
        {
            errorResponse = CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "invalid_content_type", received = contentType });
            return false;
        }

        if (!TryGetHeader(headers, "X-DevHub-Protocol", out var protocolRaw) || string.IsNullOrWhiteSpace(protocolRaw))
        {
            errorResponse = CreateErrorResponse(
                -32099,
                "not_supported",
                requestId,
                new { expected = 1, reason = "missing" });
            return false;
        }

        var protocol = protocolRaw.Trim();
        if (!string.Equals(protocol, "1", StringComparison.Ordinal))
        {
            errorResponse = CreateErrorResponse(
                -32099,
                "not_supported",
                requestId,
                new { expected = 1, received = protocol, reason = "mismatch" });
            return false;
        }

        if (!TryGetHeader(headers, "Authorization", out var authorizationRaw)
            || string.IsNullOrWhiteSpace(authorizationRaw)
            || !authorizationRaw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            errorResponse = CreateErrorResponse(
                -32001,
                "unauthorized",
                requestId,
                new { reason = "missing_token" });
            return false;
        }

        var token = authorizationRaw.Substring("Bearer ".Length).Trim();
        try
        {
            var validToken = tokenProvider();
            if (!string.Equals(token, validToken, StringComparison.Ordinal))
            {
                errorResponse = CreateErrorResponse(
                    -32001,
                    "unauthorized",
                    requestId,
                    new { reason = "invalid_token" });
                return false;
            }
        }
        catch
        {
            errorResponse = CreateErrorResponse(
                -32001,
                "unauthorized",
                requestId,
                new { reason = "invalid_token" });
            return false;
        }

        if (!TryGetHeader(headers, "X-DevHub-ClientId", out var clientIdRaw) || string.IsNullOrWhiteSpace(clientIdRaw))
        {
            errorResponse = CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "missing_header", header = "X-DevHub-ClientId" });
            return false;
        }

        var clientId = clientIdRaw.Trim();
        validatedClientId = clientId;

        if (!TryGetHeader(headers, "X-DevHub-ClientSessionId", out var sessionIdRaw) || string.IsNullOrWhiteSpace(sessionIdRaw))
        {
            errorResponse = CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "missing_header", header = "X-DevHub-ClientSessionId" });
            return false;
        }

        var sessionId = sessionIdRaw.Trim();
        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            errorResponse = CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "invalid_header", header = "X-DevHub-ClientSessionId" });
            return false;
        }

        validatedClientSessionId = sessionId;

        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 处理 WS 鉴权请求并输出认证状态。
    /// </summary>
    /// <param name="request">JSON-RPC 请求。</param>
    /// <param name="tokenProvider">当前有效 token 提供器。</param>
    /// <param name="markAuthenticated">认证状态写入回调。</param>
    /// <param name="authenticated">是否认证成功。</param>
    /// <param name="clientId">认证成功后的客户端 ID。</param>
    /// <param name="clientSessionId">认证成功后的客户端会话 ID。</param>
    /// <param name="closeAfterResponse">是否应在响应后关闭连接。</param>
    /// <returns>JSON-RPC 响应。</returns>
    public static JsonRpcResponse HandleWsAuthenticate(
        JsonRpcRequest request,
        Func<string> tokenProvider,
        Func<string, string, bool> markAuthenticated,
        out bool authenticated,
        out string? clientId,
        out string? clientSessionId,
        out bool closeAfterResponse)
    {
        authenticated = false;
        clientId = null;
        clientSessionId = null;
        closeAfterResponse = false;

        if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
        {
            return CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!paramsElement.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
        }

        if (!paramsElement.TryGetProperty("protocolVersion", out var protocolElement)
            || protocolElement.ValueKind != JsonValueKind.Number
            || !protocolElement.TryGetInt32(out var protocolVersion))
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, reason = "missing" });
        }

        if (protocolVersion != 1)
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, received = protocolVersion, reason = "mismatch" });
        }

        if (!paramsElement.TryGetProperty("clientId", out var clientIdElement)
            || clientIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(clientIdElement.GetString()))
        {
            return CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!paramsElement.TryGetProperty("clientSessionId", out var sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            return CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var parsedClientId = clientIdElement.GetString()!.Trim();
        var parsedClientSessionId = sessionIdElement.GetString()!.Trim();
        if (!Guid.TryParseExact(parsedClientSessionId, "D", out _))
        {
            return CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        try
        {
            var currentToken = tokenProvider();
            if (!string.Equals(token, currentToken, StringComparison.Ordinal))
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
            }
        }
        catch
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
        }

        if (!markAuthenticated(parsedClientId, parsedClientSessionId))
        {
            closeAfterResponse = true;
            return CreateErrorResponse(-32603, "internal_error", request.Id);
        }

        authenticated = true;
        clientId = parsedClientId;
        clientSessionId = parsedClientSessionId;

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                protocolVersion = 1
            }
        };
    }

    /// <summary>
    /// 构建 JSON-RPC 请求模型并进行信封校验。
    /// </summary>
    /// <param name="root">JSON 根节点。</param>
    /// <param name="request">解析出的请求。</param>
    /// <param name="errorResponse">解析失败时的错误响应。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryBuildRpcRequest(JsonElement root, out JsonRpcRequest request, out JsonRpcResponse errorResponse)
    {
        request = null!;

        var canUseRequestId = TryExtractRequestId(root, out var requestId);
        if (!root.TryGetProperty("jsonrpc", out var jsonRpcElement)
            || jsonRpcElement.ValueKind != JsonValueKind.String
            || !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            errorResponse = CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
            return false;
        }

        if (!root.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(methodElement.GetString()))
        {
            errorResponse = CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
            return false;
        }

        if (root.TryGetProperty("id", out var idElement))
        {
            if (!TryConvertJsonRpcId(idElement, out requestId))
            {
                errorResponse = CreateErrorResponse(-32600, "invalid_request", null);
                return false;
            }
        }

        object? requestParams = null;
        if (root.TryGetProperty("params", out var paramsElement))
        {
            if (paramsElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array and not JsonValueKind.Null)
            {
                errorResponse = CreateErrorResponse(-32600, "invalid_request", requestId);
                return false;
            }

            requestParams = paramsElement.Clone();
        }

        request = new JsonRpcRequest
        {
            Id = requestId,
            Method = methodElement.GetString()!,
            Params = requestParams
        };

        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 判断 hub.* 方法是否传入了数组参数。
    /// </summary>
    /// <param name="request">请求对象。</param>
    /// <returns>若是 hub.* 且 params 为数组返回 true。</returns>
    public static bool IsHubMethodParamsArray(JsonRpcRequest request)
    {
        if (!request.Method.StartsWith("hub.", StringComparison.Ordinal))
        {
            return false;
        }

        return request.Params is JsonElement paramsElement && paramsElement.ValueKind == JsonValueKind.Array;
    }

    /// <summary>
    /// 读取订阅参数中的事件类型过滤。
    /// </summary>
    /// <param name="request">请求对象。</param>
    /// <param name="types">解析出的类型列表，null 表示订阅全部。</param>
    /// <param name="errorResponse">解析失败时的错误响应。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryReadSubscriptionTypes(JsonRpcRequest request, out IReadOnlyCollection<string>? types, out JsonRpcResponse errorResponse)
    {
        types = null;

        if (request.Params is null)
        {
            errorResponse = null!;
            return true;
        }

        if (request.Params is not JsonElement paramsElement)
        {
            errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        if (paramsElement.ValueKind == JsonValueKind.Null)
        {
            errorResponse = null!;
            return true;
        }

        if (paramsElement.ValueKind != JsonValueKind.Object)
        {
            errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        if (!paramsElement.TryGetProperty("types", out var typesElement) || typesElement.ValueKind == JsonValueKind.Null)
        {
            errorResponse = null!;
            return true;
        }

        if (typesElement.ValueKind != JsonValueKind.Array)
        {
            errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        var parsedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeElement in typesElement.EnumerateArray())
        {
            if (typeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            var eventType = typeElement.GetString()!;
            if (!HubEventBus.IsSupportedEventType(eventType))
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id, new { reason = "unsupported_event_type", type = eventType });
                return false;
            }

            parsedTypes.Add(eventType);
        }

        types = parsedTypes.Count == 0 ? null : parsedTypes.ToArray();
        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 读取 unsubscribe 所需参数。
    /// </summary>
    /// <param name="request">请求对象。</param>
    /// <param name="subscriptionId">订阅 ID。</param>
    /// <param name="errorResponse">解析失败时的错误响应。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryReadUnsubscribeParam(JsonRpcRequest request, out string subscriptionId, out JsonRpcResponse errorResponse)
    {
        subscriptionId = string.Empty;

        if (request.Params is not JsonElement paramsElement
            || paramsElement.ValueKind != JsonValueKind.Object
            || !paramsElement.TryGetProperty("subscriptionId", out var subscriptionIdElement)
            || subscriptionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(subscriptionIdElement.GetString()))
        {
            errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        subscriptionId = subscriptionIdElement.GetString()!.Trim();
        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 判断方法是否仅支持 HTTP 传输。
    /// </summary>
    /// <param name="method">RPC 方法名。</param>
    /// <returns>仅支持 HTTP 返回 true。</returns>
    public static bool IsHttpOnlyMethod(string method)
    {
        return method is
            HubRpcMethods.HubAppsRegisterInstance or
            HubRpcMethods.HubAppsHeartbeat or
            HubRpcMethods.HubAppsUnregisterInstance or
            HubRpcMethods.HubAppsLaunch or
            HubRpcMethods.HubInvokeNotify or
            HubRpcMethods.HubInvokeRequest or
            HubRpcMethods.HubInvokePoll or
            HubRpcMethods.HubInvokeRespond;
    }

    /// <summary>
    /// 判断方法是否仅支持 WebSocket 传输。
    /// </summary>
    /// <param name="method">RPC 方法名。</param>
    /// <returns>仅支持 WebSocket 返回 true。</returns>
    public static bool IsWebSocketOnlyMethod(string method)
    {
        return method is
            HubRpcMethods.HubWsAuthenticate or
            HubRpcMethods.HubEventsSubscribe or
            HubRpcMethods.HubEventsUnsubscribe;
    }

    private static bool TryGetHeader(IReadOnlyDictionary<string, string> headers, string key, out string value)
    {
        if (headers.TryGetValue(key, out value!))
        {
            return true;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsValidJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var separatorIndex = contentType.IndexOf(';');
        var mediaType = separatorIndex >= 0
            ? contentType[..separatorIndex].Trim()
            : contentType.Trim();

        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractRequestId(JsonElement root, out object? requestId)
    {
        requestId = null;

        if (!root.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        return TryConvertJsonRpcId(idElement, out requestId);
    }

    private static bool TryConvertJsonRpcId(JsonElement idElement, out object? id)
    {
        switch (idElement.ValueKind)
        {
            case JsonValueKind.String:
                id = idElement.GetString();
                return true;
            case JsonValueKind.Number:
                if (idElement.TryGetInt64(out var int64Value))
                {
                    id = int64Value;
                    return true;
                }

                if (idElement.TryGetDouble(out var doubleValue))
                {
                    id = doubleValue;
                    return true;
                }

                id = null;
                return false;
            case JsonValueKind.Null:
                id = null;
                return false;
            default:
                id = null;
                return false;
        }
    }
}
