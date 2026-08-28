using System.Net.WebSockets;

namespace Aerochat.Connectivity;

internal interface IGatewaySocket : IDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken);

    void Abort();
}

internal sealed class ClientWebSocketAdapter : IGatewaySocket
{
    private readonly ClientWebSocket _socket;

    public ClientWebSocketAdapter(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    public void Abort() => _socket.Abort();

    public void Dispose() => _socket.Dispose();
}
