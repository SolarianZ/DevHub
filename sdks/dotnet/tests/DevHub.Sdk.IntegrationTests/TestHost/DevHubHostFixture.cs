using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.TestHost;

/// <summary>
/// DevHub Host 进程测试夹具。
/// </summary>
internal sealed class DevHubHostFixture : IAsyncDisposable
{
    private const string DataDirEnvironmentVariable = "DEVHUB_DATA_DIR";
    private const string SingleInstanceSlotEnvironmentVariable = "DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS";

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
        var content = JsonSerializer.Serialize(
            definition,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        var hostAssemblyPath = Path.Combine(_repoRoot, "host", "src", "DevHub.Host", "bin", "Release", "net10.0", "DevHub.Host.dll");
        if (!File.Exists(hostAssemblyPath))
        {
            throw new InvalidOperationException($"未找到 Host 程序：{hostAssemblyPath}");
        }

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
}
