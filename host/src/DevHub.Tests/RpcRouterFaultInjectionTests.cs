namespace DevHub.Tests;

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
        using var scope = new EnvironmentVariableScope(RpcTestFaultInjectionPolicy.ForceInternalErrorMethodsEnvironmentVariable, null);
        var policy = RpcTestFaultInjectionPolicy.Resolve(Mock.Of<ILogger<RpcTestFaultInjectionPolicy>>());
        var router = new RpcRouter([], Mock.Of<ILogger<RpcRouter>>(), policy);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "fault-disabled",
            Method = "hub.test.internalError",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error!.Code);
        Assert.Equal("method_not_found", response.Error.Message);
    }

    [Fact]
    public async Task Impl_RouteAsync_WhenFaultInjectionEnabledForMethod_ShouldReturnInternalError()
    {
        using var scope = new EnvironmentVariableScope(
            RpcTestFaultInjectionPolicy.ForceInternalErrorMethodsEnvironmentVariable,
            "hub.test.internalError");
        var policy = RpcTestFaultInjectionPolicy.Resolve(Mock.Of<ILogger<RpcTestFaultInjectionPolicy>>());
        var router = new RpcRouter([], Mock.Of<ILogger<RpcRouter>>(), policy);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "fault-enabled",
            Method = "hub.test.internalError",
            Params = null
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32603, response.Error!.Code);
        Assert.Equal("internal_error", response.Error.Message);
        Assert.Equal("fault-enabled", response.Id);
    }

    [Fact]
    public void Impl_ParseConfiguredMethods_WhenConfigured_ShouldNormalizeAndDeduplicate()
    {
        var methods = RpcTestFaultInjectionPolicy.ParseConfiguredMethods(
            " hub.test.internalError ; hub.ping,\r\nhub.test.internalError ");

        Assert.Equal(
            ["hub.ping", "hub.test.internalError"],
            methods.OrderBy(static method => method, StringComparer.Ordinal).ToArray());
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
}
