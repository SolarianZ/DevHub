using System.Text.Json;
using DevHub.Core.Models;

namespace DevHub.Core.Services;

/// <summary>
/// AppDefinition 解析与校验器。
/// </summary>
public sealed class AppDefinitionValidator
{
    /// <summary>
    /// 判断 appId 是否满足协议格式要求。
    /// </summary>
    /// <param name="appId">待校验的应用标识。</param>
    /// <returns>格式合法时返回 <c>true</c>。</returns>
    public static bool IsValidAppId(string? appId)
    {
        return ProtocolIdentifier.IsValidAppId(appId);
    }

    /// <summary>
    /// 解析并校验候选定义。
    /// </summary>
    /// <param name="definitionElement">定义 JSON 对象。</param>
    /// <param name="definition">校验成功时返回解析后的定义。</param>
    /// <param name="validationResult">结构化校验结果。</param>
    /// <returns>校验通过返回 <c>true</c>。</returns>
    public bool TryParseAndValidate(
        JsonElement definitionElement,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult)
    {
        var issues = new List<ValidationIssue>();
        definition = null;

        if (definitionElement.ValueKind != JsonValueKind.Object)
        {
            issues.Add(CreateIssue("definition", "invalid_definition", "definition must be an object"));
            validationResult = BuildValidationResult(issues);
            return false;
        }

        var appId = ReadRequiredString(definitionElement, "appId", issues, "definition.appId", "missing_app_id", "appId is required");
        if (appId is not null && !IsValidAppId(appId))
        {
            issues.Add(CreateIssue("definition.appId", "invalid_app_id", $"appId must match {ProtocolIdentifier.CanonicalPattern}"));
        }

        var scope = ReadRequiredScope(definitionElement, issues);

        var displayName = ReadRequiredString(definitionElement, "displayName", issues, "definition.displayName", "missing_display_name", "displayName is required");
        var description = ReadOptionalString(definitionElement, "description", issues, "definition.description");
        var launch = ReadLaunchConfiguration(definitionElement, issues);
        var capabilities = ReadCapabilities(definitionElement, issues);

        validationResult = BuildValidationResult(issues);
        if (!validationResult.Valid)
        {
            return false;
        }

        definition = new AppDefinition
        {
            AppId = appId!,
            Scope = scope!,
            DisplayName = displayName!,
            Description = description,
            Launch = launch,
            Capabilities = capabilities
        };

        return true;
    }

    private static AppDefinitionValidationResult BuildValidationResult(IReadOnlyList<ValidationIssue> issues)
    {
        return new AppDefinitionValidationResult
        {
            Valid = issues.Count == 0,
            Errors = issues.ToArray()
        };
    }

    private static LaunchConfiguration? ReadLaunchConfiguration(JsonElement definitionElement, ICollection<ValidationIssue> issues)
    {
        if (!definitionElement.TryGetProperty("launch", out var launchElement))
        {
            return null;
        }

        if (launchElement.ValueKind == JsonValueKind.Null)
        {
            issues.Add(CreateIssue("definition.launch", "invalid_field_type", "launch must be an object"));
            return null;
        }

        if (launchElement.ValueKind != JsonValueKind.Object)
        {
            issues.Add(CreateIssue("definition.launch", "invalid_field_type", "launch must be an object"));
            return null;
        }

        var exePath = ReadOptionalString(launchElement, "exePath", issues, "definition.launch.exePath");
        var args = ReadOptionalStringArray(launchElement, "args", issues, "definition.launch.args");
        var argsTemplate = ReadOptionalString(launchElement, "argsTemplate", issues, "definition.launch.argsTemplate");
        var workingDirectory = ReadOptionalString(launchElement, "workingDirectory", issues, "definition.launch.workingDirectory");
        var dedupeKeyTemplate = ReadOptionalString(launchElement, "dedupeKeyTemplate", issues, "definition.launch.dedupeKeyTemplate");

        return new LaunchConfiguration
        {
            ExePath = exePath,
            Args = args,
            ArgsTemplate = argsTemplate,
            WorkingDirectory = workingDirectory,
            DedupeKeyTemplate = dedupeKeyTemplate
        };
    }

    private static AppCapabilities? ReadCapabilities(JsonElement definitionElement, ICollection<ValidationIssue> issues)
    {
        if (!definitionElement.TryGetProperty("capabilities", out var capabilitiesElement))
        {
            return null;
        }

        if (capabilitiesElement.ValueKind == JsonValueKind.Null)
        {
            issues.Add(CreateIssue("definition.capabilities", "invalid_field_type", "capabilities must be an object"));
            return null;
        }

        if (capabilitiesElement.ValueKind != JsonValueKind.Object)
        {
            issues.Add(CreateIssue("definition.capabilities", "invalid_field_type", "capabilities must be an object"));
            return null;
        }

        var rpc = ReadOptionalBoolean(capabilitiesElement, "rpc", issues, "definition.capabilities.rpc");
        var events = ReadOptionalBoolean(capabilitiesElement, "events", issues, "definition.capabilities.events");

        return new AppCapabilities
        {
            Rpc = rpc,
            Events = events
        };
    }

    private static string? ReadRequiredString(
        JsonElement element,
        string propertyName,
        ICollection<ValidationIssue> issues,
        string path,
        string code,
        string missingMessage,
        bool allowWhiteSpace = false)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            issues.Add(CreateIssue(path, code, missingMessage));
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be a string"));
            return null;
        }

        var value = property.GetString();
        if (value is null || (!allowWhiteSpace && string.IsNullOrWhiteSpace(value)))
        {
            issues.Add(CreateIssue(path, code, missingMessage));
            return null;
        }

        return value;
    }

    private static string? ReadOptionalString(
        JsonElement element,
        string propertyName,
        ICollection<ValidationIssue> issues,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be a string"));
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be a string"));
            return null;
        }

        return property.GetString();
    }

    private static List<string>? ReadOptionalStringArray(
        JsonElement element,
        string propertyName,
        ICollection<ValidationIssue> issues,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be an array of strings"));
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be an array of strings"));
            return null;
        }

        var values = new List<string>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(CreateIssue($"{path}[{index}]", "invalid_field_type", $"{propertyName} items must be strings"));
                return null;
            }

            values.Add(item.GetString() ?? string.Empty);
            index += 1;
        }

        return values;
    }

    private static string? ReadRequiredScope(
        JsonElement element,
        ICollection<ValidationIssue> issues)
    {
        if (!element.TryGetProperty("scope", out var scopeProperty))
        {
            issues.Add(CreateIssue("definition.scope", "missing_scope", "scope is required"));
            return null;
        }

        if (scopeProperty.ValueKind != JsonValueKind.String)
        {
            issues.Add(CreateIssue("definition.scope", "invalid_field_type", "scope must be a string"));
            return null;
        }

        var scope = scopeProperty.GetString();
        if (!ProtocolIdentifier.IsValidScope(scope))
        {
            issues.Add(CreateIssue("definition.scope", "invalid_scope", $"scope must be \"\" or match {ProtocolIdentifier.CanonicalPattern}"));
            return null;
        }

        return scope;
    }

    private static bool? ReadOptionalBoolean(
        JsonElement element,
        string propertyName,
        ICollection<ValidationIssue> issues,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            issues.Add(CreateIssue(path, "invalid_field_type", $"{propertyName} must be a boolean"));
            return null;
        }

        return property.GetBoolean();
    }

    private static ValidationIssue CreateIssue(string path, string code, string message)
    {
        return new ValidationIssue
        {
            Path = path,
            Code = code,
            Message = message
        };
    }
}
