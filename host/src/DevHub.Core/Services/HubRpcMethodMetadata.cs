using System.Collections.Frozen;
using System.Reflection;

namespace DevHub.Core.Services;

/// <summary>
/// Hub RPC 方法所属类别。
/// </summary>
public enum HubRpcMethodCategory
{
    /// <summary>Hub 基础能力。</summary>
    Core,

    /// <summary>WebSocket 会话控制。</summary>
    WebSocketSession,

    /// <summary>Hub 事件订阅与投递。</summary>
    Events,

    /// <summary>应用定义管理。</summary>
    AppDefinitions,

    /// <summary>应用实例管理。</summary>
    AppInstances,

    /// <summary>应用启动编排。</summary>
    AppLaunch,

    /// <summary>Invocation 调用编排。</summary>
    Invocation
}

/// <summary>
/// Hub RPC 方法可用传输。
/// </summary>
[Flags]
public enum HubRpcMethodTransport
{
    /// <summary>未声明可用传输。</summary>
    None = 0,

    /// <summary>HTTP JSON-RPC 传输。</summary>
    Http = 1,

    /// <summary>WebSocket JSON-RPC 传输。</summary>
    WebSocket = 2
}

/// <summary>
/// 标记 Hub RPC 方法的传输、通知与类别能力。
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class HubRpcMethodAttribute : Attribute
{
    /// <summary>
    /// 初始化 Hub RPC 方法元数据。
    /// </summary>
    /// <param name="category">方法所属类别。</param>
    /// <param name="transports">方法支持的传输。</param>
    /// <param name="supportsNotification">是否支持 JSON-RPC notification。</param>
    /// <param name="clientCallable">是否允许客户端调用。</param>
    public HubRpcMethodAttribute(
        HubRpcMethodCategory category,
        HubRpcMethodTransport transports,
        bool supportsNotification,
        bool clientCallable = true)
    {
        Category = category;
        Transports = transports;
        SupportsNotification = supportsNotification;
        ClientCallable = clientCallable;
    }

    /// <summary>
    /// 方法所属类别。
    /// </summary>
    public HubRpcMethodCategory Category { get; }

    /// <summary>
    /// 方法支持的传输。
    /// </summary>
    public HubRpcMethodTransport Transports { get; }

    /// <summary>
    /// 是否支持 JSON-RPC notification。
    /// </summary>
    public bool SupportsNotification { get; }

    /// <summary>
    /// 是否允许客户端调用。
    /// </summary>
    public bool ClientCallable { get; }
}

/// <summary>
/// Hub RPC 方法元数据。
/// </summary>
/// <param name="Method">方法名。</param>
/// <param name="Category">方法所属类别。</param>
/// <param name="Transports">方法支持的传输。</param>
/// <param name="SupportsNotification">是否支持 JSON-RPC notification。</param>
/// <param name="ClientCallable">是否允许客户端调用。</param>
public sealed record HubRpcMethodDescriptor(
    string Method,
    HubRpcMethodCategory Category,
    HubRpcMethodTransport Transports,
    bool SupportsNotification,
    bool ClientCallable);

/// <summary>
/// Hub RPC 方法元数据注册表。
/// </summary>
public static class HubRpcMethodRegistry
{
    private static readonly Lazy<HubRpcMethodMetadataCache> Cache = new(BuildCache);

    /// <summary>
    /// 已声明的全部 Hub RPC 方法。
    /// </summary>
    public static IReadOnlySet<string> AllMethods => Cache.Value.AllMethods;

    /// <summary>
    /// 允许客户端调用的 Hub RPC 方法。
    /// </summary>
    public static IReadOnlySet<string> ClientMethods => Cache.Value.ClientMethods;

    /// <summary>
    /// 支持 JSON-RPC notification 的客户端方法。
    /// </summary>
    public static IReadOnlySet<string> NotificationMethods => Cache.Value.NotificationMethods;

    /// <summary>
    /// 支持 HTTP 传输的客户端方法。
    /// </summary>
    public static IReadOnlySet<string> HttpMethods => Cache.Value.HttpMethods;

