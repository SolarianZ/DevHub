namespace DevHub.Host.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Host.Transport;

/// <summary>
/// Host 适配层 transport 解析器白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class TransportAdapterImplTests
{
    [Fact]
    [Trait("SpecRef", "5.1.2")]
    public void Impl_AppDefinitionTransportParser_WhenRootIsNotObject_ShouldReturnInvalidDefinitionIssue()
    {
        var ok = AppDefinitionTransportParser.TryParse(ParseElement("\"bad\""), out var definition, out var validationResult);

        Assert.False(ok);
        Assert.Null(definition);
        Assert.False(validationResult.Valid);

        var issue = Assert.Single(validationResult.Errors);
        Assert.Equal("definition", issue.Path);
        Assert.Equal("invalid_definition", issue.Code);
        Assert.Equal("definition must be an object", issue.Message);
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    [Trait("SpecRef", "5.1.2")]
    public void Impl_AppDefinitionTransportParser_WhenDefinitionContainsInvalidNestedFields_ShouldCollectValidationIssues()
    {
        var ok = AppDefinitionTransportParser.TryParse(
            ParseElement(
                """
                {
                  "appId": "Bad App",
                  "displayName": "",
                  "description": null,
                  "launch": {
                    "argsTemplate": 1,
                    "workingDirectory": null,
                    "dedupeKeyTemplate": false
                  },
                  "capabilities": {
                    "rpc": "yes",
                    "events": 1
                  }
                }
                """),
            out var definition,
            out var validationResult);

        Assert.False(ok);
        Assert.Null(definition);
        Assert.False(validationResult.Valid);

        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.appId" && issue.Code == "invalid_app_id");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.displayName" && issue.Code == "missing_display_name");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.description" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.exePath" && issue.Code == "missing_launch_exe_path");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.argsTemplate" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.workingDirectory" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.dedupeKeyTemplate" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.capabilities.rpc" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.capabilities.events" && issue.Code == "invalid_field_type");
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    public void Impl_AppDefinitionTransportParser_WhenLaunchOrCapabilitiesAreNotObjects_ShouldReturnInvalidFieldType()
    {
        var ok = AppDefinitionTransportParser.TryParse(
            ParseElement(
                """
                {
                  "appId": "transport.parser",
                  "displayName": "Transport Parser",
                  "launch": null,
                  "capabilities": "bad"
                }
                """),
            out _,
            out var validationResult);

        Assert.False(ok);
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.capabilities" && issue.Code == "invalid_field_type");
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    public void Impl_AppDefinitionTransportParser_WhenDefinitionValid_ShouldParseModel()
    {
        var ok = AppDefinitionTransportParser.TryParse(
            ParseElement(
                """
                {
                  "appId": "transport.parser",
                  "displayName": "Transport Parser",
                  "description": "adapter test",
                  "launch": {
                    "exePath": "dotnet",
                    "argsTemplate": "--info",
                    "workingDirectory": "/tmp/devhub",
                    "dedupeKeyTemplate": "{appId}:{scopeOrGlobal}"
                  },
                  "capabilities": {
                    "rpc": false,
                    "events": true
                  }
                }
                """),
            out var definition,
            out var validationResult);

        Assert.True(ok);
        Assert.True(validationResult.Valid);
        Assert.NotNull(definition);
        Assert.Equal("transport.parser", definition.AppId);
        Assert.Equal("Transport Parser", definition.DisplayName);
        Assert.Equal("adapter test", definition.Description);
        Assert.Equal("dotnet", definition.Launch!.ExePath);
        Assert.Equal("--info", definition.Launch.ArgsTemplate);
        Assert.Equal("/tmp/devhub", definition.Launch.WorkingDirectory);
        Assert.Equal("{appId}:{scopeOrGlobal}", definition.Launch.DedupeKeyTemplate);
        Assert.False(definition.Capabilities!.Rpc);
        Assert.True(definition.Capabilities.Events);
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Impl_RpcRequestParameterReader_StringHelpers_ShouldFollowObjectAndWhitespaceRules()
    {
        var request = new JsonRpcRequest
        {
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = ParseElement("""{ "appId": "reader.app", "dedupeKey": null, "bad": 1 }""")
        };

        var paramsOk = RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement);
        var badParamsOk = RpcRequestParameterReader.TryReadParamsObject(
            new JsonRpcRequest
            {
                Method = HubRpcMethods.HubAppsGetDefinition,
                Params = ParseElement("""["bad"]""")
            },
            out _);

        var requiredOk = RpcRequestParameterReader.TryGetRequiredString(paramsElement, "appId", out var appId);
        var requiredWhitespaceOk = RpcRequestParameterReader.TryGetRequiredString(
            ParseElement("""{ "appId": "   " }"""),
            "appId",
            out _);
        var optionalMissingOk = RpcRequestParameterReader.TryGetOptionalString(paramsElement, "missing", out var missingValue);
        var optionalNullOk = RpcRequestParameterReader.TryGetOptionalString(paramsElement, "dedupeKey", out var nullValue);
        var optionalBadTypeOk = RpcRequestParameterReader.TryGetOptionalString(paramsElement, "bad", out _);

        Assert.True(paramsOk);
        Assert.False(badParamsOk);
        Assert.True(requiredOk);
        Assert.Equal("reader.app", appId);
        Assert.False(requiredWhitespaceOk);
        Assert.True(optionalMissingOk);
        Assert.Null(missingValue);
        Assert.True(optionalNullOk);
        Assert.Null(nullValue);
        Assert.False(optionalBadTypeOk);
    }

    [Fact]
    [Trait("SpecRef", "5.5")]
    public void Impl_RpcRequestParameterReader_TryGetOptionalScope_ShouldNormalizeEmptyStringAndRejectInvalidType()
    {
        var missingOk = RpcRequestParameterReader.TryGetOptionalScope(
            ParseElement("""{}"""),
            "scope",
            "invalid_scope",
            out var missingScope,
            out var missingError);

        var emptyOk = RpcRequestParameterReader.TryGetOptionalScope(
            ParseElement("""{ "scope": "" }"""),
            "scope",
            "invalid_scope",
            out var emptyScope,
            out _);

        var invalidOk = RpcRequestParameterReader.TryGetOptionalScope(
            ParseElement("""{ "scope": 1 }"""),
            "scope",
            "invalid_scope",
            out _,
            out var invalidError);

        Assert.True(missingOk);
        Assert.Null(missingScope);
        Assert.Null(missingError);
        Assert.True(emptyOk);
        Assert.Null(emptyScope);
        Assert.False(invalidOk);

        var errorData = JsonSerializer.SerializeToElement(invalidError);
        Assert.Equal("invalid_scope", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "5.5")]
    [Trait("SpecRef", "6.3.13")]
    public void Impl_RpcRequestParameterReader_TryParseInvocationTarget_ShouldHandleMissingTargetAndInvalidMembers()
    {
        var missingTargetOk = RpcRequestParameterReader.TryParseInvocationTarget(
            ParseElement("""{}"""),
            out var defaultTarget,
            out var defaultError);

        var invalidTargetOk = RpcRequestParameterReader.TryParseInvocationTarget(
            ParseElement("""{ "target": 1 }"""),
            out _,
            out var invalidTargetError);

        var invalidScopeOk = RpcRequestParameterReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "scope": 1 } }"""),
            out _,
            out var invalidScopeError);

        var invalidInstanceOk = RpcRequestParameterReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "instanceId": "   " } }"""),
            out _,
            out var invalidInstanceError);

        var validTargetOk = RpcRequestParameterReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "scope": "tenant-a", "instanceId": "inst-1" } }"""),
            out var validTarget,
            out var validError);

        Assert.True(missingTargetOk);
        Assert.Null(defaultTarget.Scope);
        Assert.Null(defaultTarget.InstanceId);
        Assert.Null(defaultError);

        Assert.False(invalidTargetOk);
        Assert.Equal("invalid_target", JsonSerializer.SerializeToElement(invalidTargetError).GetProperty("reason").GetString());

        Assert.False(invalidScopeOk);
        Assert.Equal("invalid_target_scope", JsonSerializer.SerializeToElement(invalidScopeError).GetProperty("reason").GetString());

        Assert.False(invalidInstanceOk);
        Assert.Equal("invalid_target_instance", JsonSerializer.SerializeToElement(invalidInstanceError).GetProperty("reason").GetString());

        Assert.True(validTargetOk);
        Assert.Null(validError);
        Assert.Equal("tenant-a", validTarget.Scope);
        Assert.Equal("inst-1", validTarget.InstanceId);
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public void Impl_RpcRequestParameterReader_TryParseRespondError_ShouldValidateShapeAndDeserializeDataObject()
    {
        Assert.False(RpcRequestParameterReader.TryParseRespondError(ParseElement("""1"""), out _));
        Assert.False(RpcRequestParameterReader.TryParseRespondError(ParseElement("""{ "message": "bad" }"""), out _));
        Assert.False(RpcRequestParameterReader.TryParseRespondError(ParseElement("""{ "code": 1001, "message": 1 }"""), out _));
        Assert.False(RpcRequestParameterReader.TryParseRespondError(ParseElement("""{ "code": 1001, "message": "bad", "data": "boom" }"""), out _));

        var ok = RpcRequestParameterReader.TryParseRespondError(
            ParseElement("""{ "code": 1001, "message": "app_error", "data": { "detail": "boom" } }"""),
            out var parsedError);

        Assert.True(ok);

        var payload = Assert.IsAssignableFrom<IDictionary<string, object?>>(parsedError);
        Assert.Equal(1001, payload["code"]);
        Assert.Equal("app_error", payload["message"]);

        var data = Assert.IsType<JsonElement>(payload["data"]);
        Assert.Equal("boom", data.GetProperty("detail").GetString());
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
