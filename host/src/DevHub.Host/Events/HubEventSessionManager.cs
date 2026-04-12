using System.Collections.Concurrent;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;

namespace DevHub.Host.Events;

/// <summary>
/// Host WebSocket 会话事件投递管理器。
/// </summary>
public sealed class HubEventSessionManager : IHubEventPublisher
{
    internal const int MaxPendingDeliveriesPerConnection = 1024;
    internal const int MaxPendingDeliveriesTotal = 16384;

    private static readonly string[] SupportedEventTypes =
    [
        HubEventTypes.AppDefinitionUpserted,
        HubEventTypes.AppDefinitionDeleted,
        HubEventTypes.AppInstanceRegistered,
        HubEventTypes.AppInstanceUnregistered,
        HubEventTypes.InvocationQueued,
        HubEventTypes.InvocationDelivered,
        HubEventTypes.InvocationCompleted,
        HubEventTypes.InvocationFailed
    ];

    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
    private readonly ILogger<HubEventSessionManager> _logger;

    /// <summary>
    /// 初始化会话事件投递管理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public HubEventSessionManager(ILogger<HubEventSessionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册连接。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    public void RegisterConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        _connections[connectionId] = new ConnectionState();
        _logger.LogDebug("事件会话管理器已注册连接: {ConnectionId}", connectionId);
    }

    /// <summary>
    /// 移除连接及其订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    public void RemoveConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_connections.TryRemove(connectionId, out _))
        {
            _logger.LogDebug("事件会话管理器已移除连接: {ConnectionId}", connectionId);
        }
    }

    /// <summary>
    /// 标记连接认证成功。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="clientId">客户端标识。</param>
    /// <param name="clientSessionId">客户端会话标识。</param>
    /// <returns>连接存在时返回 <c>true</c>。</returns>
    public bool TryMarkAuthenticated(string connectionId, string clientId, string clientSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSessionId);

        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return false;
        }

        state.IsAuthenticated = true;
        state.ClientId = clientId;
        state.ClientSessionId = clientSessionId;

        _logger.LogInformation("WS 连接认证成功，ConnectionId: {ConnectionId}, ClientId: {ClientId}", connectionId, clientId);
        return true;
    }

    /// <summary>
    /// 创建订阅。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="types">订阅类型列表；为空表示全部事件。</param>
    /// <param name="subscriptionId">生成的订阅标识。</param>
    /// <returns>连接存在且已认证时返回 <c>true</c>。</returns>
    public bool TrySubscribe(string connectionId, IReadOnlyCollection<string>? types, out string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        subscriptionId = string.Empty;

        if (!_connections.TryGetValue(connectionId, out var state) || !state.IsAuthenticated)
        {
            return false;
        }

        HashSet<string>? typeSet = null;
        if (types is not null && types.Count > 0)
        {
            typeSet = types
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
        }

        subscriptionId = $"sub-{Guid.NewGuid():N}";
        state.Subscriptions[subscriptionId] = new HubEventSubscription
        {
            SubscriptionId = subscriptionId,
            Types = typeSet
        };

        _logger.LogInformation(
            "事件订阅创建成功，ConnectionId: {ConnectionId}, SubscriptionId: {SubscriptionId}, TypeCount: {TypeCount}",
            connectionId,
            subscriptionId,
            typeSet?.Count ?? 0);

        return true;
    }

    /// <summary>
    /// 取消订阅（幂等）。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="subscriptionId">订阅标识。</param>
    public void Unsubscribe(string connectionId, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (_connections.TryGetValue(connectionId, out var state))
        {
            _ = state.Subscriptions.TryRemove(subscriptionId, out _);
        }
    }

    /// <inheritdoc />
    public void Publish(HubEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Type);

        var totalPendingEstimate = GetTotalPendingDeliveriesEstimate();

        foreach (var (connectionId, state) in _connections)
        {
            if (!state.IsAuthenticated || state.Subscriptions.IsEmpty)
            {
                continue;
            }

            foreach (var subscription in state.Subscriptions.Values)
            {
                if (!MatchesType(subscription, message.Type))
                {
                    continue;
                }

                if (state.PendingDeliveries.Count >= MaxPendingDeliveriesPerConnection)
                {
                    _logger.LogWarning(
                        "事件投递队列达到连接级上限，已丢弃。ConnectionId: {ConnectionId}, EventType: {EventType}, Limit: {Limit}",
                        connectionId,
                        message.Type,
                        MaxPendingDeliveriesPerConnection);
                    continue;
                }

                if (totalPendingEstimate >= MaxPendingDeliveriesTotal)
                {
                    _logger.LogWarning(
                        "事件投递队列达到全局上限，已丢弃。ConnectionId: {ConnectionId}, EventType: {EventType}, Limit: {Limit}",
                        connectionId,
                        message.Type,
                        MaxPendingDeliveriesTotal);
                    continue;
                }

                state.PendingDeliveries.Enqueue(new HubEventDelivery
                {
                    ConnectionId = connectionId,
                    SubscriptionId = subscription.SubscriptionId,
                    Type = message.Type,
                    TimeUtc = message.TimeUtc,
                    Payload = message.Payload
                });
                state.DeliverySignal.TrySetResult(true);
                totalPendingEstimate += 1;
            }
        }
    }

    /// <summary>
    /// 提取连接待发送事件。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="maxCount">最大提取条数。</param>
    /// <returns>待发送事件列表。</returns>
    public IReadOnlyList<HubEventDelivery> DrainDeliveries(string connectionId, int maxCount = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (maxCount <= 0)
        {
            return [];
        }

        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return [];
        }

        var results = new List<HubEventDelivery>(Math.Min(maxCount, 32));
        while (results.Count < maxCount && state.PendingDeliveries.TryDequeue(out var delivery))
        {
            results.Add(delivery);
        }

        if (state.PendingDeliveries.IsEmpty)
        {
            ResetDeliverySignal(state);
        }

        return results;
    }

    /// <summary>
    /// 等待连接出现新的待发送事件。
    /// </summary>
    /// <param name="connectionId">连接标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检测到待投递事件时返回 <c>true</c>。</returns>
    public ValueTask<bool> WaitForDeliveryAsync(string connectionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return ValueTask.FromResult(false);
        }

        if (!state.PendingDeliveries.IsEmpty)
        {
            return ValueTask.FromResult(true);
        }

        return WaitForDeliveryCoreAsync(state, cancellationToken);
    }

    /// <summary>
    /// 判断是否为支持的事件类型。
    /// </summary>
    /// <param name="eventType">事件类型。</param>
    /// <returns>支持时返回 <c>true</c>。</returns>
    public static bool IsSupportedEventType(string eventType)
    {
        return SupportedEventTypes.Contains(eventType, StringComparer.Ordinal);
    }

    /// <summary>
    /// 获取全部支持事件类型。
    /// </summary>
    /// <returns>事件类型列表。</returns>
    public static IReadOnlyList<string> GetSupportedEventTypes()
    {
        return SupportedEventTypes;
    }

    private static bool MatchesType(HubEventSubscription subscription, string eventType)
    {
        return subscription.Types is null || subscription.Types.Contains(eventType);
    }

    private int GetTotalPendingDeliveriesEstimate()
    {
        var total = 0;
        foreach (var state in _connections.Values)
        {
            total += state.PendingDeliveries.Count;
            if (total >= MaxPendingDeliveriesTotal)
            {
                break;
            }
        }

        return total;
    }

    private sealed class ConnectionState
    {
        public bool IsAuthenticated { get; set; }

        public string? ClientId { get; set; }

        public string? ClientSessionId { get; set; }

        public ConcurrentDictionary<string, HubEventSubscription> Subscriptions { get; } = new();

        public ConcurrentQueue<HubEventDelivery> PendingDeliveries { get; } = new();

        public TaskCompletionSource<bool> DeliverySignal = CreateDeliverySignal();
    }

    private sealed class HubEventSubscription
    {
        public required string SubscriptionId { get; init; }

        public HashSet<string>? Types { get; init; }
    }

    private static async ValueTask<bool> WaitForDeliveryCoreAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        await state.DeliverySignal.Task.WaitAsync(cancellationToken);
        return true;
    }

    private static void ResetDeliverySignal(ConnectionState state)
    {
        while (true)
        {
            var current = state.DeliverySignal;
            if (!current.Task.IsCompleted || !state.PendingDeliveries.IsEmpty)
            {
                return;
            }

            var replacement = CreateDeliverySignal();
            if (Interlocked.CompareExchange(ref state.DeliverySignal, replacement, current) == current)
            {
                return;
            }
        }
    }

    private static TaskCompletionSource<bool> CreateDeliverySignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
