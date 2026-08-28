using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Aerochat.Connectivity;

public sealed class GatewayClient : IChatTransport
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const string PushOnlyMessage =
        "The Aerochat gateway is push-only; outbound messages and typing are not supported.";

    [ThreadStatic]
    private static GatewayClient? _deliveryContext;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _deliveryOwnership = new(1, 1);
    private readonly HashSet<IGatewaySocket> _disposedSockets =
        new(ReferenceEqualityComparer.Instance);
    private readonly Func<IGatewaySocket> _socketFactory;
    private readonly Func<int, TimeSpan>? _jitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private IGatewaySocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _connectTask;
    private Task? _receiveTask;
    private Task? _disposeTask;
    private string? _lastEventId;
    private bool _disposed;

    public GatewayClient(
        Func<ClientWebSocket>? socketFactory = null,
        Func<int, TimeSpan>? jitter = null)
        : this(
            () => new ClientWebSocketAdapter((socketFactory ?? (() => new ClientWebSocket()))()),
            jitter,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal GatewayClient(
        Func<IGatewaySocket> socketFactory,
        Func<int, TimeSpan>? jitter,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _jitter = jitter;
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public event EventHandler<MessageCreatedEventArgs>? MessageCreated;
    public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;
    public event EventHandler<CallSignalEventArgs>? CallSignalReceived;

    public string? LastEventId => Volatile.Read(ref _lastEventId);

    public async Task ConnectAsync(
        Uri server,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        CancellationTokenSource lifetime;
        Task connectTask;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GatewayClient));
            if (_connectTask is not null || _receiveTask is not null)
                throw new InvalidOperationException("The gateway client is already connected.");

            Interlocked.Exchange(ref _lastEventId, null);
            lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            var initialConnectStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            connectTask = ConnectInitialAsync(
                server,
                token,
                lifetime,
                cancellationToken,
                initialConnectStart.Task);
            _connectTask = connectTask;
            initialConnectStart.TrySetResult();
        }

        await connectTask.ConfigureAwait(false);
    }

    public Task SendAsync(
        string conversationId,
        string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PushOnlyMessage);

    public Task SetTypingAsync(
        string conversationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PushOnlyMessage);

    public ValueTask DisposeAsync()
    {
        bool reentrantDelivery = ReferenceEquals(_deliveryContext, this);
        Task disposalTask;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return reentrantDelivery
                    ? ValueTask.CompletedTask
                    : new ValueTask(_disposeTask);
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            disposalTask = completion.Task;
        }

        _ = CompleteDisposeAsync(completion);
        return reentrantDelivery
            ? ValueTask.CompletedTask
            : new ValueTask(disposalTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        CancellationTokenSource? lifetime;
        Task? connectTask;
        Task? receiveTask;
        IGatewaySocket? socket;
        await _deliveryOwnership.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _disposed = true;
                lifetime = _lifetime;
                connectTask = _connectTask;
                receiveTask = _receiveTask;
                socket = _socket;
                _lifetime = null;
                _connectTask = null;
                _receiveTask = null;
                _socket = null;
            }
        }
        finally
        {
            _deliveryOwnership.Release();
        }

        if (lifetime is null)
            return;

        lifetime.Cancel();
        socket?.Abort();
        try
        {
            if (connectTask is not null)
                await connectTask.ConfigureAwait(false);
            if (receiveTask is not null)
                await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (socket is not null)
                DisposeSocketOnce(socket);
            lifetime.Dispose();
        }
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

    private async Task ConnectInitialAsync(
        Uri server,
        string token,
        CancellationTokenSource lifetime,
        CancellationToken callerCancellationToken,
        Task initialConnectStart)
    {
        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, callerCancellationToken);
        await initialConnectStart.ConfigureAwait(false);
        IGatewaySocket? socket = null;
        try
        {
            socket = _socketFactory();
            lock (_gate)
            {
                if (_disposed
                    || !ReferenceEquals(_lifetime, lifetime)
                    || lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(lifetime.Token);
                }

                _socket = socket;
            }

            await socket.ConnectAsync(
                BuildGatewayUri(server, token, null),
                operationCancellation.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed
                    || !ReferenceEquals(_lifetime, lifetime)
                    || lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(lifetime.Token);
                }

                IGatewaySocket connectedSocket = socket;
                _receiveTask = Task.Run(
                    () => ReceiveAndReconnectAsync(server, token, connectedSocket, lifetime.Token));
                socket = null;
            }
        }
        catch
        {
            if (socket is not null)
                DisposeSocketOnce(socket);
            ReleaseInitialOwnership(lifetime);
            throw;
        }
    }

    private void ReleaseInitialOwnership(CancellationTokenSource lifetime)
    {
        bool owned;
        lock (_gate)
        {
            owned = ReferenceEquals(_lifetime, lifetime);
            if (owned)
            {
                _lifetime = null;
                _connectTask = null;
                _receiveTask = null;
                _socket = null;
            }
        }

        if (owned)
        {
            lifetime.Dispose();
        }
    }

    private async Task ReceiveAndReconnectAsync(
        Uri server,
        string token,
        IGatewaySocket socket,
        CancellationToken cancellationToken)
    {
        IGatewaySocket current = socket;
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

                DisposeSocketOnce(current);
                if (cancellationToken.IsCancellationRequested)
                    break;

                await _delay(
                    ExponentialBackoff.GetDelay(attempt++, _jitter),
                    cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    IGatewaySocket? next = null;
                    try
                    {
                        next = _socketFactory();
                        if (!PublishSocket(next, cancellationToken))
                        {
                            DisposeSocketOnce(next);
                            return;
                        }

                        Uri reconnectUri = BuildGatewayUri(server, token, LastEventId);
                        await next.ConnectAsync(reconnectUri, cancellationToken).ConfigureAwait(false);
                        bool accepted;
                        lock (_gate)
                        {
                            accepted = !_disposed && !cancellationToken.IsCancellationRequested;
                            if (accepted)
                            {
                                current = next;
                                attempt = 0;
                            }
                        }

                        if (!accepted)
                        {
                            DisposeSocketOnce(next);
                            return;
                        }

                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (next is not null)
                            DisposeSocketOnce(next);
                        return;
                    }
                    catch (Exception)
                    {
                        if (next is not null)
                            DisposeSocketOnce(next);
                        await _delay(
                            ExponentialBackoff.GetDelay(attempt++, _jitter),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            DisposeSocketOnce(current);
        }
    }

    private bool PublishSocket(IGatewaySocket socket, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return false;

            _socket = socket;
            return true;
        }
    }

    private void DisposeSocketOnce(IGatewaySocket socket)
    {
        lock (_gate)
        {
            if (!_disposedSockets.Add(socket))
                return;

            if (ReferenceEquals(_socket, socket))
                _socket = null;
        }

        socket.Dispose();
    }

    private async Task ReceiveLoopAsync(
        IGatewaySocket socket,
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

            await _deliveryOwnership.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_socket, socket))
                        return;
                }

                GatewayClient? previousDeliveryContext = _deliveryContext;
                _deliveryContext = this;
                try
                {
                    ProcessFrame(
                        Encoding.UTF8.GetString(frame.GetBuffer(), 0, checked((int)frame.Length)),
                        socket);
                }
                finally
                {
                    _deliveryContext = previousDeliveryContext;
                }
            }
            finally
            {
                _deliveryOwnership.Release();
            }

            frame.SetLength(0);
        }
    }

    private void ProcessFrame(string json, IGatewaySocket socket)
    {
        if (!GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame) || frame is null)
            return;

        switch (frame.Type)
        {
            case "message.created":
                if (GatewayProtocol.TryParseMessage(frame.Data, out MessageCreatedEventArgs? message))
                    InvokeSubscribers(MessageCreated, message!);
                break;
            case "presence.updated":
                if (TryReadPresence(frame.Data, out PresenceUpdatedEventArgs? presence))
                    InvokeSubscribers(PresenceUpdated, presence!);
                break;
            case "call.ring":
            case "call.offer":
            case "call.answer":
            case "call.ice":
            case "call.hangup":
                if (GatewayProtocol.TryParseCallSignal(frame, out CallSignalEventArgs? call))
                    InvokeSubscribers(CallSignalReceived, call!);
                break;
        }

        if (!string.IsNullOrEmpty(frame.EventId))
            Interlocked.Exchange(ref _lastEventId, frame.EventId);
    }

    private void InvokeSubscribers<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
            return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
            }
        }
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
