namespace DevHub.Host.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;

/// <summary>
/// 共享参数读取与定义校验 helper 白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class TransportAdapterImplTests
{
    private readonly AppDefinitionValidator _validator = new();

    [Fact]
    [Trait("SpecRef", "5.1.2")]
    public void Impl_AppDefinitionValidator_WhenRootIsNotObject_ShouldReturnInvalidDefinitionIssue()
    {
        var ok = _validator.TryParseAndValidate(ParseElement("\"bad\""), out var definition, out var validationResult);

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
    public void Impl_AppDefinitionValidator_WhenDefinitionContainsInvalidNestedFields_ShouldCollectValidationIssues()
    {
        var ok = _validator.TryParseAndValidate(
            ParseElement(
                """
                {
                  "appId": "Bad App",
                  "displayName": "   ",
                  "description": null,
                  "launch": {
                    "args": [1],
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
        Assert.DoesNotContain(validationResult.Errors, issue => issue.Path == "definition.launch.exePath");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.args[0]" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.argsTemplate" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.workingDirectory" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.launch.dedupeKeyTemplate" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.capabilities.rpc" && issue.Code == "invalid_field_type");
        Assert.Contains(validationResult.Errors, issue => issue.Path == "definition.capabilities.events" && issue.Code == "invalid_field_type");
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    public void Impl_AppDefinitionValidator_WhenLaunchOrCapabilitiesAreNotObjects_ShouldReturnInvalidFieldType()
    {
        var ok = _validator.TryParseAndValidate(
            ParseElement(
                """
                {
                  "appId": "transport.parser",
                  "scope": "",
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
    public void Impl_AppDefinitionValidator_WhenDefinitionValid_ShouldParseModel()
    {
        var ok = _validator.TryParseAndValidate(
            ParseElement(
                """
                {
                  "appId": "transport.parser",
                  "scope": "",
                  "displayName": "Transport Parser",
                  "description": "adapter test",
                  "launch": {
                    "exePath": "dotnet",
                    "args": ["--app", "{appId}"],
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
        Assert.Equal(new[] { "--app", "{appId}" }, definition.Launch.Args);
        Assert.Equal("--info", definition.Launch.ArgsTemplate);
        Assert.Equal("/tmp/devhub", definition.Launch.WorkingDirectory);
        Assert.Equal("{appId}:{scopeOrGlobal}", definition.Launch.DedupeKeyTemplate);
        Assert.False(definition.Capabilities!.Rpc);
        Assert.True(definition.Capabilities.Events);
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    public void Impl_AppDefinitionValidator_WhenLaunchExePathBlank_ShouldParseModelAndPreserveValue()
    {
        var ok = _validator.TryParseAndValidate(
            ParseElement(
                """
                {
                  "appId": "transport.blank-launch",
                  "scope": "",
                  "displayName": "Blank Launch",
                  "launch": {
                    "exePath": "   ",
                    "argsTemplate": "--info"
                  }
                }
                """),
            out var definition,
            out var validationResult);

        Assert.True(ok);
        Assert.True(validationResult.Valid);
        Assert.NotNull(definition);
        Assert.Equal("   ", definition.Launch!.ExePath);
    }

    [Fact]
    [Trait("SpecRef", "5.1.1")]
    public void Impl_AppDefinitionValidator_WhenLaunchExePathMissing_ShouldParseLaunchModel()
    {
        var ok = _validator.TryParseAndValidate(
            ParseElement(
                """
                {
                  "appId": "transport.optional-launch",
                  "scope": "",
                  "displayName": "Optional Launch",
                  "launch": {
                    "args": ["--mode", "manual"]
                  }
                }
                """),
            out var definition,
            out var validationResult);

        Assert.True(ok);
        Assert.True(validationResult.Valid);
        Assert.NotNull(definition);
        Assert.Null(definition.Launch!.ExePath);
        Assert.Equal(new[] { "--mode", "manual" }, definition.Launch.Args);
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Impl_RpcParamReader_StringHelpers_ShouldFollowObjectAndWhitespaceRules()
    {
        var request = new JsonRpcRequest
        {
            Id = "reader-request",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = ParseElement("""{ "appId": "reader.app", "dedupeKey": null, "bad": 1 }""")
        };

        var paramsOk = RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams);
        var badParamsOk = RpcParamReader.TryReadParamsObject(
            new JsonRpcRequest
            {
                Id = "reader-request-bad",
                Method = HubRpcMethods.HubAppsGetDefinition,
                Params = ParseElement("""["bad"]""")
            },
            out _,
            out var badParamsError);

        var requiredOk = RpcParamReader.TryGetRequiredString(paramsElement, "appId", out var appId);
        var requiredWhitespaceOk = RpcParamReader.TryGetRequiredString(
            ParseElement("""{ "appId": "   " }"""),
            "appId",
            out _);
        var optionalMissingOk = RpcParamReader.TryGetOptionalString(paramsElement, "missing", out var missingValue);
        var optionalNullOk = RpcParamReader.TryGetOptionalString(paramsElement, "dedupeKey", out var nullValue);
        var optionalBadTypeOk = RpcParamReader.TryGetOptionalString(paramsElement, "bad", out _);

        Assert.True(paramsOk);
        Assert.Null(invalidParams);
        Assert.False(badParamsOk);
        Assert.Equal(-32602, badParamsError.Error!.Code);
        Assert.Equal("invalid_params", badParamsError.Error.Message);
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
    public void Impl_RpcParamReader_ScopeHelpers_ShouldRequireExplicitScopeAndSeparateListFilters()
    {
        var requiredMissingOk = RpcParamReader.TryGetRequiredScope(
            ParseElement("""{}"""),
            "scope",
            "invalid_scope",
            out _,
            out var requiredMissingError);

        var emptyOk = RpcParamReader.TryGetRequiredScope(
            ParseElement("""{ "scope": "" }"""),
            "scope",
            "invalid_scope",
            out var emptyScope,
            out _);

        var listFilterNullOk = RpcParamReader.TryGetRequiredListScope(
            ParseElement("""{ "scope": null }"""),
            "scope",
            "invalid_scope",
            out var listFilterScope,
            out var listFilterError);

        var invalidOk = RpcParamReader.TryGetRequiredScope(
            ParseElement("""{ "scope": 1 }"""),
            "scope",
            "invalid_scope",
            out _,
            out var invalidError);

        Assert.False(requiredMissingOk);
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(requiredMissingError).GetProperty("reason").GetString());
        Assert.True(emptyOk);
        Assert.Equal(string.Empty, emptyScope);
        Assert.True(listFilterNullOk);
        Assert.Null(listFilterScope);
        Assert.Null(listFilterError);
        Assert.False(invalidOk);

        var errorData = JsonSerializer.SerializeToElement(invalidError);
        Assert.Equal("invalid_scope", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "5.5")]
    [Trait("SpecRef", "6.3.13")]
    public void Impl_RpcParamReader_TryParseInvocationTarget_ShouldRequireTargetAndExplicitScope()
    {
        var missingTargetOk = RpcParamReader.TryParseInvocationTarget(
            ParseElement("""{}"""),
            out _,
            out var defaultError);

        var invalidTargetOk = RpcParamReader.TryParseInvocationTarget(
            ParseElement("""{ "target": 1 }"""),
            out _,
            out var invalidTargetError);

        var invalidScopeOk = RpcParamReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "scope": 1 } }"""),
            out _,
            out var invalidScopeError);

        var invalidInstanceOk = RpcParamReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "scope": "", "instanceId": "   " } }"""),
            out _,
            out var invalidInstanceError);

        var validTargetOk = RpcParamReader.TryParseInvocationTarget(
            ParseElement("""{ "target": { "scope": "tenant-a", "instanceId": "inst-1" } }"""),
            out var validTarget,
            out var validError);

        Assert.False(missingTargetOk);
        Assert.Equal("invalid_target", JsonSerializer.SerializeToElement(defaultError).GetProperty("reason").GetString());

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
    public void Impl_RpcParamReader_TryParseRespondError_ShouldValidateShapeAndDeserializeAnyJsonData()
    {
        Assert.False(RpcParamReader.TryParseRespondError(ParseElement("""1"""), out _));
        Assert.False(RpcParamReader.TryParseRespondError(ParseElement("""{ "message": "bad" }"""), out _));
        Assert.False(RpcParamReader.TryParseRespondError(ParseElement("""{ "code": 1001, "message": 1 }"""), out _));

        var ok = RpcParamReader.TryParseRespondError(
            ParseElement("""{ "code": 1001, "message": "app_error", "data": { "detail": "boom" } }"""),
            out var parsedError);

        Assert.True(ok);

        var payload = Assert.IsAssignableFrom<IDictionary<string, object?>>(parsedError);
        Assert.Equal(1001, payload["code"]);
        Assert.Equal("app_error", payload["message"]);

        var data = Assert.IsType<JsonElement>(payload["data"]);
        Assert.Equal("boom", data.GetProperty("detail").GetString());

        Assert.True(RpcParamReader.TryParseRespondError(
            ParseElement("""{ "code": 1002, "message": "app_error", "data": "boom" }"""),
            out var scalarError));
        var scalarPayload = Assert.IsAssignableFrom<IDictionary<string, object?>>(scalarError);
        var scalarData = Assert.IsType<JsonElement>(scalarPayload["data"]);
        Assert.Equal("boom", scalarData.GetString());

        Assert.True(RpcParamReader.TryParseRespondError(
            ParseElement("""{ "code": 1003, "message": "app_error", "data": null }"""),
            out var nullError));
        var nullPayload = Assert.IsAssignableFrom<IDictionary<string, object?>>(nullError);
        Assert.True(nullPayload.ContainsKey("data"));
        Assert.Null(nullPayload["data"]);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
