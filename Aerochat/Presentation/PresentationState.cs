using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;

namespace Aerochat.Presentation;

public sealed class PresentationState : ObservableObject
{
    private ScenePresentation _currentScene;
    private AdPresentation? _currentAd;
    private bool _isEditingStatus;

    public PresentationState(
        PersonPresentation currentUser,
        ScenePresentation currentScene,
        VisualSettingsPresentation settings)
    {
        CurrentUser = currentUser;
        _currentScene = currentScene;
        Settings = settings;
    }

    public PersonPresentation CurrentUser { get; }
    public ObservableCollection<ContactGroupPresentation> ContactGroups { get; } = [];
    public ObservableCollection<ContactGroupPresentation> FilteredContactGroups { get; } = [];
    public ObservableCollection<ConversationPresentation> Conversations { get; } = [];
    public ObservableCollection<ScenePresentation> Scenes { get; } = [];
    public ObservableCollection<NewsPresentation> News { get; } = [];
    public ObservableCollection<NoticePresentation> Notices { get; } = [];
    public ObservableCollection<AdPresentation> Ads { get; } = [];
    public ObservableCollection<PreviewImagePresentation> PreviewImages { get; } = [];
    public ObservableCollection<CallSessionPresentation> CallSessions { get; } = [];
    public VisualSettingsPresentation Settings { get; }

    public ScenePresentation CurrentScene
    {
        get => _currentScene;
        private set => SetProperty(ref _currentScene, value);
    }

    public AdPresentation? CurrentAd
    {
        get => _currentAd;
        set => SetProperty(ref _currentAd, value);
    }

    public bool IsEditingStatus
    {
        get => _isEditingStatus;
        set => SetProperty(ref _isEditingStatus, value);
    }

    public void ApplySearch(string searchText)
    {
        string query = searchText.Trim();

        foreach (ContactGroupPresentation filteredGroup in FilteredContactGroups)
            filteredGroup.UnlinkFilteredCopy();
        FilteredContactGroups.Clear();

        foreach (ContactGroupPresentation sourceGroup in ContactGroups)
        {
            if (query.Length == 0)
            {
                FilteredContactGroups.Add(sourceGroup);
                continue;
            }

            ContactGroupPresentation filteredGroup = new()
            {
                Name = sourceGroup.Name,
                IsCollapsed = sourceGroup.IsCollapsed,
                IsSelected = sourceGroup.IsSelected,
                IsVisibleProperty = sourceGroup.IsVisibleProperty
            };
            filteredGroup.LinkFilteredCopy(sourceGroup);

            foreach (ContactPresentation contact in sourceGroup.Items)
            {
                if (Matches(contact.Person, query))
                    filteredGroup.Items.Add(contact);
            }

            if (filteredGroup.Items.Count > 0)
                FilteredContactGroups.Add(filteredGroup);
        }

        Notify(nameof(FilteredContactGroups));
    }

    public MessagePresentation? SendDraft(
        ConversationPresentation conversation,
        DateTimeOffset sentAt)
    {
        string body = conversation.Draft.Trim();
        if (body.Length == 0)
            return null;

        int ordinal = conversation.Messages.Count + 1;
        MessagePresentation message = new()
        {
            Id = CreateMessageId(conversation.Id, ordinal),
            Author = CurrentUser,
            SentAt = sentAt,
            IsOutgoing = true,
            Body = body,
            ReplyTo = conversation.TargetMode == MessageTargetMode.Reply
                ? conversation.TargetMessage
                : null
        };

        conversation.Messages.Add(message);
        conversation.Draft = "";
        ClearTarget(conversation);
        return message;
    }

    public void BeginReply(
        ConversationPresentation conversation,
        MessagePresentation message)
    {
        if (!conversation.Messages.Contains(message))
            return;

        if (conversation.TargetMode == MessageTargetMode.Edit)
            conversation.Draft = "";

        conversation.TargetMessage = message;
        conversation.TargetMode = MessageTargetMode.Reply;
    }

    public void BeginEdit(
        ConversationPresentation conversation,
        MessagePresentation message)
    {
        if (!conversation.Messages.Contains(message) || !IsLocalMessage(message))
            return;

        conversation.TargetMessage = message;
        conversation.TargetMode = MessageTargetMode.Edit;
        conversation.Draft = message.Body;
    }

    public void CommitEdit(ConversationPresentation conversation)
    {
        MessagePresentation? target = conversation.TargetMessage;
        if (conversation.TargetMode != MessageTargetMode.Edit || target is null)
            return;

        string body = conversation.Draft.Trim();
        if (body.Length > 0 && conversation.Messages.Contains(target) && IsLocalMessage(target))
            target.Body = body;

        conversation.Draft = "";
        ClearTarget(conversation);
    }

