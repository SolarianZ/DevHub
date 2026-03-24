namespace DevHub.Host.Tests.TestHelpers;

using System.Text.Json;

/// <summary>
/// JSON-RPC 测试消息辅助工具。
/// </summary>
internal static class JsonRpcTestMessageHelper
{
    /// <summary>
    /// 将测试对象序列化为 JSON 文本。
    /// </summary>
    /// <param name="payload">待序列化对象。</param>
    /// <returns>JSON 文本。</returns>
    internal static string CreateJson(object payload)
    {
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// 解析脚本化 WebSocket 已发送消息。
    /// </summary>
    /// <param name="socket">脚本化 WebSocket。</param>
    /// <returns>消息根元素列表。</returns>
    internal static List<JsonElement> ParseSentMessages(ScriptedWebSocket socket)
    {
        var messages = new List<JsonElement>();
        foreach (var text in socket.GetSentTextsSnapshot())
        {
            using var document = JsonDocument.Parse(text);
            messages.Add(document.RootElement.Clone());
        }

        return messages;
    }

    /// <summary>
    /// 按 JSON-RPC id 查找响应消息。
    /// </summary>
    /// <param name="messages">消息集合。</param>
    /// <param name="id">目标 id。</param>
    /// <returns>命中消息，否则返回 default。</returns>
    internal static JsonElement FindResponseById(IEnumerable<JsonElement> messages, string id)
    {
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(idProperty.GetString(), id, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return default;
    }
}
