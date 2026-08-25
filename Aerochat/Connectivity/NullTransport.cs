namespace Aerochat.Connectivity;

public sealed class NullTransport : IChatTransport
{
    public event EventHandler<MessageCreatedEventArgs>? MessageCreated
    {
        add { }
        remove { }
    }

    public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(Uri server, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(token);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendAsync(
        string conversationId,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(body);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SetTypingAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