    /// <summary>
    /// 支持 WebSocket 传输的客户端方法。
    /// </summary>
    public static IReadOnlySet<string> WebSocketMethods => Cache.Value.WebSocketMethods;

    /// <summary>
    /// 仅支持 HTTP 传输的客户端方法。
    /// </summary>
    public static IReadOnlySet<string> HttpOnlyMethods => Cache.Value.HttpOnlyMethods;

    /// <summary>
    /// 仅支持 WebSocket 传输的客户端方法。
    /// </summary>
    public static IReadOnlySet<string> WebSocketOnlyMethods => Cache.Value.WebSocketOnlyMethods;

    /// <summary>
    /// 尝试获取方法元数据。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <param name="descriptor">方法元数据。</param>
    /// <returns>找到元数据时返回 <see langword="true" />。</returns>
    public static bool TryGetDescriptor(string method, out HubRpcMethodDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return Cache.Value.DescriptorsByMethod.TryGetValue(method, out descriptor!);
    }

    /// <summary>
    /// 判断是否为已知客户端方法。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <returns>已知客户端方法返回 <see langword="true" />。</returns>
    public static bool IsKnownClientMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return ClientMethods.Contains(method);
    }

    /// <summary>
    /// 判断方法是否支持 JSON-RPC notification。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <returns>支持 notification 返回 <see langword="true" />。</returns>
    public static bool SupportsNotification(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return NotificationMethods.Contains(method);
    }

    private static HubRpcMethodMetadataCache BuildCache()
    {
        var descriptors = typeof(HubRpcMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(static field => BuildDescriptor(field))
            .ToArray();

        var descriptorsByMethod = descriptors.ToFrozenDictionary(
            static descriptor => descriptor.Method,
            static descriptor => descriptor,
            StringComparer.Ordinal);

        var clientMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable)
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        var notificationMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable && descriptor.SupportsNotification)
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        var httpMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable && descriptor.Transports.HasFlag(HubRpcMethodTransport.Http))
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        var webSocketMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable && descriptor.Transports.HasFlag(HubRpcMethodTransport.WebSocket))
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        var httpOnlyMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable && descriptor.Transports == HubRpcMethodTransport.Http)
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        var webSocketOnlyMethods = descriptors
            .Where(static descriptor => descriptor.ClientCallable && descriptor.Transports == HubRpcMethodTransport.WebSocket)
            .Select(static descriptor => descriptor.Method)
            .ToFrozenSet(StringComparer.Ordinal);

        return new HubRpcMethodMetadataCache(
            descriptorsByMethod.Keys.ToFrozenSet(StringComparer.Ordinal),
            clientMethods,
            notificationMethods,
            httpMethods,
            webSocketMethods,
            httpOnlyMethods,
            webSocketOnlyMethods,
            descriptorsByMethod);
    }

    private static HubRpcMethodDescriptor BuildDescriptor(FieldInfo field)
    {
        var method = (string?)field.GetRawConstantValue();
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new InvalidOperationException($"Hub RPC 方法常量 {field.Name} 必须是非空字符串。");
        }

        var attribute = field.GetCustomAttribute<HubRpcMethodAttribute>()
            ?? throw new InvalidOperationException($"Hub RPC 方法常量 {field.Name} 必须声明 {nameof(HubRpcMethodAttribute)}。");

        return new HubRpcMethodDescriptor(
            method,
            attribute.Category,
            attribute.Transports,
            attribute.SupportsNotification,
            attribute.ClientCallable);
    }

    private sealed record HubRpcMethodMetadataCache(
        IReadOnlySet<string> AllMethods,
        IReadOnlySet<string> ClientMethods,
        IReadOnlySet<string> NotificationMethods,
        IReadOnlySet<string> HttpMethods,
        IReadOnlySet<string> WebSocketMethods,
        IReadOnlySet<string> HttpOnlyMethods,
        IReadOnlySet<string> WebSocketOnlyMethods,
        IReadOnlyDictionary<string, HubRpcMethodDescriptor> DescriptorsByMethod);
}
