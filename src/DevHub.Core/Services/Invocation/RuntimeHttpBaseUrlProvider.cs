using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// 运行时 HTTP 基础地址提供器。
/// </summary>
public interface IRuntimeHttpBaseUrlProvider
{
    /// <summary>
    /// 获取当前 Hub 的 HTTP 基础地址。
    /// </summary>
    /// <remarks>
    /// 返回值用于 launch 模板替换中的 <c>{httpBaseUrl}</c> 占位符。
    /// 读取失败时必须返回空字符串，调用方按降级语义继续执行。
    /// </remarks>
    /// <returns>若无法读取则返回空字符串。</returns>
    string GetHttpBaseUrl();
}

/// <summary>
/// 从运行时发现文件 hub.json 读取 HTTP 基础地址。
/// </summary>
public class RuntimeHttpBaseUrlProvider : IRuntimeHttpBaseUrlProvider
{
    private readonly string _hubJsonPath;
    private readonly ILogger<RuntimeHttpBaseUrlProvider> _logger;

    /// <summary>
    /// 使用统一路径选项初始化运行时 HTTP 基础地址提供器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    public RuntimeHttpBaseUrlProvider(ILogger<RuntimeHttpBaseUrlProvider> logger, RuntimePathOptions runtimePathOptions)
    {
        _hubJsonPath = runtimePathOptions.HubJsonPath;
        _logger = logger;
    }

    /// <inheritdoc />
    public string GetHttpBaseUrl()
    {
        try
        {
            if (!File.Exists(_hubJsonPath))
            {
                return string.Empty;
            }

            using var stream = File.OpenRead(_hubJsonPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("httpBaseUrl", out var httpBaseUrlElement) ||
                httpBaseUrlElement.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var httpBaseUrl = httpBaseUrlElement.GetString();
            if (string.IsNullOrWhiteSpace(httpBaseUrl))
            {
                return string.Empty;
            }

            return httpBaseUrl.TrimEnd('/');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 hub.json 的 httpBaseUrl 失败，路径: {HubJsonPath}", _hubJsonPath);
            return string.Empty;
        }
    }
}
