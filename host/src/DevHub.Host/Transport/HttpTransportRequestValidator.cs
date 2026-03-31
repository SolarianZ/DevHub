using System.Text.Json;
using DevHub.Core.Models.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// HTTP 传输层请求校验器。
/// </summary>
internal static class HttpTransportRequestValidator
{
    /// <summary>
    /// 校验 HTTP Content-Type。
    /// </summary>
    internal static bool TryValidateContentType(
        string? contentType,
        object? requestId,
        out JsonRpcResponse errorResponse)
    {
        if (!IsValidJsonContentType(contentType))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "invalid_content_type", received = contentType });
            return false;
        }

        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 校验 HTTP 请求头、协议版本与访问令牌。
    /// </summary>
    internal static bool TryValidate(
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

        if (!TryValidateContentType(contentType, requestId, out errorResponse))
        {
            return false;
        }

        if (!TryGetHeader(headers, "Authorization", out var authorizationRaw)
            || string.IsNullOrWhiteSpace(authorizationRaw)
            || !authorizationRaw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
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
                errorResponse = TransportResponseFactory.CreateErrorResponse(
                    -32001,
                    "unauthorized",
                    requestId,
                    new { reason = "invalid_token" });
                return false;
            }
        }
        catch
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32001,
                "unauthorized",
                requestId,
                new { reason = "invalid_token" });
            return false;
        }

        if (!TryGetHeader(headers, "X-DevHub-Protocol", out var protocolRaw) || string.IsNullOrWhiteSpace(protocolRaw))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32099,
                "not_supported",
                requestId,
                new { expected = 1, reason = "missing" });
            return false;
        }

        var protocol = protocolRaw.Trim();
        if (!string.Equals(protocol, "1", StringComparison.Ordinal))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32099,
                "not_supported",
                requestId,
                new { expected = 1, received = protocol, reason = "mismatch" });
            return false;
        }

        if (!TryGetHeader(headers, "X-DevHub-ClientId", out var clientIdRaw) || string.IsNullOrWhiteSpace(clientIdRaw))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "missing_header", header = "X-DevHub-ClientId" });
            return false;
        }

        validatedClientId = clientIdRaw.Trim();

        if (!TryGetHeader(headers, "X-DevHub-ClientSessionId", out var sessionIdRaw) || string.IsNullOrWhiteSpace(sessionIdRaw))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
                -32600,
                "invalid_request",
                requestId,
                new { reason = "missing_header", header = "X-DevHub-ClientSessionId" });
            return false;
        }

        var sessionId = sessionIdRaw.Trim();
        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(
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
}
