using System.Globalization;
using Aerochat.Presentation;

namespace Aerochat.Connectivity;

public sealed class PresentationAdapter : IDisposable
{
    private readonly PresentationState _state;
    private readonly IChatTransport _transport;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    public PresentationAdapter(
        PresentationState state,
        IChatTransport transport,
        Action<Action>? dispatch = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _dispatch = dispatch ?? (action => action());
        _transport.MessageCreated += OnMessageCreated;
        _transport.PresenceUpdated += OnPresenceUpdated;
    }

    public void ApplyMessageCreated(MessageCreatedEventArgs message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ulong.TryParse(message.ConversationId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong conversationId)
            || !Guid.TryParse(message.MessageId, out Guid messageId)
            || !ulong.TryParse(message.AuthorId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong authorId))
        {
            return;
        }

        _state.ApplyRemoteMessage(conversationId, messageId, authorId, message.Body, message.CreatedAt);
    }

    public void ApplyPresenceUpdated(PresenceUpdatedEventArgs update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!ulong.TryParse(update.UserId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong userId)
            || !Enum.TryParse(update.Status, ignoreCase: true, out PresenceStatus status)
            || !Enum.IsDefined(status))
        {
            return;
        }

        _state.ApplyRemotePresence(userId, status);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _transport.MessageCreated -= OnMessageCreated;
        _transport.PresenceUpdated -= OnPresenceUpdated;
        _disposed = true;
    }

    private void OnMessageCreated(object? sender, MessageCreatedEventArgs message) =>
        _dispatch(() => ApplyMessageCreated(message));

    private void OnPresenceUpdated(object? sender, PresenceUpdatedEventArgs update) =>
        _dispatch(() => ApplyPresenceUpdated(update));

}
