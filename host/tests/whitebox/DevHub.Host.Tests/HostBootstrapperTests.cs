namespace DevHub.Host.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Host.Runtime;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HostBootstrapper 行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostBootstrapperTests : IDisposable
{
    private const string ExpectedHubVersion = "test-host-version";
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _appsDirectory;
    private readonly string _definitionsCatalogPath;
    private readonly string _logsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HostBootstrapperTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostBootstrapperTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _appsDirectory = Path.Combine(_tempRoot, "apps");
        _definitionsCatalogPath = Path.Combine(_appsDirectory, "definitions.json");
        _logsDirectory = Path.Combine(_tempRoot, "logs");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_appsDirectory);
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

        var definition = context.DefinitionProvider.GetDefinition("bootstrap.init.app", ScopeContract.Global);
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
        Assert.Equal("http://127.0.0.1:7102", context.RuntimeContext.GetHttpBaseUrl());
        Assert.Equal("ws://127.0.0.1:7102/ws", context.RuntimeContext.GetWsUrl());
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

    [Fact]
    public void Impl_HostRuntimeContext_WhenWsUrlShapeInvalid_ShouldReject()
    {
        var context = new HostRuntimeContext();

        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", "ws://127.0.0.1:7100/events"));
        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", "ws://127.0.0.1:7100/ws?debug=true"));
        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", "ws://127.0.0.1:7100/ws?"));
        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", " ws://127.0.0.1:7100/ws"));
        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", "ws://user@127.0.0.1:7100/ws"));
        Assert.Throws<ArgumentException>(() => context.SetUrls("http://127.0.0.1:7100", "ws://127.0.0.1:7100/ws/"));
    }

    [Fact]
    public void Impl_Cleanup_WhenHubRuntimeExists_ShouldRotateHubJsonToPrevHubJson()
    {
        using var context = CreateContext();
        context.Bootstrapper.Initialize();

        var ok = context.Bootstrapper.TryPersistHubRuntime(
            [
                "http://127.0.0.1:7103"
            ],
            out var port);

        Assert.True(ok);
        Assert.Equal(7103, port);

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        var previousHubJsonPath = Path.Combine(_runtimeDirectory, "prev_hub.json");
        var currentContent = File.ReadAllText(hubJsonPath);

        context.Bootstrapper.Cleanup();

        Assert.False(File.Exists(hubJsonPath));
        Assert.True(File.Exists(previousHubJsonPath));
        Assert.Equal(currentContent, File.ReadAllText(previousHubJsonPath));
        Assert.Equal(string.Empty, context.RuntimeContext.GetHttpBaseUrl());
        Assert.Equal(string.Empty, context.RuntimeContext.GetWsUrl());
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
        var dataDirectoryInitializer = new HostDataDirectoryInitializer(
            Mock.Of<ILogger<HostDataDirectoryInitializer>>(),
            runtimePathOptions);
        var runtimeArtifactManager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            runtimePathOptions,
            RuntimeTuningOptions.Default,
            ExpectedHubVersion);
        var runtimeContext = new HostRuntimeContext();

        var definitionLoader = new DefinitionLoader(runtimePathOptions.DefinitionsCatalogPath, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);

        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());

        var bootstrapper = new HostBootstrapper(
            dataDirectoryInitializer,
            runtimeArtifactManager,
            runtimeContext,
            definitionProvider,
            Mock.Of<ILogger<HostBootstrapper>>());

        return new BootstrapperContext(bootstrapper, definitionProvider, appRegistry, runtimeArtifactManager, runtimeContext);
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
            scope = ScopeContract.Global,
            displayName = appId,
            capabilities = new
            {
                rpc = true,
                events = false
            }
        };

        DefinitionCatalogTestHelper.UpsertDefinition(_definitionsCatalogPath, JsonSerializer.Serialize(payload));
    }

    private sealed class BootstrapperContext : IDisposable
    {
        public BootstrapperContext(
            HostBootstrapper bootstrapper,
            DefinitionProvider definitionProvider,
            AppRegistry appRegistry,
            HostRuntimeArtifactManager runtimeArtifactManager,
            HostRuntimeContext runtimeContext)
        {
            Bootstrapper = bootstrapper;
            DefinitionProvider = definitionProvider;
            _appRegistry = appRegistry;
            _runtimeArtifactManager = runtimeArtifactManager;
            RuntimeContext = runtimeContext;
        }

        private readonly AppRegistry _appRegistry;
        private readonly HostRuntimeArtifactManager _runtimeArtifactManager;

        public HostBootstrapper Bootstrapper { get; }

        public DefinitionProvider DefinitionProvider { get; }

        public HostRuntimeContext RuntimeContext { get; }

        public void Dispose()
        {
            _runtimeArtifactManager.Dispose();
            _appRegistry.Dispose();
        }
    }
}
