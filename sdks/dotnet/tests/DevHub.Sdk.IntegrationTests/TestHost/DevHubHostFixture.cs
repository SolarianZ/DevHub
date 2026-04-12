using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using DevHub.Sdk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

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
    private const string HostAssemblyFileName = "DevHub.Host.dll";
    private const string HostTargetFramework = "net10.0";
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> SharedHostAssemblyBuilds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentBag<string> SharedHostBuildRoots = new();
    private static int _cleanupHandlerRegistered;

    private readonly string _tempRoot;
    private readonly string _repoRoot;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private Process? _hostProcess;

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

    public string DefinitionsDirectory => Path.Combine(DataDirectory, "apps", "definitions");

    public string InstancesDirectory => Path.Combine(DataDirectory, "apps", "instances");

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
        Directory.CreateDirectory(DefinitionsDirectory);
        var path = Path.Combine(DefinitionsDirectory, $"{definition.AppId}.json");
        var content = JsonConvert.SerializeObject(
            definition,
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                DateParseHandling = DateParseHandling.None
            });
        await File.WriteAllTextAsync(path, content);
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
}
