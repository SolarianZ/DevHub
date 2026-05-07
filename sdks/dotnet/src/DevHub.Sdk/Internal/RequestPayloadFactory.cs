using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal static class RequestPayloadFactory
{
    internal static object BuildGetDefinitionParams(string appId, string scope)
    {
        return new Dictionary<string, object?>
        {
            ["appId"] = ProtocolIdentifier.EnsureAppId(appId, nameof(appId)),
            ["scope"] = ScopeContract.EnsureScopedString(scope, nameof(scope))
        };
    }

    internal static object BuildGetInstanceParams(string instanceId)
    {
        return new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId))
        };
    }

    internal static object BuildValidateDefinitionParams(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = ProtocolIdentifier.EnsureAppId(definition.AppId, nameof(AppDefinition.AppId));
        _ = definition.Scope;
        _ = definition.DisplayName;
        return new Dictionary<string, object?>
        {
            ["definition"] = definition
        };
    }

    internal static object BuildUpsertDefinitionParams(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = ProtocolIdentifier.EnsureAppId(definition.AppId, nameof(AppDefinition.AppId));
        _ = definition.Scope;
        _ = definition.DisplayName;
        return new Dictionary<string, object?>
        {
            ["definition"] = definition
        };
    }

    internal static object BuildDeleteDefinitionParams(string appId, string scope)
    {
        return new Dictionary<string, object?>
        {
            ["appId"] = ProtocolIdentifier.EnsureAppId(appId, nameof(appId)),
            ["scope"] = ScopeContract.EnsureScopedString(scope, nameof(scope))
        };
    }

    internal static object BuildRegisterInstanceParams(AppInstanceRegistration instance, string password, string? launchId = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(instance.Invoke);
        var scope = instance.Scope;

        if (instance.Pid < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(instance.Pid), instance.Pid, "Pid 必须大于等于 1。");
        }

        if (instance.Meta is not null)
        {
            EnsureSerializesToObject(instance.Meta, nameof(instance), "Meta");
        }

        var instancePayload = new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(instance.InstanceId, nameof(AppInstanceRegistration.InstanceId)),
            ["appId"] = ProtocolIdentifier.EnsureAppId(instance.AppId, nameof(AppInstanceRegistration.AppId)),
            ["pid"] = instance.Pid,
            ["invoke"] = new Dictionary<string, object?>
            {
                ["poll"] = instance.Invoke.Poll,
                ["respond"] = instance.Invoke.Respond
            }
        };

        instancePayload["scope"] = ScopeContract.EnsureScopedString(scope, nameof(instance.Scope));

        if (instance.Meta is not null)
        {
            instancePayload["meta"] = instance.Meta;
        }

        var payload = new Dictionary<string, object?>
        {
            ["password"] = password,
            ["instance"] = instancePayload
        };

        if (launchId is not null)
        {
            if (string.IsNullOrWhiteSpace(launchId))
            {
                throw new ArgumentException("LaunchId 不能为空白字符串。", nameof(launchId));
            }

            payload["launchId"] = launchId;
        }

        return payload;
    }

    internal static object BuildHeartbeatParams(string instanceId, string instanceSessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);
        return new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId)),
            ["instanceSessionToken"] = instanceSessionToken
        };
    }

    internal static object BuildUnregisterParams(string instanceId, string instanceSessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);
        return new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId)),
            ["instanceSessionToken"] = instanceSessionToken
        };
    }

    internal static object BuildListDefinitionsParams(ListDefinitionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new Dictionary<string, object?>
        {
            ["scope"] = ScopeContract.EnsureScopeFilter(request.Scope, nameof(request.Scope))
        };

        if (request.AppId is not null)
        {
            payload["appId"] = ProtocolIdentifier.EnsureAppId(request.AppId, nameof(request.AppId));
        }

        return payload;
    }

    internal static object BuildListInstancesParams(ListInstancesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new Dictionary<string, object?>
        {
            ["scope"] = ScopeContract.EnsureScopeFilter(request.Scope, nameof(request.Scope))
        };
        if (request.AppId is not null)
        {
            payload["appId"] = ProtocolIdentifier.EnsureAppId(request.AppId, nameof(request.AppId));
        }

        if (request.IncludeOffline)
        {
            payload["includeOffline"] = true;
        }

        return payload;
    }

    internal static object BuildLaunchParams(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = ScopeContract.EnsureScopedString(request.Scope, nameof(request.Scope));

        if (request.WaitForRegisterMs is { } waitForRegisterMs && waitForRegisterMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.WaitForRegisterMs), waitForRegisterMs, "WaitForRegisterMs 不能小于 0。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["appId"] = ProtocolIdentifier.EnsureAppId(request.AppId, nameof(request.AppId)),
            ["scope"] = scope
        };

        if (request.DedupeKey is not null)
        {
            payload["dedupeKey"] = request.DedupeKey;
        }

        if (request.WaitForRegisterMs is not null)
        {
            payload["waitForRegisterMs"] = request.WaitForRegisterMs.Value;
        }

        return payload;
    }

    internal static object BuildNotifyParams(InvokeRequest request)
    {
        return BuildInvokeParams(request, isRequest: false);
    }

    internal static object BuildRequestParams(InvokeRequest request)
    {
        return BuildInvokeParams(request, isRequest: true);
    }

    internal static object BuildPollParams(PollRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceSessionToken);

        var maxCount = request.MaxCount ?? 10;
        var waitMs = request.WaitMs ?? 25000;

        if (maxCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxCount), maxCount, "MaxCount 必须位于 1..100。");
        }

        if (waitMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.WaitMs), waitMs, "WaitMs 不能小于 0。");
        }

        return new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(request.InstanceId, nameof(request.InstanceId)),
            ["instanceSessionToken"] = request.InstanceSessionToken,
            ["maxCount"] = maxCount,
            ["waitMs"] = waitMs
        };
    }

    internal static object BuildRespondParams(RespondRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceSessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LeaseToken);

        var hasValue = request.HasValue;
        var hasError = request.Error is not null;
        if (hasValue == hasError)
        {
            throw new ArgumentException("RespondRequest 必须且只能包含 Value 或 Error 之一。", nameof(request));
        }

        var payload = new Dictionary<string, object?>
        {
            ["instanceId"] = ProtocolIdentifier.EnsureInstanceId(request.InstanceId, nameof(request.InstanceId)),
            ["instanceSessionToken"] = request.InstanceSessionToken,
            ["invocationId"] = request.InvocationId,
            ["leaseToken"] = request.LeaseToken
        };

        if (hasValue)
        {
            payload["value"] = request.Value;
        }
        else
        {
            payload["error"] = BuildCalleeErrorPayload(request.Error!, nameof(request));
        }

        return payload;
    }

    private static object BuildInvokeParams(InvokeRequest request, bool isRequest)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Method);

        var target = request.Target ?? throw new ArgumentException("Target 不能为空。", nameof(request));
        var targetScope = ScopeContract.EnsureScopedString(target.Scope, nameof(target.Scope));
        var targetInstanceId = ProtocolIdentifier.EnsureOptionalInstanceId(target.InstanceId, nameof(target.InstanceId));

        int? ttlMs = request.Options?.TtlMs ?? (isRequest ? 300000 : 60000);
        if (!isRequest && request.Options?.WaitTimeoutMs is not null)
        {
            throw new ArgumentException("hub.invoke.notify 不支持 waitTimeoutMs。", nameof(request));
        }

        int? waitTimeoutMs = isRequest ? request.Options?.WaitTimeoutMs ?? 120000 : null;
        var queueIfOffline = request.Options?.QueueIfOffline ?? true;
        var autoLaunch = request.Options?.AutoLaunch ?? targetInstanceId is null;

        if (ttlMs is null || ttlMs < 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Options), ttlMs, "ttlMs 必须大于等于 1000。");
        }

        if (waitTimeoutMs is not null && waitTimeoutMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Options), waitTimeoutMs, "waitTimeoutMs 必须大于等于 1。");
        }

        if (waitTimeoutMs is not null && waitTimeoutMs > ttlMs)
        {
            throw new ArgumentException("waitTimeoutMs 不能大于 ttlMs。", nameof(request));
        }

        if (targetInstanceId is not null && autoLaunch)
        {
            throw new ArgumentException("指定 target.instanceId 时不能启用 autoLaunch。", nameof(request));
        }

        if (autoLaunch && !queueIfOffline)
        {
            throw new ArgumentException("启用 autoLaunch 时 queueIfOffline 必须为 true。", nameof(request));
        }

        var payload = new Dictionary<string, object?>
        {
            ["appId"] = ProtocolIdentifier.EnsureAppId(request.AppId, nameof(request.AppId)),
            ["method"] = request.Method,
            ["args"] = request.Args,
            ["options"] = new Dictionary<string, object?>
            {
                ["ttlMs"] = ttlMs,
                ["queueIfOffline"] = queueIfOffline,
                ["autoLaunch"] = autoLaunch
            }
        };

        if (isRequest)
        {
            ((Dictionary<string, object?>)payload["options"]!)["waitTimeoutMs"] = waitTimeoutMs;
        }

        payload["target"] = new Dictionary<string, object?>
        {
            ["scope"] = targetScope,
            ["instanceId"] = targetInstanceId
        };

        return payload;
    }

    private static void EnsureSerializesToObject(object value, string paramName, string propertyName)
    {
        JsonElement jsonValue;

        try
        {
            jsonValue = JsonSerializer.SerializeToElement(value, DevHubJson.SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ArgumentException($"{propertyName} 必须可序列化为 JSON 对象。", paramName, exception);
        }

        if (jsonValue.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{propertyName} 必须序列化为 JSON 对象。", paramName);
        }
    }

    private static object BuildCalleeErrorPayload(DevHubCalleeError error, string paramName)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(error.Message);

        var payload = new Dictionary<string, object?>
        {
            ["code"] = error.Code,
            ["message"] = error.Message
        };

        if (error.Data is { } data)
        {
            payload["data"] = JsonSerializer.Deserialize<object>(data.GetRawText(), DevHubJson.SerializerOptions);
        }

        return payload;
    }

}
