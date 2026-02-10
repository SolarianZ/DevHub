namespace DevHub.Host.Tests.TestHelpers;

using System.Net.WebSockets;
using System.Text;

/// <summary>
/// 可脚本化的 WebSocket 测试替身。
/// </summary>
internal sealed class ScriptedWebSocket : WebSocket
{
    private readonly Queue<SocketFrame> _frames;
    private readonly TimeSpan _closeFrameDelay;
    private WebSocketState _state;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    /// <summary>
    /// 初始化脚本化 WebSocket。
    /// </summary>
    /// <param name="textMessages">按顺序返回的文本帧列表。</param>
    /// <param name="closeFrameDelay">文本帧耗尽后返回 close 帧前的延迟。</param>
    public ScriptedWebSocket(IEnumerable<string> textMessages, TimeSpan? closeFrameDelay = null)
    {
        _frames = new Queue<SocketFrame>(textMessages.Select(text => SocketFrame.Text(text)));
        _frames.Enqueue(SocketFrame.Close());
        _closeFrameDelay = closeFrameDelay ?? TimeSpan.Zero;
        _state = WebSocketState.Open;
    }

    /// <summary>
    /// 获取服务端发送到该套接字的文本消息。
    /// </summary>
    public List<string> SentTexts { get; } = [];

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
    }

    /// <inheritdoc />
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        if (_frames.Count == 0)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        var frame = _frames.Dequeue();
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
            SentTexts.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
        }

        return Task.CompletedTask;
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
