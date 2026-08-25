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

        ConversationPresentation? conversation = _state.Conversations
            .FirstOrDefault(item => item.Id == conversationId);
        PersonPresentation? author = FindPerson(authorId);
        if (conversation is null || author is null || conversation.Messages.Any(item => item.Id == messageId))
            return;

        conversation.Messages.Add(new MessagePresentation
        {
            Id = messageId,
            Author = author,
            SentAt = message.CreatedAt,
            IsOutgoing = author.Id == _state.CurrentUser.Id,
            Body = message.Body
        });
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

        PersonPresentation? person = FindPerson(userId);
        if (person is not null)
        {
            person.Presence.Status = status;
        }
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

    private PersonPresentation? FindPerson(ulong id)
    {
        if (_state.CurrentUser.Id == id)
            return _state.CurrentUser;

        return _state.Conversations
            .SelectMany(conversation => conversation.Participants)
            .Concat(_state.ContactGroups.SelectMany(group => group.Items.Select(item => item.Person)))
            .FirstOrDefault(person => person.Id == id);
    }
}
