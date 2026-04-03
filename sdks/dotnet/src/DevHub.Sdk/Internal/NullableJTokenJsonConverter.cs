using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Internal;

internal sealed class NullableJTokenJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return typeof(JToken).IsAssignableFrom(objectType);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var token = JToken.ReadFrom(reader);
        return token.Type == JTokenType.Null ? null : token;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        if (value is JToken token)
        {
            token.WriteTo(writer);
            return;
        }

        throw new JsonSerializationException("值必须为 JToken 或 null。");
    }
}
