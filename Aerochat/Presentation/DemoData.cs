using System.Windows.Media;

namespace Aerochat.Presentation;

public static class DemoData
{
    private const string PlaceholderAvatar =
        "/Aerochat;component/Resources/Frames/PlaceholderPfp.png";

    public static PresentationState Create()
    {
        PersonPresentation nate = CreatePerson(
            1000, "Nate Rivera", "nate.rivera", PlaceholderAvatar,
            PresenceStatus.Online, "Building the visual shell", "Available");
        PersonPresentation maya = CreatePerson(
            1001, "Maya Chen", "maya.chen",
            "/Aerochat;component/Resources/Emoji/Camera.png",
            PresenceStatus.Online, "Reviewing scene concepts", "Ready to chat");
        PersonPresentation jordan = CreatePerson(
            1002, "Jordan Brooks", "jordan.brooks",
            "/Aerochat;component/Resources/Emoji/Music.png",
            PresenceStatus.Busy, "Listening to the launch mix", "Heads down");
        PersonPresentation sofia = CreatePerson(
            1003, "Sofia Alvarez", "sofia.alvarez",
            "/Aerochat;component/Resources/Emoji/Rainbow.png",
            PresenceStatus.Away, "Away from the keyboard", "Back after lunch");
        PersonPresentation elliot = CreatePerson(
            1004, "Elliot Park", "elliot.park",
            "/Aerochat;component/Resources/Emoji/Computer.png",
            PresenceStatus.Offline, "", "Offline");

        ScenePresentation defaultScene = new()
        {
            Id = 1,
            DisplayName = "Aerochat",
            File = "/Aerochat;component/Scenes/default.png",
            Color = Color.FromRgb(54, 139, 184),
            TextColor = Colors.White,
            ShadowColor = Color.FromArgb(150, 7, 35, 56),
            IsDefault = true
        };
        ScenePresentation blueWavesScene = new()
        {
            Id = 2,
            DisplayName = "Blue Waves",
            File = "/Aerochat;component/Scenes/BlueWaves.png",
            Color = Color.FromRgb(36, 103, 171),
            TextColor = Colors.White,
            ShadowColor = Color.FromArgb(170, 3, 28, 62)
        };
        ScenePresentation vistaScene = new()
        {
            Id = 3,
            DisplayName = "Vista Aurora",
            File = "/Aerochat;component/Scenes/Vista.png",
            Color = Color.FromRgb(80, 127, 76),
            TextColor = Colors.White,
            ShadowColor = Color.FromArgb(160, 19, 43, 17)
        };

        PresentationState state = new(
            nate,
            defaultScene,
            new VisualSettingsPresentation
            {
                ShowAds = true,
                ShowNews = true,
                ShowEyecandy = true,
                ShowTimestamps = true,
                EnableAnimations = true,
                Language = "English",
                TimeFormat = "h:mm tt"
            });

        state.Scenes.Add(defaultScene);
        state.Scenes.Add(blueWavesScene);
        state.Scenes.Add(vistaScene);

        ConversationPresentation directConversation = CreateDirectConversation(nate, maya);
        ConversationPresentation groupConversation = CreateGroupConversation(
            nate, jordan, sofia, elliot);
        state.Conversations.Add(directConversation);
        state.Conversations.Add(groupConversation);

        ContactGroupPresentation favorites = new() { Name = "Favorites" };
        favorites.Items.Add(new ContactPresentation
        {
            ConversationId = directConversation.Id,
            Person = maya
        });
        favorites.Items.Add(new ContactPresentation
        {
            ConversationId = groupConversation.Id,
            Person = jordan
        });

        ContactGroupPresentation conversations = new() { Name = "Conversations" };
        conversations.Items.Add(new ContactPresentation
        {
            ConversationId = groupConversation.Id,
            Person = sofia
        });

        ContactGroupPresentation servers = new() { Name = "Servers" };
        servers.Items.Add(new ContactPresentation
        {
            ConversationId = groupConversation.Id,
            Person = elliot,
            IsServer = true
        });

        state.ContactGroups.Add(favorites);
        state.ContactGroups.Add(conversations);
        state.ContactGroups.Add(servers);

        state.News.Add(new NewsPresentation(
            "Visual shell preview is ready",
            "Explore deterministic conversations, scenes, and local actions.",
            At(2026, 8, 24, 8, 0),
            Color.FromRgb(33, 117, 185)));
        state.News.Add(new NewsPresentation(
            "Aero themes return",
            "Classic glass, bright scenes, and expressive status details are back.",
            At(2026, 8, 23, 16, 30),
            Color.FromRgb(88, 149, 69)));

        state.Notices.Add(new NoticePresentation(
            "Local preview",
            "This visual shell uses fixed demo data and does not connect to a service.",
            At(2026, 8, 24, 8, 15),
            Color.FromRgb(236, 157, 44)));

        state.Ads.Add(new AdPresentation(
            "Bytemind",
            "/Aerochat;component/Ads/Bytemind.gif",
            "A tiny corner of the old web, restored for the Aero era.",
            Color.FromRgb(79, 111, 178)));
        state.Ads.Add(new AdPresentation(
            "Visit New Hampshire",
            "/Aerochat;component/Ads/visitnhrevise.gif",
            "A scenic break between conversations.",
            Color.FromRgb(74, 135, 99)));
        state.CurrentAd = state.Ads[0];

        state.PreviewImages.Add(new PreviewImagePresentation(
            "aerochat.png",
            "/Aerochat;component/Scenes/Aerochat.png",
            "Aerochat scene study"));
        state.PreviewImages.Add(new PreviewImagePresentation(
            "blue-waves.png",
            "/Aerochat;component/Scenes/BlueWaves.png",
            "Blue Waves scene study"));

        state.ApplySearch("");
        return state;
    }