    public void SelectScene(ScenePresentation scene) => CurrentScene = scene;

    public void CancelTarget(ConversationPresentation conversation)
    {
        conversation.TargetMessage = null;
        conversation.TargetMode = MessageTargetMode.None;
    }

    public void ApplyRemotePresence(ulong userId, PresenceStatus status)
    {
        PersonPresentation? person = FindPerson(userId);
        if (person is not null)
            person.Presence.Status = status;
    }

    public void ApplyRemoteMessage(
        ulong conversationId,
        Guid messageId,
        ulong authorId,
        string body,
        DateTimeOffset createdAt)
    {
        ConversationPresentation conversation = EnsureConversation(conversationId, authorId);
        if (conversation.Messages.Any(message => message.Id == messageId))
            return;

        PersonPresentation author = FindPerson(authorId) ?? CurrentUser;
        conversation.Messages.Add(new MessagePresentation
        {
            Id = messageId,
            Author = author,
            SentAt = createdAt,
            IsOutgoing = author.Id == CurrentUser.Id,
            Body = body
        });
    }

    public CallSessionPresentation GetOrCreateCallSession(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return CallSessions.FirstOrDefault(session => session.ConversationId == conversationId)
            ?? AddCallSession(conversationId);
    }

    public void ApplyCallSignal(
        string eventType,
        string conversationId,
        string? sdp,
        string? candidate,
        string? reason)
    {
        GetOrCreateCallSession(conversationId).Apply(eventType, sdp, candidate, reason);
    }

    public CallSessionPresentation BeginOutgoingCall(string conversationId)
    {
        CallSessionPresentation session = GetOrCreateCallSession(conversationId);
        session.SetLocalState(CallSessionState.Ringing);
        return session;
    }

    private CallSessionPresentation AddCallSession(string conversationId)
    {
        CallSessionPresentation session = new() { ConversationId = conversationId };
        CallSessions.Add(session);
        return session;
    }

    private ConversationPresentation EnsureConversation(ulong conversationId, ulong authorId)
    {
        ConversationPresentation? existing = Conversations.FirstOrDefault(item => item.Id == conversationId);
        if (existing is not null)
            return existing;

        PersonPresentation author = FindPerson(authorId) ?? new PersonPresentation
        {
            Id = authorId,
            Name = $"User {authorId}",
            Username = $"user.{authorId}",
            Avatar = "",
            Presence = new PresencePresentation { Status = PresenceStatus.Online }
        };
        ConversationPresentation conversation = new()
        {
            Id = conversationId,
            Name = author.Name,
            Topic = "",
            IsGroup = false,
            Recipient = author
        };
        conversation.Participants.Add(CurrentUser);
        if (author.Id != CurrentUser.Id)
            conversation.Participants.Add(author);
        Conversations.Add(conversation);
        return conversation;
    }

    private PersonPresentation? FindPerson(ulong id)
    {
        if (CurrentUser.Id == id)
            return CurrentUser;

        return Conversations
            .SelectMany(conversation => conversation.Participants)
            .Concat(ContactGroups.SelectMany(group => group.Items.Select(item => item.Person)))
            .FirstOrDefault(person => person.Id == id);
    }

    private bool IsLocalMessage(MessagePresentation message) =>
        message.IsOutgoing && message.Author.Id == CurrentUser.Id;

    private static bool Matches(PersonPresentation person, string query) =>
        person.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        person.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        person.Presence.Activity.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        person.Presence.CustomStatus.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static Guid CreateMessageId(ulong conversationId, int ordinal) =>
        Guid.ParseExact($"{conversationId:x16}{ordinal:x8}00000000", "N");

    private static void ClearTarget(ConversationPresentation conversation)
    {
        conversation.TargetMessage = null;
        conversation.TargetMode = MessageTargetMode.None;
    }
}

public sealed class AdImagePresentation
{
    public required string Image { get; init; }
    public required string Url { get; init; }
    public required string ImageType { get; init; }
    public int AnimationFrames { get; init; }
    public int AnimationFramerate { get; init; }
}

public sealed class AdImagePresentationConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AdPresentation ad)
            return null;

        AdImageType imageType = ad.ImageType;
        if (imageType == AdImageType.StaticImage &&
            ad.ImageUri.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            imageType = AdImageType.Gif;
        }

        return new AdImagePresentation
        {
            Image = ad.ImageUri,
            Url = ad.ImageUri,
            ImageType = imageType.ToString(),
            AnimationFrames = ad.AnimationFrames,
            AnimationFramerate = ad.AnimationFramerate
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
