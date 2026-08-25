namespace Aerochat.Connectivity;

public interface IChatTransport : IAsyncDisposable
{
    event EventHandler<MessageCreatedEventArgs>? MessageCreated;
    event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;

    Task ConnectAsync(Uri server, string token, CancellationToken cancellationToken = default);

    Task SendAsync(
        string conversationId,
        string body,
        CancellationToken cancellationToken = default);

    Task SetTypingAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}

public sealed class MessageCreatedEventArgs : EventArgs
{
    public MessageCreatedEventArgs(
        string conversationId,
        string messageId,
        string authorId,
        string body,
        DateTimeOffset createdAt,
        string kind = "message")
    {
        ConversationId = conversationId;
        MessageId = messageId;
        AuthorId = authorId;
        Body = body;
        CreatedAt = createdAt;
        Kind = kind;
    }

    public string ConversationId { get; }
    public string MessageId { get; }
    public string AuthorId { get; }
    public string Body { get; }
    public DateTimeOffset CreatedAt { get; }
    public string Kind { get; }
}

public sealed class PresenceUpdatedEventArgs : EventArgs
{
    public PresenceUpdatedEventArgs(string userId, string status)
    {
        UserId = userId;
        Status = status;
    }

    public string UserId { get; }
    public string Status { get; }
}
