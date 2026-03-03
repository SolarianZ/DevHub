namespace DevHub.Host.Tests.TestHelpers;

using System.Net.WebSockets;
using System.Text;

/// <summary>
/// 可脚本化的 WebSocket 测试替身。
/// </summary>
internal sealed class ScriptedWebSocket : WebSocket
{
    private readonly Queue<SocketFrame> _frames = new();
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly TimeSpan _closeFrameDelay;
    private readonly object _framesLock = new();
    private readonly object _sentTextsLock = new();
    private WebSocketState _state;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;
    private bool _closeFrameQueued;

    /// <summary>
    /// 初始化脚本化 WebSocket。
    /// </summary>
    /// <param name="textMessages">按顺序返回的文本帧列表。</param>
    /// <param name="closeFrameDelay">文本帧耗尽后返回 close 帧前的延迟。</param>
    /// <param name="autoCloseWhenQueueDrained">是否在初始帧消费完后自动返回 close 帧。</param>
    public ScriptedWebSocket(
        IEnumerable<string> textMessages,
        TimeSpan? closeFrameDelay = null,
        bool autoCloseWhenQueueDrained = true)
    {
        _closeFrameDelay = closeFrameDelay ?? TimeSpan.Zero;
        _state = WebSocketState.Open;

        foreach (var text in textMessages)
        {
            EnqueueFrame(SocketFrame.Text(text));
        }

        if (autoCloseWhenQueueDrained)
        {
            EnqueueClose();
        }
    }

    /// <summary>
    /// 获取服务端发送到该套接字的文本消息。
    /// </summary>
    public List<string> SentTexts { get; } = [];

    /// <summary>
    /// 向输入脚本追加文本消息。
    /// </summary>
    /// <param name="text">文本消息。</param>
    public void EnqueueText(string text)
    {
        EnqueueFrame(SocketFrame.Text(text));
    }

    /// <summary>
    /// 向输入脚本追加 close 帧。
    /// </summary>
    public void EnqueueClose()
    {
        EnqueueFrame(SocketFrame.Close());
    }

    /// <summary>
    /// 获取当前已发送文本快照。
    /// </summary>
    /// <returns>文本消息快照。</returns>
    public IReadOnlyList<string> GetSentTextsSnapshot()
    {
        lock (_sentTextsLock)
        {
            return SentTexts.ToArray();
        }
    }

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;

    /// <inheritdoc />
    public override string? CloseStatusDescription => _closeStatusDescription;

    /// <inheritdoc />
    public override WebSocketState State => _state;

    /// <inheritdoc />
    public override string? SubProtocol => null;

    /// <inheritdoc />
    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _frameSignal.Release();
    }

    /// <inheritdoc />
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _frameSignal.Release();
    }

    /// <inheritdoc />
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        SocketFrame frame;

        while (true)
        {
            await _frameSignal.WaitAsync(cancellationToken);

            lock (_framesLock)
            {
                if (_frames.Count == 0)
                {
                    if (_state != WebSocketState.Open)
                    {
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                    }

                    continue;
                }

                frame = _frames.Dequeue();
                break;
            }
        }

        if (frame.MessageType == WebSocketMessageType.Close)
        {
            if (_closeFrameDelay > TimeSpan.Zero)
            {
                await Task.Delay(_closeFrameDelay, cancellationToken);
            }

            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        if (buffer.Array is null)
        {
            throw new InvalidOperationException("WebSocket 接收缓冲区不能为空。");
        }

        frame.Payload.CopyTo(buffer.Array, buffer.Offset);
        return new WebSocketReceiveResult(frame.Payload.Length, frame.MessageType, true);
    }

    /// <inheritdoc />
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (messageType == WebSocketMessageType.Text && buffer.Array is not null)
        {
            lock (_sentTextsLock)
            {
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            }
        }

        return Task.CompletedTask;
    }

    private void EnqueueFrame(SocketFrame frame)
    {
        lock (_framesLock)
        {
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                if (_closeFrameQueued)
                {
                    return;
                }

                _closeFrameQueued = true;
            }

            _frames.Enqueue(frame);
        }

        _frameSignal.Release();
    }

    private sealed record SocketFrame(WebSocketMessageType MessageType, byte[] Payload)
    {
        public static SocketFrame Text(string text)
        {
            return new SocketFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text));
        }

        public static SocketFrame Close()
        {
            return new SocketFrame(WebSocketMessageType.Close, []);
        }
    }
}
