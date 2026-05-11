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
    private readonly bool _blockCloseAsyncUntilCanceled;
    private readonly object _framesLock = new();
    private readonly object _sentTextsLock = new();
    private readonly object _sendSignalLock = new();
    private SocketFrame? _activeFrame;
    private WebSocketState _state;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;
    private bool _closeFrameQueued;
    private int _closeAsyncCallCount;
    private int _closeAsyncCancellationCount;
    private int _sentTextCount;
    private TaskCompletionSource<int> _nextSendSignal = CreateSendSignal();

    /// <summary>
    /// 初始化脚本化 WebSocket。
    /// </summary>
    /// <param name="textMessages">按顺序返回的文本帧列表。</param>
    /// <param name="closeFrameDelay">文本帧耗尽后返回 close 帧前的延迟。</param>
    /// <param name="autoCloseWhenQueueDrained">是否在初始帧消费完后自动返回 close 帧。</param>
    /// <param name="blockCloseAsyncUntilCanceled">是否让 <see cref="CloseAsync" /> 挂起直至取消令牌被触发。</param>
    public ScriptedWebSocket(
        IEnumerable<string> textMessages,
        TimeSpan? closeFrameDelay = null,
        bool autoCloseWhenQueueDrained = true,
        bool blockCloseAsyncUntilCanceled = false)
    {
        _closeFrameDelay = closeFrameDelay ?? TimeSpan.Zero;
        _blockCloseAsyncUntilCanceled = blockCloseAsyncUntilCanceled;
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
    /// 初始化包含原始文本帧字节的脚本化 WebSocket。
    /// </summary>
    /// <param name="textMessageBytes">按顺序返回的文本帧原始负载。</param>
    /// <param name="closeFrameDelay">文本帧耗尽后返回 close 帧前的延迟。</param>
    /// <param name="autoCloseWhenQueueDrained">初始帧消费完成后是否自动返回 close 帧。</param>
    public ScriptedWebSocket(
        IEnumerable<byte[]> textMessageBytes,
        TimeSpan? closeFrameDelay = null,
        bool autoCloseWhenQueueDrained = true)
    {
        _closeFrameDelay = closeFrameDelay ?? TimeSpan.Zero;
        _state = WebSocketState.Open;

        foreach (var payload in textMessageBytes)
        {
            EnqueueFrame(SocketFrame.TextBytes(payload));
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
    /// 获取服务端调用 <see cref="CloseAsync" /> 的次数。
    /// </summary>
    public int CloseAsyncCallCount => Volatile.Read(ref _closeAsyncCallCount);

    /// <summary>
    /// 获取服务端调用 <see cref="CloseAsync" /> 时因取消退出的次数。
    /// </summary>
    public int CloseAsyncCancellationCount => Volatile.Read(ref _closeAsyncCancellationCount);

    /// <summary>
    /// 向输入脚本追加文本消息。
    /// </summary>
    /// <param name="text">文本消息。</param>
    public void EnqueueText(string text)
    {
        EnqueueFrame(SocketFrame.Text(text));
    }

    /// <summary>
    /// 向输入脚本追加原始文本帧字节。
    /// </summary>
    /// <param name="payload">文本帧原始负载。</param>
    public void EnqueueTextBytes(byte[] payload)
    {
        EnqueueFrame(SocketFrame.TextBytes(payload));
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

    /// <summary>
    /// 等待服务端发送新的文本消息。
    /// </summary>
    /// <param name="observedCount">调用方当前已观察到的文本消息数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的已发送文本消息总数。</returns>
    public Task<int> WaitForNextSentTextAsync(int observedCount, CancellationToken cancellationToken)
    {
        lock (_sendSignalLock)
        {
            if (_sentTextCount > observedCount)
            {
                return Task.FromResult(_sentTextCount);
            }

            var waitTask = _nextSendSignal.Task;
            if (!cancellationToken.CanBeCanceled)
            {
                return waitTask;
            }

            return WaitWithCancellationAsync(waitTask, cancellationToken);
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
    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _closeAsyncCallCount);
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        if (_blockCloseAsyncUntilCanceled)
        {
            _state = WebSocketState.CloseSent;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _closeAsyncCancellationCount);
                throw;
            }

            return;
        }

        _state = WebSocketState.Closed;
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
        CompletePendingSendWaiters();
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
            if (_activeFrame is not null)
            {
                frame = _activeFrame;
                break;
            }

            await _frameSignal.WaitAsync(cancellationToken);

            lock (_framesLock)
            {
                if (_activeFrame is not null)
                {
                    frame = _activeFrame;
                    break;
                }

                if (_frames.Count == 0)
                {
                    if (_state != WebSocketState.Open)
                    {
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                    }

                    continue;
                }

                frame = _frames.Dequeue();
                _activeFrame = frame;
                break;
            }
        }

        if (frame.MessageType == WebSocketMessageType.Close)
        {
            _activeFrame = null;
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

        var remainingCount = frame.Payload.Length - frame.Offset;
        var bytesToCopy = Math.Min(remainingCount, buffer.Count);
        Buffer.BlockCopy(frame.Payload, frame.Offset, buffer.Array, buffer.Offset, bytesToCopy);
        frame.Offset += bytesToCopy;

        var endOfMessage = frame.Offset >= frame.Payload.Length;
        if (endOfMessage)
        {
            _activeFrame = null;
        }

        return new WebSocketReceiveResult(bytesToCopy, frame.MessageType, endOfMessage);
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

            SignalSentText();
        }

        return Task.CompletedTask;
    }

    private static TaskCompletionSource<int> CreateSendSignal()
    {
        return new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<int> WaitWithCancellationAsync(Task<int> task, CancellationToken cancellationToken)
    {
        var cancellationTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, cancellationTask);

        var completed = await Task.WhenAny(task, cancellationTask.Task);
        if (completed == task)
        {
            return await task;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }

    private void SignalSentText()
    {
        TaskCompletionSource<int>? previousSignal;
        int sentTextCount;

        lock (_sendSignalLock)
        {
            _sentTextCount += 1;
            sentTextCount = _sentTextCount;
            previousSignal = _nextSendSignal;
            _nextSendSignal = CreateSendSignal();
        }

        previousSignal.TrySetResult(sentTextCount);
    }

    private void CompletePendingSendWaiters()
    {
        TaskCompletionSource<int>? previousSignal;
        int sentTextCount;

        lock (_sendSignalLock)
        {
            sentTextCount = _sentTextCount;
            previousSignal = _nextSendSignal;
            _nextSendSignal = CreateSendSignal();
        }

        previousSignal.TrySetResult(sentTextCount);
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

    private sealed class SocketFrame
    {
        public SocketFrame(WebSocketMessageType messageType, byte[] payload)
        {
            MessageType = messageType;
            Payload = payload;
        }

        public WebSocketMessageType MessageType { get; }

        public byte[] Payload { get; }

        public int Offset { get; set; }

        public static SocketFrame Text(string text)
        {
            return new SocketFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text));
        }

        public static SocketFrame TextBytes(byte[] payload)
        {
            return new SocketFrame(WebSocketMessageType.Text, payload);
        }

        public static SocketFrame Close()
        {
            return new SocketFrame(WebSocketMessageType.Close, []);
        }
    }
}
