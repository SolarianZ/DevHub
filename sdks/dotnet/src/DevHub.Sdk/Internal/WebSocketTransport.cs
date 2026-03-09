using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace DevHub.Sdk.Internal;

internal interface IWebSocketConnectionFactory
{
    Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

internal interface IWebSocketConnection : IAsyncDisposable
{
    WebSocketState State { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
}

internal sealed class WebSocketReceiveMessage
{
    public required WebSocketMessageType MessageType { get; init; }

    public string? Text { get; init; }

    public WebSocketCloseStatus? CloseStatus { get; init; }

    public string? CloseStatusDescription { get; init; }
}

internal sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public async Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var clientWebSocket = new ClientWebSocket();
        await clientWebSocket.ConnectAsync(uri, cancellationToken);
        return new ClientWebSocketConnection(clientWebSocket);
    }
}

internal sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _clientWebSocket;

    public ClientWebSocketConnection(ClientWebSocket clientWebSocket)
    {
        _clientWebSocket = clientWebSocket;
    }

    public WebSocketState State => _clientWebSocket.State;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _clientWebSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await _clientWebSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveMessage
                {
                    MessageType = result.MessageType,
                    CloseStatus = result.CloseStatus,
                    CloseStatusDescription = result.CloseStatusDescription
                };
            }

            if (result.Count > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }

            if (result.EndOfMessage)
            {
                return new WebSocketReceiveMessage
                {
                    MessageType = result.MessageType,
                    Text = Encoding.UTF8.GetString(stream.ToArray())
                };
            }
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (_clientWebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _clientWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _clientWebSocket.Dispose();
        return ValueTask.CompletedTask;
    }
}
