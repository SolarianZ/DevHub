namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// RPC Router 测试故障注入行为。
/// </summary>
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
    public async Task Impl_RouteAsync_WhenLongerPrefixRegisteredLater_ShouldPreferLongerPrefixFirst()
    {
        var callOrder = new List<string>();
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            callOrder,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var mediumHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            callOrder,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var specificHandler = new TestHandler(
            "hub.apps.instances",
            "hub.apps.instances",
            callOrder,
            request => Task.FromResult(Success(request.Id, "hub.apps.instances")));

        var router = new RpcRouter([broadHandler, mediumHandler, specificHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-order",
            Method = "hub.apps.instances.list",
            Params = null
        }, CancellationToken.None);

        AssertHandledBy(response, "hub.apps.instances");
        Assert.Equal(["hub.apps.instances"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenSamePrefixReturnsMethodNotFound_ShouldFallbackInRegistrationOrder()
    {
        var callOrder = new List<string>();
        var firstHandler = new TestHandler(
            "hub.apps",
            "first",
            callOrder,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var secondHandler = new TestHandler(
            "hub.apps",
            "second",
            callOrder,
            request => Task.FromResult(Success(request.Id, "second")));

        var router = new RpcRouter([firstHandler, secondHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-fallback",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        AssertHandledBy(response, "second");
        Assert.Equal(["first", "second"], callOrder);
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
    public async Task Impl_RouteAsync_WhenMatchedPrefixHandlerThrowsAndOthersOnlyReturnMethodNotFound_ShouldReturnInternalError()
    {
        var callOrder = new List<string>();
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            callOrder,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var failingHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            callOrder,
            _ => throw new InvalidOperationException("boom"));

        var router = new RpcRouter([broadHandler, failingHandler], Mock.Of<ILogger<RpcRouter>>());

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "prefix-exception",
            Method = "hub.apps.listDefinitions",
            Params = null
        }, CancellationToken.None);

        AssertInternalError(response, "prefix-exception");
        Assert.Equal(["hub.apps", "hub"], callOrder);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenMatchedPrefixHandlersOnlyReturnMethodNotFound_ShouldReturnMethodNotFound()
    {
        var callOrder = new List<string>();
        var broadHandler = new TestHandler(
            "hub",
            "hub",
            callOrder,
            request => Task.FromResult(MethodNotFound(request.Id)));
        var specificHandler = new TestHandler(
            "hub.apps",
            "hub.apps",
            callOrder,
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
        Assert.Equal(["hub.apps", "hub"], callOrder);
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
        private readonly List<string> _callOrder;
        private readonly Func<JsonRpcRequest, Task<JsonRpcResponse>> _callback;

        public TestHandler(
            string method,
            string name,
            List<string> callOrder,
            Func<JsonRpcRequest, Task<JsonRpcResponse>> callback)
        {
            Method = method;
            _name = name;
            _callOrder = callOrder;
            _callback = callback;
        }

        public string Method { get; }

        public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        {
            _callOrder.Add(_name);
            cancellationToken.ThrowIfCancellationRequested();
            return _callback(request);
        }
    }
}
