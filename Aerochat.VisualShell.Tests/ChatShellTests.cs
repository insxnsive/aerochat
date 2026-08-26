using System.IO;
using Aerochat.Presentation;
using Aerochat.Connectivity;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aerochat.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class ChatShellTests
{
    [Test]
    public async Task Chat_live_send_posts_rest_body_without_optimistic_append()
    {
        await Task.Run(() => WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation source = state.Conversations[0];
            ConversationPresentation conversation = new()
            {
                Id = source.Id,
                Name = source.Name,
                Topic = source.Topic,
                IsGroup = source.IsGroup,
                IsServerBacked = true,
                Recipient = source.Recipient
            };
            conversation.Participants.Add(state.CurrentUser);
            var handler = new RecordingHandler();
            var client = new ChatMessageClient(new HttpClient(handler),
                new Uri("http://localhost:5080/"), "session-token");
            var chat = new Chat(state, conversation, new WindowNavigator(state), client);
            conversation.Draft = "sent live";

            chat.SendDraftAsync().GetAwaiter().GetResult();

            Assert.That(handler.Request, Is.Not.Null);
            Assert.That(handler.Request!.Headers.Authorization!.Parameter, Is.EqualTo("session-token"));
            Assert.That(handler.Body, Does.Contain("\"body\":\"sent live\""));
            Assert.That(handler.Body, Does.Contain("\"kind\":\"message\""));
            Assert.That(conversation.Messages, Is.Empty);
            chat.Close();
        }));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
    [Test]
    public void Chat_constructs_with_sample_messages_without_network_client()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));

            Assert.That(chat.DataContext, Is.SameAs(conversation));
            Assert.That(conversation.Messages, Is.Not.Empty);

            chat.Close();
        });
    }

    [Test]
    public void Navigator_creates_chat_from_a_conversation_payload()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var navigator = new WindowNavigator(state);

            var chat = (Chat)navigator.Create(ShellRoute.Chat, conversation);

            Assert.That(chat.State, Is.SameAs(state));
            Assert.That(chat.Navigator, Is.SameAs(navigator));
            Assert.That(chat.Conversation, Is.SameAs(conversation));
            Assert.That(chat.DataContext, Is.SameAs(conversation));

            chat.Close();
        });
    }

    [Test]
    public void Navigator_creates_chat_from_a_conversation_id_lookup()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var navigator = new WindowNavigator(state);

            var chat = (Chat)navigator.Create(ShellRoute.Chat, conversation.Id);

            Assert.That(chat.Conversation, Is.SameAs(conversation));
            Assert.That(chat.DataContext, Is.SameAs(conversation));

            chat.Close();
        });
    }

    [Test]
    public void Reply_and_edit_change_only_local_conversation_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages[0];

        state.BeginReply(conversation, target);
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.Reply));

        conversation.Draft = "Reply text";
        MessagePresentation? reply = state.SendDraft(
            conversation,
            new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero));

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.ReplyTo, Is.SameAs(target));

        state.BeginEdit(conversation, reply);
        conversation.Draft = "Edited locally";
        state.CommitEdit(conversation);

        Assert.That(reply.Body, Is.EqualTo("Edited locally"));
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
    }

    [Test]
    public void Cancel_target_clears_local_reply_or_edit_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages[0];

        state.BeginReply(conversation, target);
        conversation.Draft = "draft";
        state.CancelTarget(conversation);

        Assert.Multiple(() =>
        {
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.Draft, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void Chat_xaml_rebinds_to_presentation_state_without_visual_drift()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Not.Contain("ChatWindowViewModel"));
            Assert.That(xaml, Does.Not.Contain("xmlns:viewmodels"));
            Assert.That(xaml, Does.Not.Contain("Theme.Scene"));
            Assert.That(xaml, Does.Not.Contain("VoiceManager"));
            Assert.That(xaml, Does.Not.Contain("Channel."));
            Assert.That(xaml, Does.Not.Contain("Guild."));
            Assert.That(xaml, Does.Not.Contain("Loading"));
            Assert.That(xaml, Does.Not.Contain("SettingsManager"));
            Assert.That(xaml, Does.Not.Contain("TypingString"));
            Assert.That(xaml, Does.Not.Contain("LastReceivedMessage"));
            Assert.That(xaml, Does.Not.Contain("MessageEntity"));
            Assert.That(xaml, Does.Not.Contain("TimestampString"));
            Assert.That(xaml, Does.Not.Contain("ReplyMessage"));
            Assert.That(xaml, Does.Not.Contain("ItemsSource=\"{Binding Attachments}\""));

            Assert.That(xaml, Does.Contain("xmlns:presentation=\"clr-namespace:Aerochat.Presentation\""));
            Assert.That(xaml, Does.Contain("d:DataContext=\"{d:DesignInstance Type=presentation:ConversationPresentation}\""));
            Assert.That(xaml, Does.Contain("State.CurrentUser"));
            Assert.That(xaml, Does.Contain("State.CurrentScene"));
            Assert.That(xaml, Does.Contain("State.Settings"));
            Assert.That(xaml, Does.Contain("Binding Name"));
            Assert.That(xaml, Does.Contain("Binding Topic"));
            Assert.That(xaml, Does.Contain("Binding IsGroup"));
            Assert.That(xaml, Does.Contain("Binding Recipient"));
            Assert.That(xaml, Does.Contain("Participants"));
            Assert.That(xaml, Does.Contain("ItemsSource=\"{Binding Path=Messages"));
            Assert.That(xaml, Does.Contain("Draft"));
            Assert.That(xaml, Does.Contain("Binding TypingText"));
            Assert.That(xaml, Does.Contain("Binding TargetMessage"));
            Assert.That(xaml, Does.Contain("Binding TargetMode"));
            Assert.That(xaml, Does.Contain("Binding Body"));
            Assert.That(xaml, Does.Contain("Binding Author"));
            Assert.That(xaml, Does.Contain("Binding SentAt"));
            Assert.That(xaml, Does.Contain("Binding IsOutgoing"));
            Assert.That(xaml, Does.Contain("Binding AttachmentUri"));
            Assert.That(xaml, Does.Contain("Binding ReplyTo"));

            Assert.That(xaml, Does.Contain("Height=\"466\" Width=\"587\""));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/Background.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/TopBarBg.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/InputBackground.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/BottomToolbar.png"));
            Assert.That(xaml, Does.Contain("x:Name=\"PART_AttachmentEditorGrid\""));
            Assert.That(xaml, Does.Contain("x:Name=\"PART_ReplyTargetContainer\""));
            Assert.That(xaml, Does.Contain("x:Name=\"DrawingContainer\""));
            Assert.That(xaml, Does.Contain("ToolbarClick"));
            Assert.That(xaml, Does.Contain("MessageTextBox_PreviewKeyDown"));
            Assert.That(xaml, Does.Contain("DrawOnClickUndo"));
            Assert.That(xaml, Does.Contain("OpenEmojiFlyout"));
            Assert.That(xaml, Does.Contain("Window_SizeChanged"));

            string[] forbiddenBindings =
            [
                "Loading", "TypingString", "LastReceivedMessage", "MessageEntity",
                "TimestampString", "ReplyMessage", "IsAuthorCurrentUser",
                "Ephemeral", "Special", "HiddenInfo", "IsReply", "IsSelectedForUiAction"
            ];
            Assert.That(forbiddenBindings.Where(xaml.Contains), Is.Empty,
                "Stale Chat bindings: " + string.Join(", ", forbiddenBindings.Where(xaml.Contains)));
        });
    }

    [Test]
    public void Chat_xaml_handlers_are_defined_by_the_local_codebehind()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
        string codeBehind = File.ReadAllText(GetChatPath("Chat.xaml.cs"));
        string[] attributes =
        [
            "Click", "PreviewMouseDown", "PreviewMouseUp", "MouseDown", "MouseUp",
            "MouseMove", "PreviewMouseMove", "MouseLeftButtonDown", "MouseRightButtonUp",
            "Drop", "Loaded", "SizeChanged", "PreviewKeyDown", "ScrollChanged",
            "ContextMenuOpening", "TextChanged", "LostFocus", "Closed"
        ];
        var referenced = attributes
            .SelectMany(attribute => System.Text.RegularExpressions.Regex.Matches(
                xaml, $@"(?<![\\w]){attribute}=""([A-Za-z_]\\w*)""")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missing = referenced.Where(handler => !System.Text.RegularExpressions.Regex.IsMatch(
            codeBehind, $@"\b(?:private|public|protected|internal)\s+(?:async\s+)?(?:Task|void|bool|[A-Za-z_]\w*[<>?]*)\s+{handler}\s*\("))
            .ToArray();
        Assert.That(missing, Is.Empty, "Missing Chat handlers: " + string.Join(", ", missing));
    }

    private static string GetChatPath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/Windows",
            fileName));
}
