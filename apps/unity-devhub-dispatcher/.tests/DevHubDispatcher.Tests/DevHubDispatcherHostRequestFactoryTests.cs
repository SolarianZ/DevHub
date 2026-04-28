using DevHub.Sdk.Models;
using DevHubDispatcher.Editor;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Tests;

public sealed class DevHubDispatcherHostRequestFactoryTests
{
    [Fact]
    public void AppContractFactory_BuildAppInstanceRegistration_ShouldUseGlobalScopeAndKeepProjectPathInMeta()
    {
        var registration = DevHubDispatcherAppContractFactory.BuildAppInstanceRegistration(
            "unity-editor-instance",
            "Sample_App.v2",
            "/Projects/My Unity Project",
            "2019.4.40f1",
            "/Applications/Unity/Unity.app",
            1234);

        Assert.Equal("unity-editor-instance", registration.InstanceId);
        Assert.Equal("Sample_App.v2", registration.AppId);
        Assert.Equal(string.Empty, registration.Scope);
        Assert.Equal(1234, registration.Pid);
        Assert.NotNull(registration.Invoke);
        Assert.True(registration.Invoke.Poll);
        Assert.True(registration.Invoke.Respond);

        var meta = Assert.IsType<JObject>(registration.Meta);
        Assert.Equal("/Projects/My Unity Project", (string)meta["projectPath"]);
        Assert.Equal("2019.4.40f1", (string)meta["unityVersion"]);
        Assert.Equal("/Applications/Unity/Unity.app", (string)meta["unityEditorPath"]);
        Assert.Equal("devhub.dispatcher", (string)meta["dispatcherPackage"]);
        Assert.Equal("unity-editor", (string)meta["dispatcherRole"]);
    }

    [Fact]
    public void HostRequestFactory_TryBuildInvokeRequest_WhenOptionsProvided_ShouldMapToSdkContract()
    {
        var payload = new JObject
        {
            ["count"] = 1
        };
        var envelope = DevHubDispatcherHostRequestFactory.BuildEnvelope("sample-tool", payload);
        payload["count"] = 2;

        var success = DevHubDispatcherHostRequestFactory.TryBuildInvokeRequest(
            "Sample_App.v2",
            "sample.echo",
            envelope,
            new DevHubDispatcherSendOptions
            {
                Scope = "Project.Scope",
                InstanceId = "Instance_1",
                TtlMs = 1234,
                WaitTimeoutMs = 5678,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            out var request,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("Sample_App.v2", request.AppId);
        Assert.Equal("sample.echo", request.Method);

        var args = Assert.IsType<JObject>(request.Args);
        Assert.Equal("sample-tool", (string)args["toolId"]);
        Assert.Equal(1, (int)args["payload"]!["count"]!);

        Assert.NotNull(request.Target);
        Assert.Equal("Project.Scope", request.Target.Scope);
        Assert.Equal("Instance_1", request.Target.InstanceId);

        Assert.NotNull(request.Options);
        Assert.Equal(1234, request.Options.TtlMs);
        Assert.Equal(5678, request.Options.WaitTimeoutMs);
        Assert.True(request.Options.QueueIfOffline);
        Assert.False(request.Options.AutoLaunch);
    }

    [Fact]
    public void HostRequestFactory_TryBuildInvokeRequest_WhenAppIdInvalid_ShouldReturnFailureWithoutThrowing()
    {
        var success = DevHubDispatcherHostRequestFactory.TryBuildInvokeRequest(
            ".sample",
            "sample.echo",
            new JObject(),
            null,
            out InvokeRequest request,
            out string error);

        Assert.False(success);
        Assert.Null(request);
        Assert.Contains("appId", error, StringComparison.OrdinalIgnoreCase);
    }
}
