using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.TestHost;

/// <summary>
/// DevHub Host 进程测试夹具。
/// </summary>
internal sealed class DevHubHostFixture : IAsyncDisposable
{
    private const string DataDirEnvironmentVariable = "DEVHUB_DATA_DIR";
    private const string SingleInstanceSlotEnvironmentVariable = "DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS";
    private const string PrebuiltHostAssemblyEnvironmentVariable = "DEVHUB_DOTNET_SDK_HOST_ASSEMBLY";
    private const string SharedPrebuiltHostAssemblyEnvironmentVariable = "DEVHUB_SDK_HOST_ASSEMBLY";
    private const string TestLiveStatusEnvironmentVariable = "DEVHUB_TEST_LIVE_STATUS";
    private const string HostAssemblyFileName = "DevHub.Host.dll";
    private const string HostTargetFramework = "net10.0";
    private const int LongWaitStatusThresholdSeconds = 8;
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> SharedHostAssemblyBuilds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentBag<string> SharedHostBuildRoots = new();
    private static int _cleanupHandlerRegistered;

    private readonly string _tempRoot;
    private readonly string _repoRoot;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private Process? _hostProcess;

    private sealed class LongWaitStatus
    {
        private readonly string _label;
        private readonly int _estimatedSeconds;
        private readonly bool _liveEnabled;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _entered;
        private int _lastRenderedSecond = -1;
        private int _lastRenderLength;

        public LongWaitStatus(string label, double estimatedSeconds)
        {
            _label = label;
            _estimatedSeconds = Math.Max(0, (int)Math.Ceiling(estimatedSeconds));
            _liveEnabled = ReadLiveStatusEnabled();
        }

        public void Tick()
        {
            var elapsedSeconds = (int)_stopwatch.Elapsed.TotalSeconds;
            if (!_entered && elapsedSeconds >= LongWaitStatusThresholdSeconds)
            {
                _entered = true;
                if (!_liveEnabled)
                {
                    Console.WriteLine($"[状态] {_label} 开始，预计等待约 {_estimatedSeconds}s");
                    return;
                }
            }

            if (_liveEnabled && _entered && elapsedSeconds != _lastRenderedSecond)
            {
                _lastRenderedSecond = elapsedSeconds;
                var content = $"[状态] {_label} 已等待 {elapsedSeconds}s";
                var trailingSpaces = new string(' ', Math.Max(0, _lastRenderLength - content.Length));
                Console.Write($"\r{content}{trailingSpaces}");
                Console.Out.Flush();
                _lastRenderLength = Math.Max(_lastRenderLength, content.Length);
            }
        }

        public void Finish()
        {
            if (!(_liveEnabled && _entered))
            {
                return;
            }

            Tick();
            Console.WriteLine();
        }
    }

    private DevHubHostFixture(
        string tempRoot,
        string repoRoot,
        string dataDirectory)
    {
        _tempRoot = tempRoot;
        _repoRoot = repoRoot;
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");

    public string AppsDirectory => Path.Combine(DataDirectory, "apps");

    public string DefinitionsCatalogPath => Path.Combine(AppsDirectory, "definitions.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    internal string TempRoot => _tempRoot;

    internal int HostProcessId => _hostProcess?.Id ?? 0;

    public static async Task<DevHubHostFixture> StartAsync()
    {
        var repoRoot = ResolveRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkIntegrationTests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(tempRoot);

        var fixture = new DevHubHostFixture(
            tempRoot,
            repoRoot,
            dataDirectory);
        await fixture.StartProcessAsync();
        return fixture;
    }

    public async Task WriteDefinitionAsync(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = definition.AppId;
        _ = definition.Scope;

        Directory.CreateDirectory(AppsDirectory);

        var definitionsByIdentity = await ReadDefinitionsAsync();
        definitionsByIdentity[GetDefinitionIdentity(definition)] = CloneDefinition(definition);
        await WriteDefinitionsAsync(definitionsByIdentity.Values);
    }

    public Task<DevHubClient> CreateClientAsync(string clientId)
    {
        return DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = clientId,
            DataDir = DataDirectory
        });
    }

