using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DevHub.Sdk.Internal;

internal static class DevHubJson
{
    internal static JsonSerializerSettings SerializerSettings { get; } = CreateSerializerSettings();

    internal static JsonSerializer CreateSerializer()
    {
        return JsonSerializer.CreateDefault(SerializerSettings);
    }

    internal static JToken? Clone(JToken? token)
    {
        return token?.DeepClone();
    }

    internal static string Serialize(object? value)
    {
        return JsonConvert.SerializeObject(value, SerializerSettings);
    }

    internal static T? Deserialize<T>(string json)
    {
        CompatibilityGuards.ThrowIfNull(json, nameof(json));
        return JsonConvert.DeserializeObject<T>(json, SerializerSettings);
    }

    internal static T? Deserialize<T>(JToken token)
    {
        CompatibilityGuards.ThrowIfNull(token, nameof(token));
        return token.ToObject<T>(CreateSerializer());
    }

    internal static JToken SerializeToToken(object? value)
    {
        if (value is null)
        {
            return JValue.CreateNull();
        }

        if (value is JToken token)
        {
            return token.DeepClone();
        }

        return JToken.FromObject(value, CreateSerializer());
    }

    internal static JToken ParseToken(string json)
    {
        CompatibilityGuards.ThrowIfNull(json, nameof(json));

        using var stringReader = new StringReader(json);
        using var jsonReader = new JsonTextReader(stringReader)
        {
            DateParseHandling = DateParseHandling.None
        };

        return JToken.ReadFrom(jsonReader);
    }

    internal static JObject ParseObject(string json)
    {
        var token = ParseToken(json);
        if (token.Type != JTokenType.Object)
        {
            throw new JsonException("JSON 根必须为对象。");
        }

        return (JObject)token;
    }

    private static JsonSerializerSettings CreateSerializerSettings()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.None
        };

        settings.Converters.Add(new StringEnumConverter
        {
            CamelCaseText = true,
            AllowIntegerValues = false
        });

        return settings;
    }
}
