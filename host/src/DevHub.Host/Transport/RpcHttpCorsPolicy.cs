namespace DevHub.Host.Transport;

using Microsoft.AspNetCore.Http;

/// <summary>
/// `/rpc` HTTP 入口的浏览器 / WebView CORS 规则。
/// </summary>
internal static class RpcHttpCorsPolicy
{
    internal const string AllowMethodsValue = "POST, OPTIONS";
    internal const string AllowHeadersValue =
        "Authorization, Content-Type, X-DevHub-Protocol, X-DevHub-ClientId, X-DevHub-ClientSessionId";

    private const string OriginHeaderName = "Origin";
    private const string VaryHeaderName = "Vary";
    private const string AllowOriginHeaderName = "Access-Control-Allow-Origin";
    private const string AllowMethodsHeaderName = "Access-Control-Allow-Methods";
    private const string AllowHeadersHeaderName = "Access-Control-Allow-Headers";
    private const string AccessControlRequestMethodHeaderName = "Access-Control-Request-Method";

    internal static IResult CreatePreflightResponse(HttpRequest request)
    {
        ApplyPreflightHeaders(
            request.HttpContext.Response.Headers,
            request.Headers[OriginHeaderName].ToString(),
            request.Headers[AccessControlRequestMethodHeaderName].ToString());
        return Results.NoContent();
    }

    internal static IResult WrapResponse(HttpRequest request, IResult result)
    {
        ApplyResponseHeaders(request.HttpContext.Response.Headers, request.Headers[OriginHeaderName].ToString());
        return result;
    }

    private static void ApplyPreflightHeaders(IHeaderDictionary headers, string? origin, string? requestedMethod)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        if (!string.Equals(requestedMethod?.Trim(), HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyOriginHeaders(headers, origin);
        headers[AllowMethodsHeaderName] = AllowMethodsValue;
        headers[AllowHeadersHeaderName] = AllowHeadersValue;
    }

    private static void ApplyResponseHeaders(IHeaderDictionary headers, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        ApplyOriginHeaders(headers, origin);
    }

    private static void ApplyOriginHeaders(IHeaderDictionary headers, string origin)
    {
        headers[AllowOriginHeaderName] = origin;
        AppendVaryHeader(headers, OriginHeaderName);
    }

    private static void AppendVaryHeader(IHeaderDictionary headers, string value)
    {
        if (!headers.TryGetValue(VaryHeaderName, out var existingValues) || existingValues.Count == 0)
        {
            headers[VaryHeaderName] = value;
            return;
        }

        foreach (var existingValue in existingValues)
        {
            if (string.IsNullOrWhiteSpace(existingValue))
            {
                continue;
            }

            foreach (var segment in existingValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        headers.Append(VaryHeaderName, value);
    }
}
