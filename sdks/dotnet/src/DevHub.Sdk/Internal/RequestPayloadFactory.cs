using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal static class RequestPayloadFactory
{
    internal static object BuildGetDefinitionParams(string appId, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.EnsureScopedString(scope, nameof(scope))
        };
    }

    internal static object BuildValidateDefinitionParams(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new Dictionary<string, object?>
        {
            ["definition"] = definition
        };
    }

    internal static object BuildUpsertDefinitionParams(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new Dictionary<string, object?>
        {
            ["definition"] = definition
        };
    }

    internal static object BuildDeleteDefinitionParams(string appId, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.EnsureScopedString(scope, nameof(scope))
        };
    }

    internal static object BuildRegisterInstanceParams(AppInstanceRegistration instance, string password)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.AppId);
        ArgumentNullException.ThrowIfNull(instance.Invoke);

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
            ["instanceId"] = instance.InstanceId,
            ["appId"] = instance.AppId,
            ["pid"] = instance.Pid,
            ["invoke"] = new Dictionary<string, object?>
            {
                ["poll"] = instance.Invoke.Poll,
                ["respond"] = instance.Invoke.Respond
            }
        };

        instancePayload["scope"] = ScopeContract.EnsureScopedString(instance.Scope, nameof(instance.Scope));

        if (instance.Meta is not null)
        {
            instancePayload["meta"] = instance.Meta;
        }

        return new Dictionary<string, object?>
        {
            ["password"] = password,
            ["instance"] = instancePayload
        };
    }

    internal static object BuildHeartbeatParams(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId
        };
    }

    internal static object BuildUnregisterParams(string instanceId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["password"] = password
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
            payload["appId"] = request.AppId;
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
            payload["appId"] = request.AppId;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppId);
        var scope = ScopeContract.EnsureScopedString(request.Scope, nameof(request.Scope));

        if (request.WaitForRegisterMs is { } waitForRegisterMs && waitForRegisterMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.WaitForRegisterMs), waitForRegisterMs, "WaitForRegisterMs 不能小于 0。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["appId"] = request.AppId,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);

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
            ["instanceId"] = request.InstanceId,
            ["maxCount"] = maxCount,
            ["waitMs"] = waitMs
        };
    }

    internal static object BuildRespondParams(RespondRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvocationId);

        var hasValue = request.HasValue;
        var hasError = request.Error is not null;
        if (hasValue == hasError)
        {
            throw new ArgumentException("RespondRequest 必须且只能包含 Value 或 Error 之一。", nameof(request));
        }

        var payload = new Dictionary<string, object?>
        {
            ["instanceId"] = request.InstanceId,
            ["invocationId"] = request.InvocationId
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Method);

        var target = request.Target ?? throw new ArgumentException("Target 不能为空。", nameof(request));
        if (target.InstanceId is not null && string.IsNullOrWhiteSpace(target.InstanceId))
        {
            throw new ArgumentException("Target.InstanceId 不能为空白字符串。", nameof(request));
        }

        var targetScope = ScopeContract.EnsureScopedString(target.Scope, nameof(target.Scope));

        int? ttlMs = request.Options?.TtlMs ?? (isRequest ? 300000 : 60000);
        if (!isRequest && request.Options?.WaitTimeoutMs is not null)
        {
            throw new ArgumentException("hub.invoke.notify 不支持 waitTimeoutMs。", nameof(request));
        }

        int? waitTimeoutMs = isRequest ? request.Options?.WaitTimeoutMs ?? 120000 : null;
        var queueIfOffline = request.Options?.QueueIfOffline ?? true;
        var autoLaunch = request.Options?.AutoLaunch ?? target.InstanceId is null;

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

        if (target.InstanceId is not null && autoLaunch)
        {
            throw new ArgumentException("指定 target.instanceId 时不能启用 autoLaunch。", nameof(request));
        }

        if (autoLaunch && !queueIfOffline)
        {
            throw new ArgumentException("启用 autoLaunch 时 queueIfOffline 必须为 true。", nameof(request));
        }

        var payload = new Dictionary<string, object?>
        {
            ["appId"] = request.AppId,
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
            ["instanceId"] = target.InstanceId
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
            if (data.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Error.Data 必须为 JSON 对象。", paramName);
            }

            payload["data"] = JsonSerializer.Deserialize<object>(data.GetRawText(), DevHubJson.SerializerOptions);
        }

        return payload;
    }

}
