using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// 提供 RPC 日志输出所需的 JSON 脱敏序列化能力。
/// </summary>
internal static class RpcLogJsonSerializer
{
    private const string RedactedValue = "<redacted>";

    internal static string Serialize(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var element = value switch
        {
            JsonElement jsonElement => jsonElement.Clone(),
            JsonDocument jsonDocument => jsonDocument.RootElement.Clone(),
            _ => JsonSerializer.SerializeToElement(value, value.GetType())
        };

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        WriteSanitizedValue(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSanitizedValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                    {
                        writer.WriteStringValue(RedactedValue);
                        continue;
                    }

                    WriteSanitizedValue(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitizedValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                return;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                return;
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        return string.Equals(propertyName, "password", StringComparison.Ordinal)
            || string.Equals(propertyName, "instanceSessionToken", StringComparison.Ordinal);
    }
}
