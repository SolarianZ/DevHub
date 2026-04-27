using DevHub.Sdk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Internal;

internal static class ResponsePayloadReader
{
    internal static T DeserializeRequired<T>(JToken result, string location)
    {
        try
        {
            var value = DevHubJson.Deserialize<T>(result);
            return value ?? throw new InvalidOperationException($"无法解析 {location}。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException($"无法解析 {location}。", exception);
        }
    }

    internal static void EnsureOk(bool ok, string location)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：ok 必须为 true。");
        }
    }

    internal static JToken EnsurePropertyExists(
        JToken payload,
        string location,
        string propertyName,
        JTokenType? expectedType = null)
    {
        if (payload is not JObject payloadObject || !payloadObject.TryGetValue(propertyName, out var propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }

        if (expectedType is { } kind && propertyValue.Type != kind)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }

        return propertyValue;
    }

    internal static void EnsureTimestamp(DateTimeOffset? value, string location, string propertyName)
    {
        if (value is null || value.Value == default)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为默认值。");
        }
    }

    internal static void EnsureNotEmpty(string? value, string location, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    internal static void EnsureAppIdValue(string? value, string location, string propertyName)
    {
        if (!ProtocolIdentifier.IsValidAppId(value))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    internal static void EnsureInstanceIdValue(string? value, string location, string propertyName)
    {
        if (!ProtocolIdentifier.IsValidInstanceId(value))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    internal static void EnsureNotNull<T>(T? value, string location, string propertyName)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    internal static void ValidateAppDefinitionElement(JToken element, string location)
    {
        EnsureElementKind(element, location, JTokenType.Object);
        EnsureAppIdProperty(element, location, "appId");
        EnsureScopeStringProperty(element, location, "scope");
        EnsureStringProperty(element, location, "displayName");
        EnsureOptionalStringProperty(element, location, "description");

        if (TryGetProperty(element, "capabilities", out var capabilitiesToken) && capabilitiesToken.Type != JTokenType.Null)
        {
            EnsureElementKind(capabilitiesToken, $"{location}.capabilities", JTokenType.Object);
            EnsureOptionalBooleanProperty(capabilitiesToken, $"{location}.capabilities", "rpc");
            EnsureOptionalBooleanProperty(capabilitiesToken, $"{location}.capabilities", "events");
        }

        if (TryGetProperty(element, "launch", out var launchToken) && launchToken.Type != JTokenType.Null)
        {
            EnsureElementKind(launchToken, $"{location}.launch", JTokenType.Object);
            EnsureStringProperty(launchToken, $"{location}.launch", "exePath");
            EnsureOptionalStringProperty(launchToken, $"{location}.launch", "argsTemplate");
            EnsureOptionalStringProperty(launchToken, $"{location}.launch", "workingDirectory");
            EnsureOptionalStringProperty(launchToken, $"{location}.launch", "dedupeKeyTemplate");
        }
    }

    internal static void ValidateAppInstanceElement(JToken element, string location)
    {
        EnsureElementKind(element, location, JTokenType.Object);
        EnsureInstanceIdProperty(element, location, "instanceId");
        EnsureAppIdProperty(element, location, "appId");
        EnsureScopeStringProperty(element, location, "scope");
        EnsurePositiveIntegerProperty(element, location, "pid");
        EnsureStringProperty(element, location, "registeredAtUtc");
        EnsureStringProperty(element, location, "lastSeenUtc");

        var invokeToken = EnsurePropertyExists(element, location, "invoke", JTokenType.Object);
        EnsureBooleanProperty(invokeToken, $"{location}.invoke", "poll");
        EnsureBooleanProperty(invokeToken, $"{location}.invoke", "respond");

        if (TryGetProperty(element, "meta", out var metaToken) && metaToken.Type != JTokenType.Null)
        {
            EnsureElementKind(metaToken, $"{location}.meta", JTokenType.Object);
        }

        if (TryGetProperty(element, "password", out _))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：不得包含 password。");
        }

        if (TryGetProperty(element, "instanceSessionToken", out _))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：不得包含 instanceSessionToken。");
        }
    }

    internal static void ValidateValidationIssuesElement(JToken element, string location)
    {
        EnsureElementKind(element, location, JTokenType.Array);

        var index = 0;
        foreach (var issueElement in element.Children())
        {
            ValidateValidationIssueElement(issueElement, $"{location}[{index}]");
            index++;
        }
    }

    internal static void ValidateEventPayload(DevHubEvent evt, string location)
    {
        switch (evt.Type.Value)
        {
            case "app.definition.upserted":
            case "app.definition.deleted":
            case "app.instance.registered":
            case "app.instance.unregistered":
                if (evt.Payload is not { } requiredPayload)
                {
                    throw new InvalidOperationException($"{location}.payload 非法：不能为空。");
                }

                ValidateKnownEventPayload(requiredPayload, evt.Type.Value, location);
                break;
            default:
                return;
        }
    }

    internal static void ValidateInvocationElement(JToken element, string location)
    {
        EnsureElementKind(element, location, JTokenType.Object);
        EnsureStringProperty(element, location, "invocationId");
        EnsureAppIdProperty(element, location, "appId");
        var targetToken = EnsurePropertyExists(element, location, "target", JTokenType.Object);
        EnsureScopeStringProperty(targetToken, $"{location}.target", "scope");
        EnsureOptionalInstanceIdOrNullProperty(targetToken, $"{location}.target", "instanceId");
        EnsureStringProperty(element, location, "method");
        EnsureStringProperty(element, location, "kind");
        EnsureStringProperty(element, location, "createdAtUtc");

        if (TryGetProperty(element, "options", out var optionsToken) && optionsToken.Type != JTokenType.Null)
        {
            EnsureElementKind(optionsToken, $"{location}.options", JTokenType.Object);
            EnsureOptionalIntegerPropertyAtLeast(optionsToken, $"{location}.options", "ttlMs", 1000);
            EnsureOptionalIntegerPropertyAtLeast(optionsToken, $"{location}.options", "waitTimeoutMs", 1);
            EnsureOptionalBooleanProperty(optionsToken, $"{location}.options", "queueIfOffline");
            EnsureOptionalBooleanProperty(optionsToken, $"{location}.options", "autoLaunch");
        }

        var callerToken = EnsurePropertyExists(element, location, "caller", JTokenType.Object);
        EnsureStringProperty(callerToken, $"{location}.caller", "clientId");
        EnsureGuidStringProperty(callerToken, $"{location}.caller", "clientSessionId");

        if (TryGetProperty(element, "delivery", out var deliveryToken) && deliveryToken.Type != JTokenType.Null)
        {
            EnsureElementKind(deliveryToken, $"{location}.delivery", JTokenType.Object);
            EnsurePositiveIntegerProperty(deliveryToken, $"{location}.delivery", "leaseSeconds");
            EnsurePositiveIntegerProperty(deliveryToken, $"{location}.delivery", "attempt");
        }
    }

    private static void EnsureElementKind(JToken element, string location, JTokenType expectedKind)
    {
        if (element.Type != expectedKind)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：JSON 类型非法。");
        }
    }

    private static void EnsureStringProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.String);
        if (string.IsNullOrWhiteSpace((string?)propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    private static void EnsureGuidStringProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.String);
        var value = (string?)propertyValue;
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须为 UUID 字符串。");
        }
    }

    private static void EnsureAppIdProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.String);
        if (!ProtocolIdentifier.IsValidAppId((string?)propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureInstanceIdProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.String);
        if (!ProtocolIdentifier.IsValidInstanceId((string?)propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureBooleanProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName);
        if (propertyValue.Type != JTokenType.Boolean)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalBooleanProperty(JToken element, string location, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type != JTokenType.Boolean)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalStringProperty(JToken element, string location, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type != JTokenType.String)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalStringOrNullProperty(JToken element, string location, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type == JTokenType.Null)
        {
            return;
        }

        if (propertyValue.Type != JTokenType.String)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static string EnsureScopeStringProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.String);
        var value = (string?)propertyValue;
        if (!ScopeContract.IsValidScopedString(value))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }

        return value!;
    }

    private static void EnsureOptionalScopeStringProperty(JToken element, string location, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type != JTokenType.String || !ScopeContract.IsValidScopedString((string?)propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalInstanceIdOrNullProperty(JToken element, string location, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type == JTokenType.Null)
        {
            return;
        }

        if (propertyValue.Type != JTokenType.String || !ProtocolIdentifier.IsValidInstanceId((string?)propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsurePositiveIntegerProperty(JToken element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JTokenType.Integer);
        if (!TryReadInt32(propertyValue, out var value) || value < 1)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须大于等于 1。");
        }
    }

    private static void EnsureOptionalIntegerPropertyAtLeast(
        JToken element,
        string location,
        string propertyName,
        int minimumValue)
    {
        if (!TryGetProperty(element, propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.Type != JTokenType.Integer ||
            !TryReadInt32(propertyValue, out var value) ||
            value < minimumValue)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须大于等于 {minimumValue}。");
        }
    }

    private static void ValidateKnownEventPayload(JToken payload, string eventType, string location)
    {
        switch (eventType)
        {
            case "app.definition.upserted":
                EnsureElementKind(payload, $"{location}.payload", JTokenType.Object);
                EnsureAppIdProperty(payload, $"{location}.payload", "appId");
                var payloadScope = EnsureScopeStringProperty(payload, $"{location}.payload", "scope");
                var definitionElement = EnsurePropertyExists(payload, $"{location}.payload", "definition", JTokenType.Object);
                ValidateAppDefinitionElement(definitionElement, $"{location}.payload.definition");
                var definitionScope = (string?)EnsurePropertyExists(definitionElement, $"{location}.payload.definition", "scope", JTokenType.String);
                if (!string.Equals(payloadScope, definitionScope, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{location}.payload 返回结果非法：scope 与 definition.scope 必须一致。");
                }

                break;
            case "app.definition.deleted":
                EnsureElementKind(payload, $"{location}.payload", JTokenType.Object);
                EnsureAppIdProperty(payload, $"{location}.payload", "appId");
                EnsureScopeStringProperty(payload, $"{location}.payload", "scope");
                break;
            case "app.instance.registered":
            case "app.instance.unregistered":
                EnsureElementKind(payload, $"{location}.payload", JTokenType.Object);
                EnsureAppIdProperty(payload, $"{location}.payload", "appId");
                EnsureInstanceIdProperty(payload, $"{location}.payload", "instanceId");
                EnsureOptionalScopeStringProperty(payload, $"{location}.payload", "scope");
                if (TryGetProperty(payload, "password", out _))
                {
                    throw new InvalidOperationException($"{location}.payload 非法：不得包含 password。");
                }

                break;
        }
    }

    private static void ValidateValidationIssueElement(JToken element, string location)
    {
        EnsureElementKind(element, location, JTokenType.Object);
        EnsureStringProperty(element, location, "path");
        EnsureStringProperty(element, location, "code");
        EnsureStringProperty(element, location, "message");
    }

    private static bool TryGetProperty(JToken element, string propertyName, out JToken propertyValue)
    {
        if (element is JObject elementObject && elementObject.TryGetValue(propertyName, out propertyValue))
        {
            return true;
        }

        propertyValue = null!;
        return false;
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
}
