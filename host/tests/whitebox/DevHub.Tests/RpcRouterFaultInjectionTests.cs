namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// RPC Router 测试故障注入行为。
/// </summary>
[Collection(TestCollections.ProcessEnvironment)]
[Trait("Category", "Impl")]
public sealed class RpcRouterFaultInjectionTests
{
    [Fact]
    public async Task Impl_RouteAsync_WhenFaultInjectionDisabled_ShouldKeepOriginalRouting()
    {
        using var scope = new EnvironmentVariableScope(RpcTestFaultInjectionPolicy.ForceInternalErrorRequestIdsEnvironmentVariable, null);
        var policy = RpcTestFaultInjectionPolicy.Resolve(Mock.Of<ILogger<RpcTestFaultInjectionPolicy>>());
        var router = new RpcRouter([], Mock.Of<ILogger<RpcRouter>>(), policy);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "fault-disabled",
            Method = "hub.ping",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error!.Code);
        Assert.Equal("method_not_found", response.Error.Message);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenFaultInjectionEnabledForRequestId_ShouldReturnInternalError()
    {
        using var scope = new EnvironmentVariableScope(
            RpcTestFaultInjectionPolicy.ForceInternalErrorRequestIdsEnvironmentVariable,
            "fault-enabled");
        var policy = RpcTestFaultInjectionPolicy.Resolve(Mock.Of<ILogger<RpcTestFaultInjectionPolicy>>());
        var router = new RpcRouter([], Mock.Of<ILogger<RpcRouter>>(), policy);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "fault-enabled",
            Method = "hub.ping",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32603, response.Error!.Code);
        Assert.Equal("internal_error", response.Error.Message);
        Assert.Equal("fault-enabled", response.Id);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenPrefixOnlyDiffersByCase_ShouldReturnMethodNotFound()
    {
        var handler = new Mock<IRpcHandler>();
        handler.SetupGet(static candidate => candidate.Method).Returns("hub.apps");
        handler.SetupGet(static candidate => candidate.SupportsPrefixRouting).Returns(true);
        handler
            .Setup(static candidate => candidate.HandleAsync(It.IsAny<JsonRpcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonRpcResponse
            {
                Id = "case-sensitive-prefix",
                Result = new { ok = true }
            });

        var router = new RpcRouter([handler.Object], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "case-sensitive-prefix",
            Method = "hub.Apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error.Code);
        Assert.Equal("method_not_found", response.Error.Message);
        handler.Verify(static candidate => candidate.HandleAsync(It.IsAny<JsonRpcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenMostSpecificPrefixCanHandleRequest_ShouldReturnItsResult()
    {
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var mediumHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var specificHandler = new TestHandler(
            "hub.apps.instances",
            "hub.apps.instances",
            supportsPrefixRouting: true,
            request => Task.FromResult(Success(request.Id, "hub.apps.instances")));

        var router = new RpcRouter([broadHandler, mediumHandler, specificHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-order",
            Method = "hub.apps.instances.list",
            Params = null
        }, CancellationToken.None);

        AssertHandledBy(response, "hub.apps.instances");
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenSamePrefixHandlersYieldSuccess_ShouldReturnSuccessfulResult()
    {
        var firstHandler = new TestHandler(
            "hub.apps",
            "first",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var secondHandler = new TestHandler(
            "hub.apps",
            "second",
            supportsPrefixRouting: true,
            request => Task.FromResult(Success(request.Id, "second")));

        var router = new RpcRouter([firstHandler, secondHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-fallback",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        AssertHandledBy(response, "second");
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenExactHandlerThrows_ShouldReturnInternalError()
    {
        var callOrder = new List<string>();
        var handler = new TestHandler(
            "hub.ping",
            "exact",
            callOrder,
            _ => throw new InvalidOperationException("boom"));

        var router = new RpcRouter([handler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "exact-exception",
            Method = "hub.ping",
            Params = null
        }, CancellationToken.None);

        AssertInternalError(response, "exact-exception");
        Assert.Equal(["exact"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenExactHandlerThrowsActiveCancellation_ShouldPropagateCancellation()
    {
        var callOrder = new List<string>();
        var handler = new TestHandler(
            "hub.ping",
            "exact-canceled",
            callOrder,
            _ => throw new OperationCanceledException());
        var router = new RpcRouter([handler], Mock.Of<ILogger<RpcRouter>>());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await router.RouteAsync(new JsonRpcRequest
        {
            Id = "exact-canceled",
            Method = "hub.ping",
            Params = null
        }, cancellationTokenSource.Token));

        Assert.Equal(["exact-canceled"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenExactHandlerThrowsInactiveCancellation_ShouldReturnInternalError()
    {
        var callOrder = new List<string>();
        var handler = new TestHandler(
            "hub.ping",
            "exact-inactive-canceled",
            callOrder,
            _ => throw new OperationCanceledException());
        var router = new RpcRouter([handler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "exact-inactive-canceled",
            Method = "hub.ping",
            Params = null
        }, CancellationToken.None);

        AssertInternalError(response, "exact-inactive-canceled");
        Assert.Equal(["exact-inactive-canceled"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenPrefixHandlerThrowsActiveCancellation_ShouldPropagateCancellation()
    {
        var callOrder = new List<string>();
        var handler = new TestHandler(
            "hub.apps",
            "prefix-canceled",
            supportsPrefixRouting: true,
            callOrder,
            _ => throw new OperationCanceledException());
        var router = new RpcRouter([handler], Mock.Of<ILogger<RpcRouter>>());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-canceled",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, cancellationTokenSource.Token));

        Assert.Equal(["prefix-canceled"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenMatchedPrefixHandlersDoNotProduceSuccessAndOneFails_ShouldReturnInternalError()
    {
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var failingHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            supportsPrefixRouting: true,
            _ => throw new InvalidOperationException("boom"));

        var router = new RpcRouter([broadHandler, failingHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-exception",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        AssertInternalError(response, "prefix-exception");
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenMatchedPrefixHandlersDoNotProduceSuccess_ShouldReturnMethodNotFound()
    {
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var specificHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            supportsPrefixRouting: true,
            request => Task.FromResult(MethodNotFound(request.Id)));

        var router = new RpcRouter([broadHandler, specificHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-not-found",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error!.Code);
        Assert.Equal("method_not_found", response.Error.Message);
        Assert.Equal("prefix-not-found", response.Id);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenSingleMethodHandlerDoesNotOptIntoPrefixRouting_ShouldReturnMethodNotFoundWithoutInvokingHandler()
    {
        var handler = new Mock<IRpcHandler>();
        handler.SetupGet(static candidate => candidate.Method).Returns(HubRpcMethods.HubAppsLaunch);

        var router = new RpcRouter([handler.Object], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "launch-prefix-blocked",
            Method = "hub.apps.launch.anything",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error!.Code);
        Assert.Equal("method_not_found", response.Error.Message);
        handler.Verify(static candidate => candidate.HandleAsync(It.IsAny<JsonRpcRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Impl_ParseConfiguredRequestIds_WhenConfigured_ShouldNormalizeAndDeduplicate()
    {
        var requestIds = RpcTestFaultInjectionPolicy.ParseConfiguredRequestIds(
            " fault-enabled ; 42,\r\nfault-enabled ");

        Assert.Equal(
            ["42", "fault-enabled"],
            requestIds.OrderBy(static requestId => requestId, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Impl_ShouldForceInternalError_WhenRequestIdIsNumeric_ShouldMatchConfiguredText()
    {
        using var scope = new EnvironmentVariableScope(
            RpcTestFaultInjectionPolicy.ForceInternalErrorRequestIdsEnvironmentVariable,
            "42");
        var policy = RpcTestFaultInjectionPolicy.Resolve();

        Assert.True(policy.ShouldForceInternalError(42L));
        Assert.False(policy.ShouldForceInternalError(43L));
    }

    private static JsonRpcResponse MethodNotFound(object? requestId)
    {
        return RpcErrorFactory.Create(requestId, -32601, "method_not_found");
    }

    private static JsonRpcResponse Success(object? requestId, string handledBy)
    {
        return new JsonRpcResponse
        {
            Id = requestId,
            Result = new
            {
                ok = true,
                handledBy
            }
        };
    }

    private static void AssertHandledBy(JsonRpcResponse response, string handledBy)
    {
        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(handledBy, result.GetProperty("handledBy").GetString());
    }

    private static void AssertInternalError(JsonRpcResponse response, object? requestId)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(-32603, response.Error!.Code);
        Assert.Equal("internal_error", response.Error.Message);
        Assert.Equal(requestId, response.Id);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }

    private sealed class TestHandler : IRpcHandler
    {
        private readonly string _name;
        private readonly List<string>? _callOrder;
        private readonly Func<JsonRpcRequest, Task<JsonRpcResponse>> _callback;

        public TestHandler(
            string method,
            string name,
            bool supportsPrefixRouting,
            Func<JsonRpcRequest, Task<JsonRpcResponse>> callback)
            : this(method, name, supportsPrefixRouting, null, callback)
        {
        }

        public TestHandler(
            string method,
            string name,
            List<string>? callOrder,
            Func<JsonRpcRequest, Task<JsonRpcResponse>> callback)
            : this(method, name, false, callOrder, callback)
        {
        }

        public TestHandler(
            string method,
            string name,
            bool supportsPrefixRouting,
            List<string>? callOrder,
            Func<JsonRpcRequest, Task<JsonRpcResponse>> callback)
        {
            Method = method;
            _name = name;
            SupportsPrefixRouting = supportsPrefixRouting;
            _callOrder = callOrder;
            _callback = callback;
        }

        public string Method { get; }

        public bool SupportsPrefixRouting { get; }

        public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        {
            _callOrder?.Add(_name);
            cancellationToken.ThrowIfCancellationRequested();
            return _callback(request);
        }
    }
}
