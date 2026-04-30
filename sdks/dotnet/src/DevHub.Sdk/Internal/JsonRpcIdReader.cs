using System.Globalization;
using System.Text.Json;

namespace DevHub.Sdk.Internal;

internal static class JsonRpcIdReader
{
    internal static string ReadRequiredResponseId(JsonElement root, string location)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            throw new InvalidOperationException($"{location} 缺少 id 字段。");
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => ReadSupportedNumericId(idElement, location),
            _ => throw new InvalidOperationException($"{location} 的 id 类型非法。")
        };
    }

    private static string ReadSupportedNumericId(JsonElement idElement, string location)
    {
        if (idElement.TryGetInt64(out var int64Value))
        {
            return int64Value.ToString(CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"{location} 的 id 数值非法：必须为 Int64 范围内整数。");
    }
}
