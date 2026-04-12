using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Sdk.Internal;

internal static class DevHubJson
{
    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    internal static JsonElement? Clone(JsonElement? element)
    {
        return element?.Clone();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