    private static ConversationPresentation CreateDirectConversation(
        PersonPresentation currentUser,
        PersonPresentation recipient)
    {
        ConversationPresentation conversation = new()
        {
            Id = 2001,
            Name = recipient.Name,
            Topic = "Weekend design sync",
            IsGroup = false,
            Recipient = recipient,
            TypingText = "Maya is typing..."
        };
        conversation.Participants.Add(currentUser);
        conversation.Participants.Add(recipient);

        MessagePresentation first = CreateMessage(
            "00000001-0000-0000-0000-000000000001",
            recipient,
            At(2026, 8, 24, 9, 5),
            false,
            "The glass header is landing nicely.");
        MessagePresentation second = CreateMessage(
            "00000001-0000-0000-0000-000000000002",
            currentUser,
            At(2026, 8, 24, 9, 7),
            true,
            "Great. I will tighten the contact spacing next.");
        MessagePresentation third = CreateMessage(
            "00000001-0000-0000-0000-000000000003",
            recipient,
            At(2026, 8, 24, 9, 9),
            false,
            "That should make the Home view feel much calmer.",
            replyTo: second);
        MessagePresentation fourth = CreateMessage(
            "00000001-0000-0000-0000-000000000004",
            recipient,
            At(2026, 8, 24, 9, 12),
            false,
            "I saved the scene reference here.",
            "/Aerochat;component/Scenes/Aerochat.png");

        conversation.Messages.Add(first);
        conversation.Messages.Add(second);
        conversation.Messages.Add(third);
        conversation.Messages.Add(fourth);
        return conversation;
    }

    private static ConversationPresentation CreateGroupConversation(
        PersonPresentation currentUser,
        PersonPresentation jordan,
        PersonPresentation sofia,
        PersonPresentation elliot)
    {
        ConversationPresentation conversation = new()
        {
            Id = 2002,
            Name = "Visual Shell Crew",
            Topic = "Polishing the local Aerochat preview",
            IsGroup = true,
            TypingText = "Jordan and Sofia are typing..."
        };
        conversation.Participants.Add(currentUser);
        conversation.Participants.Add(jordan);
        conversation.Participants.Add(sofia);
        conversation.Participants.Add(elliot);

        MessagePresentation first = CreateMessage(
            "00000002-0000-0000-0000-000000000001",
            jordan,
            At(2026, 8, 24, 10, 0),
            false,
            "The busy, away, and offline states are all visible now.");
        MessagePresentation second = CreateMessage(
            "00000002-0000-0000-0000-000000000002",
            currentUser,
            At(2026, 8, 24, 10, 4),
            true,
            "Perfect. I am checking the reply and attachment states.");
        MessagePresentation third = CreateMessage(
            "00000002-0000-0000-0000-000000000003",
            sofia,
            At(2026, 8, 24, 10, 8),
            false,
            "Blue Waves makes a good attachment preview.",
            "/Aerochat;component/Scenes/BlueWaves.png",
            second);

        conversation.Messages.Add(first);
        conversation.Messages.Add(second);
        conversation.Messages.Add(third);
        return conversation;
    }

    private static PersonPresentation CreatePerson(
        ulong id,
        string name,
        string username,
        string avatar,
        PresenceStatus status,
        string activity,
        string customStatus) => new()
    {
        Id = id,
        Name = name,
        Username = username,
        Avatar = avatar,
        Presence = new PresencePresentation
        {
            Status = status,
            Activity = activity,
            CustomStatus = customStatus
        }
    };

    private static MessagePresentation CreateMessage(
        string id,
        PersonPresentation author,
        DateTimeOffset sentAt,
        bool isOutgoing,
        string body,
        string? attachmentUri = null,
        MessagePresentation? replyTo = null) => new()
    {
        Id = Guid.Parse(id),
        Author = author,
        SentAt = sentAt,
        IsOutgoing = isOutgoing,
        Body = body,
        AttachmentUri = attachmentUri,
        ReplyTo = replyTo
    };

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
