namespace DevHub.Host.Tests;

using System.Text.Json;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HostBootstrapper 行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostBootstrapperTests : IDisposable
{
    private const string ExpectedHubVersion = "1.0.1-test";
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly string _instancesDirectory;
    private readonly string _logsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HostBootstrapperTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostBootstrapperTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _definitionsDirectory = Path.Combine(_tempRoot, "apps", "definitions");
        _instancesDirectory = Path.Combine(_tempRoot, "apps", "instances");
        _logsDirectory = Path.Combine(_tempRoot, "logs");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
        Directory.CreateDirectory(_instancesDirectory);
        Directory.CreateDirectory(_logsDirectory);
    }

    [Fact]
    public void Impl_Initialize_ShouldCreateTokenAndRefreshDefinitions()
    {
        WriteDefinition("bootstrap.init.app");

        using var context = CreateContext();

        context.Bootstrapper.Initialize();

        var tokenFilePath = Path.Combine(_runtimeDirectory, "token.txt");
        Assert.True(File.Exists(tokenFilePath));

        var definition = context.DefinitionProvider.GetDefinition("bootstrap.init.app");
        Assert.NotNull(definition);
    }

    [Fact]
    public void Impl_TryPersistHubRuntime_WhenLoopbackAddressExists_ShouldWriteHubJson()
    {
        using var context = CreateContext();
        context.Bootstrapper.Initialize();

        var ok = context.Bootstrapper.TryPersistHubRuntime(
            [
                "https://127.0.0.1:7100",
                "http://0.0.0.0:7101",
                "http://localhost:7102"
            ],
            out var port);

        Assert.True(ok);
        Assert.Equal(7102, port);

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        Assert.True(File.Exists(hubJsonPath));

        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal(ExpectedHubVersion, document.RootElement.GetProperty("hubVersion").GetString());
        Assert.Equal("http://127.0.0.1:7102", document.RootElement.GetProperty("httpBaseUrl").GetString());
    }

    [Fact]
    public void Impl_TryPersistHubRuntime_WhenNoValidAddress_ShouldReturnFalse()
    {
        using var context = CreateContext();
        context.Bootstrapper.Initialize();

        var ok = context.Bootstrapper.TryPersistHubRuntime(
            [
                "https://localhost:7100",
                "http://0.0.0.0:7101",
                "invalid-address"
            ],
            out var port);

        Assert.False(ok);
        Assert.Equal(0, port);

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        Assert.False(File.Exists(hubJsonPath));
    }

    /// <inheritdoc />
    public void Dispose()
    {
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

    private BootstrapperContext CreateContext()
    {
        var runtimePathOptions = CreateRuntimePathOptions();
        var fileSystemManager = new FileSystemManager(
            Mock.Of<ILogger<FileSystemManager>>(),
            runtimePathOptions,
            RuntimeTuningOptions.Default,
            ExpectedHubVersion);

        var definitionLoader = new DefinitionLoader(runtimePathOptions.DefinitionsPath, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);

        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var invocationStore = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, new SystemClock());
        var requestWaiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var timeoutWorker = new InvocationTimeoutWorker(invocationStore, requestWaiter, new SystemClock(), Mock.Of<ILogger<InvocationTimeoutWorker>>());

        var bootstrapper = new HostBootstrapper(
            fileSystemManager,
            definitionProvider,
            timeoutWorker,
            Mock.Of<ILogger<HostBootstrapper>>());

        return new BootstrapperContext(bootstrapper, definitionProvider, appRegistry, timeoutWorker);
    }

    private RuntimePathOptions CreateRuntimePathOptions()
    {
        return RuntimePathOptions.Create(_tempRoot);
    }

    private void WriteDefinition(string appId)
    {
        var payload = new
        {
            appId,
            displayName = appId,
            capabilities = new
            {
                rpc = true,
                events = false
            }
        };

        var filePath = Path.Combine(_definitionsDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }

    private sealed class BootstrapperContext : IDisposable
    {
        public BootstrapperContext(
            HostBootstrapper bootstrapper,
            DefinitionProvider definitionProvider,
            AppRegistry appRegistry,
            InvocationTimeoutWorker timeoutWorker)
        {
            Bootstrapper = bootstrapper;
            DefinitionProvider = definitionProvider;
            _appRegistry = appRegistry;
            _timeoutWorker = timeoutWorker;
        }

        private readonly AppRegistry _appRegistry;
        private readonly InvocationTimeoutWorker _timeoutWorker;

        public HostBootstrapper Bootstrapper { get; }

        public DefinitionProvider DefinitionProvider { get; }

        public void Dispose()
        {
            _timeoutWorker.Dispose();
            _appRegistry.Dispose();
        }
    }
}
