using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Aerochat.Connectivity;

public sealed class GatewayClient : IChatTransport
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const string PushOnlyMessage =
        "The Aerochat gateway is push-only; outbound messages and typing are not supported.";

    private readonly object _gate = new();
    private readonly Func<ClientWebSocket> _socketFactory;
    private readonly Func<int, TimeSpan>? _jitter;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveTask;
    private string? _lastEventId;

    public GatewayClient(
        Func<ClientWebSocket>? socketFactory = null,
        Func<int, TimeSpan>? jitter = null)
    {
        _socketFactory = socketFactory ?? (() => new ClientWebSocket());
        _jitter = jitter;
    }

    public event EventHandler<MessageCreatedEventArgs>? MessageCreated;
    public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;

    public string? LastEventId => Volatile.Read(ref _lastEventId);

    public async Task ConnectAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        lock (_gate)
        {
            if (_receiveTask is not null)
                throw new InvalidOperationException("The gateway client is already connected.");
        }

        Interlocked.Exchange(ref _lastEventId, null);
        Uri gatewayUri = BuildGatewayUri(server, token, null);
        ClientWebSocket socket = _socketFactory();
        try
        {
            await socket.ConnectAsync(gatewayUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        CancellationTokenSource lifetime = new();
        lock (_gate)
        {
            _socket = socket;
            _lifetime = lifetime;
            _receiveTask = ReceiveAndReconnectAsync(server, token, socket, lifetime.Token);
        }
    }

    public Task SendAsync(
        string conversationId,
        string body,
        CancellationToken cancellationToken = default) =>
        // Task 9 deviation: the server gateway intentionally accepts no inbound frames.
        throw new NotSupportedException(PushOnlyMessage);

    public Task SetTypingAsync(
        string conversationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PushOnlyMessage);

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? receiveTask;
        ClientWebSocket? socket;
        lock (_gate)
        {
            lifetime = _lifetime;
            receiveTask = _receiveTask;
            socket = _socket;
            _lifetime = null;
            _receiveTask = null;
            _socket = null;
        }

        if (lifetime is null)
            return;

        lifetime.Cancel();
        socket?.Abort();
        if (receiveTask is not null)
            await receiveTask.ConfigureAwait(false);

        socket?.Dispose();
        lifetime.Dispose();
    }

    public static Uri BuildGatewayUri(Uri server, string token, string? lastEventId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The gateway server URI must use HTTP(S) or WS(S).", nameof(server));
        }

        UriBuilder builder = new(server)
        {
            Scheme = string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(server.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
            Path = server.AbsolutePath.TrimEnd('/') + "/ws"
        };

        string query = $"token={Uri.EscapeDataString(token)}";
        if (!string.IsNullOrEmpty(lastEventId))
            query += $"&lastEventId={Uri.EscapeDataString(lastEventId)}";
        builder.Query = query;
        return builder.Uri;
    }

    private async Task ReceiveAndReconnectAsync(
        Uri server,
        string token,
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        ClientWebSocket current = socket;
        int attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ReceiveLoopAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException)
                {
                }

                current.Dispose();
                if (cancellationToken.IsCancellationRequested)
                    break;

                await Task.Delay(
                    ExponentialBackoff.GetDelay(attempt++, _jitter),
                    cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    ClientWebSocket next = _socketFactory();
                    try
                    {
                        Uri reconnectUri = BuildGatewayUri(server, token, LastEventId);
                        await next.ConnectAsync(reconnectUri, cancellationToken).ConfigureAwait(false);
                        current = next;
                        attempt = 0;
                        lock (_gate)
                        {
                            _socket = current;
                        }

                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        next.Dispose();
                        return;
                    }
                    catch (WebSocketException)
                    {
                        next.Dispose();
                        await Task.Delay(
                            ExponentialBackoff.GetDelay(attempt++, _jitter),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            if (result.MessageType != WebSocketMessageType.Text)
            {
                frame.SetLength(0);
                continue;
            }

            frame.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            ProcessFrame(Encoding.UTF8.GetString(frame.GetBuffer(), 0, checked((int)frame.Length)));
            frame.SetLength(0);
        }
    }

    private void ProcessFrame(string json)
    {
        if (!GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame) || frame is null)
            return;

        if (!string.IsNullOrEmpty(frame.EventId))
            Interlocked.Exchange(ref _lastEventId, frame.EventId);

        switch (frame.Type)
        {
            case "message.created":
                if (TryReadMessage(frame.Data, out MessageCreatedEventArgs? message))
                    MessageCreated?.Invoke(this, message!);
                break;
            case "presence.updated":
                if (TryReadPresence(frame.Data, out PresenceUpdatedEventArgs? presence))
                    PresenceUpdated?.Invoke(this, presence!);
                break;
        }
    }

    private static bool TryReadMessage(
        JsonElement data,
        out MessageCreatedEventArgs? message)
    {
        message = null;
        if (!TryGetString(data, "conversationId", out string? conversationId)
            || !data.TryGetProperty("message", out JsonElement payload)
            || payload.ValueKind != JsonValueKind.Object
            || !TryGetString(payload, "id", out string? messageId)
            || !TryGetString(payload, "authorId", out string? authorId)
            || !TryGetString(payload, "body", out string? body)
            || !TryGetString(payload, "createdAt", out string? createdAtText)
            || !DateTimeOffset.TryParse(
                createdAtText!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAt))
        {
            return false;
        }

        string kind = TryGetString(payload, "kind", out string? parsedKind)
            ? parsedKind!
            : "message";
        message = new MessageCreatedEventArgs(
            conversationId!, messageId!, authorId!, body!, createdAt, kind);
        return true;
    }

    private static bool TryReadPresence(
        JsonElement data,
        out PresenceUpdatedEventArgs? presence)
    {
        presence = null;
        if (!TryGetString(data, "userId", out string? userId)
            || !TryGetString(data, "status", out string? status))
        {
            return false;
        }

        presence = new PresenceUpdatedEventArgs(userId!, status!);
        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }
}
