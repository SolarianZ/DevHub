using System.Text.Json;
using DevHub.Core.Models.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// WebSocket 认证处理器。
/// </summary>
internal static class WebSocketAuthenticationProcessor
{
    /// <summary>
    /// 处理 <c>hub.ws.authenticate</c> 请求。
    /// </summary>
    internal static JsonRpcResponse Authenticate(
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
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!paramsElement.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
        }

        if (!paramsElement.TryGetProperty("protocolVersion", out var protocolElement)
            || protocolElement.ValueKind != JsonValueKind.Number
            || !protocolElement.TryGetInt32(out var protocolVersion))
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, reason = "missing" });
        }

        if (protocolVersion != 1)
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, received = protocolVersion, reason = "mismatch" });
        }

        if (!paramsElement.TryGetProperty("clientId", out var clientIdElement)
            || clientIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(clientIdElement.GetString()))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!paramsElement.TryGetProperty("clientSessionId", out var sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var parsedClientId = clientIdElement.GetString()!.Trim();
        var parsedClientSessionId = sessionIdElement.GetString()!.Trim();
        if (!Guid.TryParseExact(parsedClientSessionId, "D", out _))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        try
        {
            var currentToken = tokenProvider();
            if (!string.Equals(token, currentToken, StringComparison.Ordinal))
            {
                closeAfterResponse = true;
                return TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
            }
        }
        catch
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
        }

        if (!markAuthenticated(parsedClientId, parsedClientSessionId))
        {
            closeAfterResponse = true;
            return TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", request.Id);
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
}
