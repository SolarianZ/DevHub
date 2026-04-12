using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Services;

namespace DevHub.Host.Transport;

/// <summary>
/// AppDefinition 传输层解析器。
/// </summary>
internal static class AppDefinitionTransportParser
{
    internal static bool TryParse(
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
        if (appId is not null && !AppDefinitionValidator.IsValidAppId(appId))
        {
            issues.Add(CreateIssue("definition.appId", "invalid_app_id", "appId must match ^[a-z0-9][a-z0-9.-]*$"));
        }

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

        if (launchElement.ValueKind != JsonValueKind.Object)
        {
            issues.Add(CreateIssue("definition.launch", "invalid_field_type", "launch must be an object"));
            return null;
        }

        var exePath = ReadRequiredString(
            launchElement,
            "exePath",
            issues,
            "definition.launch.exePath",
            "missing_launch_exe_path",
            "launch.exePath is required when launch is provided");

        var argsTemplate = ReadOptionalString(launchElement, "argsTemplate", issues, "definition.launch.argsTemplate");
        var workingDirectory = ReadOptionalString(launchElement, "workingDirectory", issues, "definition.launch.workingDirectory");
        var dedupeKeyTemplate = ReadOptionalString(launchElement, "dedupeKeyTemplate", issues, "definition.launch.dedupeKeyTemplate");

        if (issues.Count > 0 && exePath is null)
        {
            return null;
        }

        return new LaunchConfiguration
        {
            ExePath = exePath,
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
        string missingMessage)
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
        if (string.IsNullOrWhiteSpace(value))
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
