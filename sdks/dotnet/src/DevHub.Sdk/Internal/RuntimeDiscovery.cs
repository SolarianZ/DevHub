using DevHub.Sdk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Internal;

internal static class RuntimeDiscovery
{
    private const string DataDirEnvironmentVariableName = "DEVHUB_DATA_DIR";

    internal static async Task<DevHubRuntimeConnectionInfo> DiscoverAsync(DevHubClientOptions options, CancellationToken cancellationToken)
    {
        CompatibilityGuards.ThrowIfNull(options, nameof(options));
        options.Validate();

        var dataDirectory = ResolveDataDirectory(options.DataDir);
        var runtimeDirectory = GetRuntimeDirectory(dataDirectory);
        var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
        if (!File.Exists(hubJsonPath))
        {
            throw new InvalidOperationException($"未找到 hub.json：{hubJsonPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hubJsonContent = await CompatibilityIo.ReadAllTextAsync(hubJsonPath, cancellationToken);
        var hubJsonObject = DevHubJson.ParseObject(hubJsonContent);
        ValidateHubVersion(hubJsonObject, hubJsonPath);

        var runtime = DevHubJson.Deserialize<HubRuntime>(hubJsonObject)
            ?? throw new InvalidOperationException($"hub.json 解析失败：{hubJsonPath}");

        ValidateRuntime(runtime, hubJsonPath);

        if (!File.Exists(runtime.TokenFile))
        {
            throw new InvalidOperationException($"未找到 token 文件：{runtime.TokenFile}");
        }

        var token = (await CompatibilityIo.ReadAllTextAsync(runtime.TokenFile, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"token 文件为空：{runtime.TokenFile}");
        }

        return new DevHubRuntimeConnectionInfo(runtimeDirectory, token, runtime);
    }

    internal static string ResolveDataDirectory(string? dataDirectoryOverride)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
        {
            var explicitDataDirectory = Path.GetFullPath(dataDirectoryOverride);
            EnsureDataDirectoryIsNotRuntimeDirectory(explicitDataDirectory);
            return explicitDataDirectory;
        }

        var dataDirectoryFromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariableName);
        var resolvedDataDirectory = !string.IsNullOrWhiteSpace(dataDirectoryFromEnvironment)
            ? Path.GetFullPath(dataDirectoryFromEnvironment)
            : GetDefaultDataDirectory();

        EnsureDataDirectoryIsNotRuntimeDirectory(resolvedDataDirectory);
        return resolvedDataDirectory;
    }

    internal static string GetRuntimeDirectory(string dataDirectory)
    {
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(dataDirectory, nameof(dataDirectory));
        return Path.Combine(dataDirectory, "runtime");
    }

    private static void EnsureDataDirectoryIsNotRuntimeDirectory(string dataDirectory)
    {
        var legacyHubJsonPath = Path.Combine(dataDirectory, "hub.json");
        var legacyTokenFilePath = Path.Combine(dataDirectory, "token.txt");
        if (!File.Exists(legacyHubJsonPath) && !File.Exists(legacyTokenFilePath))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DataDir 必须指向数据根目录，不能直接指向 runtime 子目录：{dataDirectory}。请改用其上级目录，并使用 <dataDir>/runtime/hub.json 布局。");
    }

    private static string GetDefaultDataDirectory()
    {
        if (CompatibilityPlatform.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevHub");
        }

        if (CompatibilityPlatform.IsMacOS())
        {
            return Path.Combine(
                GetUserHomePath(),
                "Library",
                "Application Support",
                "DevHub");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(GetUserHomePath(), ".local", "share")
            : xdgDataHome;

        return Path.Combine(Path.GetFullPath(dataHome), "DevHub");
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

        if (string.IsNullOrWhiteSpace(runtime.TokenFile) || !CompatibilityPath.IsPathFullyQualified(runtime.TokenFile))
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

    private static void ValidateHubVersion(JObject root, string hubJsonPath)
    {
        if (root.TryGetValue("hubVersion", out var hubVersionToken)
            && hubVersionToken.Type != JTokenType.String)
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
