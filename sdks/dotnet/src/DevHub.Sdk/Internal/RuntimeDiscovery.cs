using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal sealed class RuntimeConnectionInfo
{
    public required string RuntimeDirectory { get; init; }

    public required string Token { get; init; }

    public required HubRuntime Runtime { get; init; }

    public Uri RpcEndpoint => new($"{Runtime.HttpBaseUrl}/rpc", UriKind.Absolute);

    public Uri WebSocketEndpoint => new(Runtime.WsUrl, UriKind.Absolute);
}

internal static class RuntimeDiscovery
{
    private const string RuntimeDirEnvironmentVariableName = "DEVHUB_RUNTIME_DIR";

    internal static async Task<RuntimeConnectionInfo> DiscoverAsync(DevHubClientOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var runtimeDirectory = ResolveRuntimeDirectory(options.RuntimeDir);
        var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
        if (!File.Exists(hubJsonPath))
        {
            throw new InvalidOperationException($"未找到 hub.json：{hubJsonPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var hubJsonStream = File.OpenRead(hubJsonPath);
        using var hubJsonDocument = await JsonDocument.ParseAsync(hubJsonStream, cancellationToken: cancellationToken);
        ValidateHubVersion(hubJsonDocument.RootElement, hubJsonPath);

        var runtime = hubJsonDocument.RootElement.Deserialize<HubRuntime>(DevHubJson.SerializerOptions)
            ?? throw new InvalidOperationException($"hub.json 解析失败：{hubJsonPath}");

        ValidateRuntime(runtime, hubJsonPath);

        if (!File.Exists(runtime.TokenFile))
        {
            throw new InvalidOperationException($"未找到 token 文件：{runtime.TokenFile}");
        }

        var token = (await File.ReadAllTextAsync(runtime.TokenFile, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"token 文件为空：{runtime.TokenFile}");
        }

        return new RuntimeConnectionInfo
        {
            RuntimeDirectory = runtimeDirectory,
            Token = token,
            Runtime = runtime
        };
    }

    internal static string ResolveRuntimeDirectory(string? runtimeDirectoryOverride)
    {
        if (!string.IsNullOrWhiteSpace(runtimeDirectoryOverride))
        {
            return Path.GetFullPath(runtimeDirectoryOverride);
        }

        var runtimeDirectoryFromEnvironment = Environment.GetEnvironmentVariable(RuntimeDirEnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(runtimeDirectoryFromEnvironment))
        {
            return Path.GetFullPath(runtimeDirectoryFromEnvironment);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevHub",
                "runtime");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                GetUserHomePath(),
                "Library",
                "Application Support",
                "DevHub",
                "runtime");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(GetUserHomePath(), ".local", "share")
            : xdgDataHome;

        return Path.Combine(dataHome, "DevHub", "runtime");
    }

    private static void ValidateRuntime(HubRuntime runtime, string hubJsonPath)
    {
        if (runtime.ProtocolVersion != 1)
        {
            throw new InvalidOperationException($"hub.json.protocolVersion 非法：{hubJsonPath}");
        }

        if (runtime.Pid < 1)
        {
            throw new InvalidOperationException($"hub.json.pid 非法：{hubJsonPath}");
        }

        ValidateHttpBaseUrl(runtime.HttpBaseUrl, hubJsonPath);
        ValidateWebSocketUrl(runtime.WsUrl, hubJsonPath);

        if (string.IsNullOrWhiteSpace(runtime.TokenFile) || !Path.IsPathRooted(runtime.TokenFile))
        {
            throw new InvalidOperationException($"hub.json.tokenFile 非法：{hubJsonPath}");
        }

        if (runtime.StartedAtUtc == default)
        {
            throw new InvalidOperationException($"hub.json.startedAtUtc 非法：{hubJsonPath}");
        }

        if (runtime.RuntimeTuning is null ||
            runtime.RuntimeTuning.LeaseSeconds < 1 ||
            runtime.RuntimeTuning.OnlineThresholdSeconds < 1 ||
            runtime.RuntimeTuning.LaunchDedupeWindowSeconds < 1)
        {
            throw new InvalidOperationException($"hub.json.runtimeTuning 非法：{hubJsonPath}");
        }
    }

    private static void ValidateHubVersion(JsonElement root, string hubJsonPath)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("hubVersion", out var hubVersionProperty)
            && hubVersionProperty.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"hub.json.hubVersion 非法：{hubJsonPath}");
        }
    }

    private static void ValidateHttpBaseUrl(string httpBaseUrl, string hubJsonPath)
    {
        if (string.IsNullOrWhiteSpace(httpBaseUrl) || httpBaseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"hub.json.httpBaseUrl 非法：{hubJsonPath}");
        }

        if (!Uri.TryCreate(httpBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException($"hub.json.httpBaseUrl 非法：{hubJsonPath}");
        }
    }

    private static void ValidateWebSocketUrl(string wsUrl, string hubJsonPath)
    {
        if (string.IsNullOrWhiteSpace(wsUrl) || wsUrl.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"hub.json.wsUrl 非法：{hubJsonPath}");
        }

        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss") ||
            !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException($"hub.json.wsUrl 非法：{hubJsonPath}");
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUserHomePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return userProfile;
        }

        var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return personal;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        throw new InvalidOperationException("无法解析当前用户主目录。");
    }
}
