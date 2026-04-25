using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal static class ResponsePayloadReader
{
    internal static T DeserializeRequired<T>(JsonElement result, string location)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(result.GetRawText(), DevHubJson.SerializerOptions);
            return value ?? throw new InvalidOperationException($"无法解析 {location}。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
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

    internal static JsonElement EnsurePropertyExists(
        JsonElement payload,
        string location,
        string propertyName,
        JsonValueKind? expectedKind = null)
    {
        if (!payload.TryGetProperty(propertyName, out var propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }

        if (expectedKind is { } kind && propertyValue.ValueKind != kind)
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

    internal static void EnsureNotNull<T>(T? value, string location, string propertyName)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    internal static void ValidateAppDefinitionElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "appId");
        EnsureScopeStringProperty(element, location, "scope");
        EnsureStringProperty(element, location, "displayName");
        EnsureOptionalStringProperty(element, location, "description");

        if (element.TryGetProperty("capabilities", out var capabilitiesElement) &&
            capabilitiesElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(capabilitiesElement, $"{location}.capabilities", JsonValueKind.Object);
            EnsureOptionalBooleanProperty(capabilitiesElement, $"{location}.capabilities", "rpc");
            EnsureOptionalBooleanProperty(capabilitiesElement, $"{location}.capabilities", "events");
        }

        if (element.TryGetProperty("launch", out var launchElement) &&
            launchElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(launchElement, $"{location}.launch", JsonValueKind.Object);
            EnsureStringProperty(launchElement, $"{location}.launch", "exePath");
            EnsureOptionalStringProperty(launchElement, $"{location}.launch", "argsTemplate");
            EnsureOptionalStringProperty(launchElement, $"{location}.launch", "workingDirectory");
            EnsureOptionalStringProperty(launchElement, $"{location}.launch", "dedupeKeyTemplate");
        }
    }

    internal static void ValidateAppInstanceElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "instanceId");
        EnsureStringProperty(element, location, "appId");
        EnsureScopeStringProperty(element, location, "scope");
        EnsurePositiveIntegerProperty(element, location, "pid");
        EnsureStringProperty(element, location, "registeredAtUtc");
        EnsureStringProperty(element, location, "lastSeenUtc");

        var invokeElement = EnsurePropertyExists(element, location, "invoke", JsonValueKind.Object);
        EnsureBooleanProperty(invokeElement, $"{location}.invoke", "poll");
        EnsureBooleanProperty(invokeElement, $"{location}.invoke", "respond");

        if (element.TryGetProperty("meta", out var metaElement) &&
            metaElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(metaElement, $"{location}.meta", JsonValueKind.Object);
        }

        if (element.TryGetProperty("password", out _))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：不得包含 password。");
        }
    }

    internal static void ValidateValidationIssuesElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Array);

        var index = 0;
        foreach (var issueElement in element.EnumerateArray())
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

    private static void ValidateKnownEventPayload(JsonElement payload, string eventType, string location)
    {
        switch (eventType)
        {
            case "app.definition.upserted":
                EnsureElementKind(payload, $"{location}.payload", JsonValueKind.Object);
                EnsureStringProperty(payload, $"{location}.payload", "appId");
                var upsertedScope = EnsureScopeStringProperty(payload, $"{location}.payload", "scope");
                var definitionElement = EnsurePropertyExists(payload, $"{location}.payload", "definition", JsonValueKind.Object);
                ValidateAppDefinitionElement(definitionElement, $"{location}.payload.definition");
                var definitionScope = EnsureScopeStringProperty(definitionElement, $"{location}.payload.definition", "scope");
                if (!string.Equals(upsertedScope, definitionScope, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{location}.payload 返回结果非法：scope 与 definition.scope 必须一致。");
                }

                break;
            case "app.definition.deleted":
                EnsureElementKind(payload, $"{location}.payload", JsonValueKind.Object);
                EnsureStringProperty(payload, $"{location}.payload", "appId");
                EnsureScopeStringProperty(payload, $"{location}.payload", "scope");
                break;
            case "app.instance.registered":
            case "app.instance.unregistered":
                EnsureElementKind(payload, $"{location}.payload", JsonValueKind.Object);
                EnsureStringProperty(payload, $"{location}.payload", "appId");
                EnsureStringProperty(payload, $"{location}.payload", "instanceId");
                EnsureOptionalScopeStringProperty(payload, $"{location}.payload", "scope");
                if (payload.TryGetProperty("password", out _))
                {
                    throw new InvalidOperationException($"{location}.payload 非法：不得包含 password。");
                }

                break;
        }
    }

    internal static void ValidateInvocationElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "invocationId");
        EnsureStringProperty(element, location, "appId");
        var targetElement = EnsurePropertyExists(element, location, "target", JsonValueKind.Object);
        EnsureScopeStringProperty(targetElement, $"{location}.target", "scope");
        EnsureOptionalStringOrNullProperty(targetElement, $"{location}.target", "instanceId");
        EnsureStringProperty(element, location, "method");
        EnsureStringProperty(element, location, "kind");
        EnsureStringProperty(element, location, "createdAtUtc");

        if (element.TryGetProperty("options", out var optionsElement) &&
            optionsElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(optionsElement, $"{location}.options", JsonValueKind.Object);
            EnsureOptionalIntegerPropertyAtLeast(optionsElement, $"{location}.options", "ttlMs", 1000);
            EnsureOptionalIntegerPropertyAtLeast(optionsElement, $"{location}.options", "waitTimeoutMs", 1);
            EnsureOptionalBooleanProperty(optionsElement, $"{location}.options", "queueIfOffline");
            EnsureOptionalBooleanProperty(optionsElement, $"{location}.options", "autoLaunch");
        }

        var callerElement = EnsurePropertyExists(element, location, "caller", JsonValueKind.Object);
        EnsureStringProperty(callerElement, $"{location}.caller", "clientId");
        EnsureStringProperty(callerElement, $"{location}.caller", "clientSessionId");

        if (element.TryGetProperty("delivery", out var deliveryElement) &&
            deliveryElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(deliveryElement, $"{location}.delivery", JsonValueKind.Object);
            EnsurePositiveIntegerProperty(deliveryElement, $"{location}.delivery", "leaseSeconds");
            EnsurePositiveIntegerProperty(deliveryElement, $"{location}.delivery", "attempt");
        }
    }

    private static void EnsureElementKind(JsonElement element, string location, JsonValueKind expectedKind)
    {
        if (element.ValueKind != expectedKind)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：JSON 类型非法。");
        }
    }

    private static void EnsureStringProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JsonValueKind.String);
        if (string.IsNullOrWhiteSpace(propertyValue.GetString()))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    private static void EnsureBooleanProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName);
        if (propertyValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalBooleanProperty(JsonElement element, string location, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalStringProperty(JsonElement element, string location, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsureOptionalStringOrNullProperty(JsonElement element, string location, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static string EnsureScopeStringProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName);
        if (propertyValue.ValueKind != JsonValueKind.String || !ScopeContract.IsValidScopedString(propertyValue.GetString()))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }

        return propertyValue.GetString()!;
    }

    private static void EnsureOptionalScopeStringProperty(JsonElement element, string location, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.ValueKind != JsonValueKind.String || !ScopeContract.IsValidScopedString(propertyValue.GetString()))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsurePositiveIntegerProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JsonValueKind.Number);
        if (!propertyValue.TryGetInt32(out var value) || value < 1)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须大于等于 1。");
        }
    }

    private static void EnsureOptionalIntegerPropertyAtLeast(
        JsonElement element,
        string location,
        string propertyName,
        int minimumValue)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        if (propertyValue.ValueKind != JsonValueKind.Number ||
            !propertyValue.TryGetInt32(out var value) ||
            value < minimumValue)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须大于等于 {minimumValue}。");
        }
    }

    private static void ValidateValidationIssueElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "path");
        EnsureStringProperty(element, location, "code");
        EnsureStringProperty(element, location, "message");
    }
}