    public Task<DevHubClient> CreateClientAsync(string clientId, HttpMessageHandler handler)
    {
        return DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = clientId,
            DataDir = DataDirectory
        }, handler);
    }

    public Task<DevHubEventsClient> CreateEventsClientAsync(string clientId)
    {
        return DevHubEventsClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = clientId,
            DataDir = DataDirectory
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_hostProcess is not null)
        {
            try
            {
                if (!_hostProcess.HasExited)
                {
                    _hostProcess.Kill(entireProcessTree: true);
                    await _hostProcess.WaitForExitAsync();
                }
            }
            catch
            {
            }

            _hostProcess.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task StartProcessAsync()
    {
        var hostAssemblyPath = await ResolveHostAssemblyPathAsync(_repoRoot, ResolveTestAssemblyConfiguration());

        var slot = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(hostAssemblyPath);
        startInfo.Environment[DataDirEnvironmentVariable] = DataDirectory;
        startInfo.Environment[SingleInstanceSlotEnvironmentVariable] = slot;

        _hostProcess = new Process
        {
            StartInfo = startInfo
        };

        _hostProcess.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _stdout.AppendLine(args.Data);
            }
        };

        _hostProcess.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _stderr.AppendLine(args.Data);
            }
        };

        if (!_hostProcess.Start())
        {
            throw new InvalidOperationException("启动 Host 进程失败。");
        }

        _hostProcess.BeginOutputReadLine();
        _hostProcess.BeginErrorReadLine();

        var hubJsonPath = Path.Combine(RuntimeDirectory, "hub.json");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var status = new LongWaitStatus("等待 .NET SDK Host fixture 生成 hub.json", 30);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(hubJsonPath))
                {
                    return;
                }

                if (_hostProcess.HasExited)
                {
                    throw new InvalidOperationException($"Host 进程提前退出。stdout={_stdout} stderr={_stderr}");
                }

                await Task.Delay(250);
                status.Tick();
            }
        }
        finally
        {
            status.Finish();
        }

        throw new InvalidOperationException($"等待 hub.json 超时。stdout={_stdout} stderr={_stderr}");
    }

    internal static async Task<string> ResolveHostAssemblyPathAsync(string repoRoot, string? preferredConfiguration)
    {
        return await ResolveHostAssemblyPathAsync(repoRoot, preferredConfiguration, environmentVariables: null);
    }

    internal static async Task<string> ResolveHostAssemblyPathAsync(
        string repoRoot,
        string? preferredConfiguration,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var configuredHostAssemblyPath = ResolveConfiguredHostAssemblyPathFromEnvironment(repoRoot, environmentVariables);
        if (configuredHostAssemblyPath is not null)
        {
            return configuredHostAssemblyPath;
        }

        var effectiveConfiguration = string.IsNullOrWhiteSpace(preferredConfiguration)
            ? "Release"
            : preferredConfiguration.Trim();
        var cacheKey = $"{Path.GetFullPath(repoRoot)}|{effectiveConfiguration}";
        var lazyBuild = SharedHostAssemblyBuilds.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string>>(
                () => BuildIsolatedHostAssemblyAsync(repoRoot, effectiveConfiguration),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyBuild.Value;
        }
        catch
        {
            SharedHostAssemblyBuilds.TryRemove(cacheKey, out _);
            throw;
        }
    }

    internal static string? ResolveConfiguredHostAssemblyPathFromEnvironment(
        string repoRoot,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        foreach (var environmentVariableName in new[]
                 {
                     PrebuiltHostAssemblyEnvironmentVariable,
                     SharedPrebuiltHostAssemblyEnvironmentVariable
                 })
        {
            var configuredPath = ReadEnvironmentVariable(environmentVariableName, environmentVariables);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return ResolveConfiguredHostAssemblyPath(repoRoot, configuredPath, environmentVariableName);
            }
        }

        return null;
    }

    private static bool ReadLiveStatusEnabled()
    {
        var rawValue = Environment.GetEnvironmentVariable(TestLiveStatusEnvironmentVariable);
        if (rawValue is null)
        {
            return false;
        }

        var candidate = rawValue.Trim().ToLowerInvariant();
        if (candidate is "1" or "true" or "yes" or "on")
        {
            return true;
        }

        if (candidate is "0" or "false" or "no" or "off")
        {
            return false;
        }

        throw new InvalidOperationException(
            $"{TestLiveStatusEnvironmentVariable} 必须是布尔值（1/0/true/false/yes/no/on/off）。");
    }

    internal static string ResolveConfiguredHostAssemblyPath(
        string repoRoot,
        string configuredPath,
        string environmentVariableName)
    {
        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(repoRoot, configuredPath));

        if (File.Exists(resolvedPath))
        {
            return resolvedPath;
        }

        throw new InvalidOperationException(
            $"环境变量 {environmentVariableName} 指定的 Host 程序不存在：{resolvedPath}");
    }

    internal static string ResolveBuiltHostAssemblyPath(string buildRoot, string configuration)
    {
        var hostAssemblyPath = Path.Combine(buildRoot, "bin", configuration, HostTargetFramework, HostAssemblyFileName);
        if (File.Exists(hostAssemblyPath))
        {
            return hostAssemblyPath;
        }

        throw new InvalidOperationException($"未找到构建后的 Host 程序：{hostAssemblyPath}");
    }

    private static async Task<string> BuildIsolatedHostAssemblyAsync(string repoRoot, string configuration)
    {
        var buildRoot = Path.Combine(
            Path.GetTempPath(),
            "DevHubDotNetSdkHostBuild",
            $"{configuration.ToLowerInvariant()}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(buildRoot);

        try
        {
            await BuildHostAssemblyAsync(repoRoot, configuration, buildRoot);
            RegisterBuildRootForCleanup(buildRoot);
            return ResolveBuiltHostAssemblyPath(buildRoot, configuration);
        }
        catch
        {
            TryDeleteDirectory(buildRoot);
            throw;
        }
    }

    private static async Task BuildHostAssemblyAsync(string repoRoot, string configuration, string buildRoot)
    {
        var hostProjectPath = Path.Combine(repoRoot, "host", "src", "DevHub.Host", "DevHub.Host.csproj");
        if (!File.Exists(hostProjectPath))
        {
            throw new InvalidOperationException($"未找到 Host 工程：{hostProjectPath}");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(hostProjectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add($"-p:BaseOutputPath={EnsureTrailingDirectorySeparator(Path.Combine(buildRoot, "bin"))}");

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("启动 Host 构建失败。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"构建 Host 失败。stdout={stdout} stderr={stderr}");
        }
    }

    private async Task<Dictionary<string, AppDefinition>> ReadDefinitionsAsync()
    {
        if (!File.Exists(DefinitionsCatalogPath))
        {
            return new Dictionary<string, AppDefinition>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(DefinitionsCatalogPath);
        var catalog = await JsonSerializer.DeserializeAsync<AppDefinitionsCatalogDocument>(stream, CreateJsonOptions())
            ?? new AppDefinitionsCatalogDocument();

        var definitions = new Dictionary<string, AppDefinition>(StringComparer.Ordinal);
        foreach (var appEntry in catalog.Definitions)
        {
            if (string.IsNullOrWhiteSpace(appEntry.AppId))
            {
                continue;
            }

            foreach (var scopeEntry in appEntry.Scopes)
            {
                var definition = new AppDefinition
                {
                    AppId = appEntry.AppId,
                    Scope = scopeEntry.Scope ?? string.Empty,
                    DisplayName = scopeEntry.DisplayName ?? string.Empty,
                    Description = scopeEntry.Description,
                    Launch = scopeEntry.Launch,
                    Capabilities = scopeEntry.Capabilities
                };
                definitions[GetDefinitionIdentity(definition)] = definition;
            }
        }

        return definitions;
    }

    private async Task WriteDefinitionsAsync(IEnumerable<AppDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var orderedDefinitions = definitions
            .Select(CloneDefinition)
            .OrderBy(static definition => definition.AppId, StringComparer.Ordinal)
            .ThenBy(static definition => definition.Scope.Length == 0 ? 0 : 1)
            .ThenBy(static definition => definition.Scope, StringComparer.Ordinal)
            .ToArray();

        var catalog = new AppDefinitionsCatalogDocument
        {
            Definitions = orderedDefinitions
                .GroupBy(static definition => definition.AppId, StringComparer.Ordinal)
                .Select(static group => new AppDefinitionsCatalogAppEntryDocument
                {
                    AppId = group.Key,
                    Scopes = group
                        .Select(static definition => new AppDefinitionsCatalogScopeEntryDocument
                        {
                            Scope = definition.Scope,
                            DisplayName = definition.DisplayName,
                            Description = definition.Description,
                            Launch = definition.Launch,
                            Capabilities = definition.Capabilities
                        })
                        .ToList()
                })
                .ToList()
        };

        var tempPath = Path.Combine(AppsDirectory, $".definitions.{Guid.NewGuid():N}.json.tmp");
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, CreateJsonOptions());
        }

        File.Move(tempPath, DefinitionsCatalogPath, overwrite: true);
    }

    private static AppDefinition CloneDefinition(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new AppDefinition
        {
            AppId = definition.AppId,
            Scope = definition.Scope,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            Launch = definition.Launch,
            Capabilities = definition.Capabilities
        };
    }

    private static string GetDefinitionIdentity(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return $"{definition.AppId}\n{definition.Scope}";
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
    }

    private static string? ReadEnvironmentVariable(
        string environmentVariableName,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables is not null)
        {
            return environmentVariables.TryGetValue(environmentVariableName, out var value)
                ? value?.Trim()
                : null;
        }

        return Environment.GetEnvironmentVariable(environmentVariableName)?.Trim();
    }

    private static void RegisterBuildRootForCleanup(string buildRoot)
    {
        SharedHostBuildRoots.Add(buildRoot);

        if (Interlocked.Exchange(ref _cleanupHandlerRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                foreach (var root in SharedHostBuildRoots)
                {
                    TryDeleteDirectory(root);
                }
            };
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : $"{path}{Path.DirectorySeparatorChar}";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(current.FullName, "host", "src", "DevHub.Host", "DevHub.Host.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("无法定位仓库根目录。");
    }

    private static string? ResolveTestAssemblyConfiguration()
    {
        return typeof(DevHubHostFixture)
            .Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;
    }

    private sealed class AppDefinitionsCatalogDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("definitions")]
        public List<AppDefinitionsCatalogAppEntryDocument> Definitions { get; set; } = [];
    }

    private sealed class AppDefinitionsCatalogAppEntryDocument
    {
        [JsonPropertyName("appId")]
        public string AppId { get; set; } = string.Empty;

        [JsonPropertyName("scopes")]
        public List<AppDefinitionsCatalogScopeEntryDocument> Scopes { get; set; } = [];
    }

    private sealed class AppDefinitionsCatalogScopeEntryDocument
    {
        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("launch")]
        public LaunchConfiguration? Launch { get; set; }

        [JsonPropertyName("capabilities")]
        public AppCapabilities? Capabilities { get; set; }
    }
}
