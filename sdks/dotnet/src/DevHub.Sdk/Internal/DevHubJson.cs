using System.Collections;
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
        return JsonConvert.SerializeObject(NormalizeForSerialization(value), SerializerSettings);
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
        return NormalizeForSerialization(value) switch
        {
            null => JValue.CreateNull(),
            JToken token => token,
            var normalized => JToken.FromObject(normalized, CreateSerializer())
        };
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
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = false
                }
            },
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

    private static object? NormalizeForSerialization(object? value)
    {
        if (value is not null && TryConvertSystemTextJsonValue(value, out var jsonToken))
        {
            return jsonToken;
        }

        if (value is null)
        {
            return null;
        }

        if (value is JToken token)
        {
            return token.DeepClone();
        }

        if (value is IDictionary dictionary)
        {
            return NormalizeDictionary(dictionary);
        }

        if (value is IEnumerable enumerable && value is not string && !(value is byte[]))
        {
            return NormalizeEnumerable(enumerable);
        }

        return value;
    }

    private static IDictionary<object, object?> NormalizeDictionary(IDictionary dictionary)
    {
        var normalized = new Dictionary<object, object?>();
        foreach (DictionaryEntry entry in dictionary)
        {
            normalized[entry.Key] = NormalizeForSerialization(entry.Value);
        }

        return normalized;
    }

    private static IList<object?> NormalizeEnumerable(IEnumerable enumerable)
    {
        var normalized = new List<object?>();
        foreach (var item in enumerable)
        {
            normalized.Add(NormalizeForSerialization(item));
        }

        return normalized;
    }

    private static bool TryConvertSystemTextJsonValue(object value, out JToken token)
    {
        var type = value.GetType();
        if (string.Equals(type.FullName, "System.Text.Json.JsonDocument", StringComparison.Ordinal))
        {
            var rootElement = type.GetProperty("RootElement")?.GetValue(value);
            if (rootElement is null)
            {
                token = JValue.CreateNull();
                return true;
            }

            return TryConvertSystemTextJsonValue(rootElement, out token);
        }

        if (!string.Equals(type.FullName, "System.Text.Json.JsonElement", StringComparison.Ordinal))
        {
            token = null!;
            return false;
        }

        var valueKind = type.GetProperty("ValueKind")?.GetValue(value)?.ToString();
        if (string.Equals(valueKind, "Undefined", StringComparison.Ordinal))
        {
            token = JValue.CreateNull();
            return true;
        }

        var rawText = type.GetMethod("GetRawText", Array.Empty<Type>())?.Invoke(value, Array.Empty<object>()) as string
            ?? throw new JsonException("无法读取 System.Text.Json JSON 值。");

        token = ParseToken(rawText);
        return true;
    }
}
