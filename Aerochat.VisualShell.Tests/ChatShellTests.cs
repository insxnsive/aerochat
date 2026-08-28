using System.IO;
using Aerochat.Presentation;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Rtc;
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
            var wireId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
            ConversationPresentation conversation = new()
            {
                Id = source.Id,
                WireId = wireId.ToString("D"),
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
            Assert.That(handler.Request!.RequestUri!.AbsolutePath,
                Is.EqualTo($"/conversations/{wireId:D}/messages"));
            Assert.That(handler.Request.Headers.Authorization!.Parameter, Is.EqualTo("session-token"));
            Assert.That(handler.Body, Does.Contain("\"body\":\"sent live\""));
            Assert.That(handler.Body, Does.Contain("\"kind\":\"message\""));
            Assert.That(conversation.Messages, Is.Empty);
            chat.Close();
        }));
    }

    [Test]
    public async Task Chat_live_sticker_posts_kind_and_ref_payload_without_optimistic_append()
    {
        await Task.Run(() => WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation source = state.Conversations[0];
            var conversation = new ConversationPresentation
            {
                Id = source.Id, Name = source.Name, Topic = source.Topic, IsGroup = source.IsGroup,
                IsServerBacked = true, Recipient = source.Recipient
            };
            var handler = new RecordingHandler();
            var client = new ChatMessageClient(new HttpClient(handler), new Uri("http://localhost:5080/"), "session-token");
            var chat = new Chat(state, conversation, new WindowNavigator(state), client);

            chat.SelectStickerAsync(StickerCatalog.Items[0]).GetAwaiter().GetResult();

            using JsonDocument request = JsonDocument.Parse(handler.Body);
            JsonElement root = request.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("sticker"));
                Assert.That(root.GetProperty("refPayloadJson").GetString(),
                    Is.EqualTo(StickerCatalog.Items[0].RefPayloadJson));
                Assert.That(conversation.Messages, Is.Empty);
            });
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
    public void Chat_routes_call_controls_through_injected_coordinator_and_disposes_it_on_close()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var calls = new RecordingCallCoordinator(state.GetOrCreateCallSession(conversation.Id.ToString()));
            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);

            chat.StartCallAsync().GetAwaiter().GetResult();
            chat.AcceptCallAsync().GetAwaiter().GetResult();
            chat.ToggleMuteCall();
            chat.HangupCallAsync().GetAwaiter().GetResult();
            chat.Close();

            Assert.Multiple(() =>
            {
                Assert.That(calls.StartCount, Is.EqualTo(1));
                Assert.That(calls.AcceptCount, Is.EqualTo(1));
                Assert.That(calls.ToggleMuteCount, Is.EqualTo(1));
                Assert.That(calls.HangupCount, Is.EqualTo(1));
                Assert.That(calls.DisposeCount, Is.EqualTo(1));
            });
        });
    }

    [Test]
    public void Chat_shows_the_retained_notification_surface_for_an_incoming_call()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var calls = new RecordingCallCoordinator(state.GetOrCreateCallSession(conversation.Id.ToString()));
            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);

            calls.Session.Apply("call.ring", null, null, null);
            chat.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            Assert.Multiple(() =>
            {
                Assert.That(chat.ActiveCallNotification, Is.Not.Null);
                Assert.That(chat.ActiveCallNotification!.ViewModel.Type, Is.EqualTo(2));
                Assert.That(chat.ActiveCallNotification.State, Is.EqualTo(NotificationState.Open));
            });

            chat.Close();
        });
    }

    [Test]
    public void Chat_restores_an_incoming_call_notification_when_opened_after_the_ring()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var calls = new RecordingCallCoordinator(state.GetOrCreateCallSession(conversation.Id.ToString()));
            calls.Session.Apply("call.ring", null, null, null);

            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);

            Assert.That(chat.ActiveCallNotification, Is.Not.Null);
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
            Assert.That(xaml, Does.Contain("Message=\"{Binding}\""));

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
            Assert.That(xaml, Does.Contain("StickerFlyout"));
            Assert.That(xaml, Does.Contain("StickerItemsControl"));
            Assert.That(xaml, Does.Contain("StickerButtonGrid"));
            Assert.That(xaml, Does.Contain("StickerBox_Click"));
            Assert.That(xaml, Does.Contain("OpenStickerFlyout"));
            Assert.That(xaml, Does.Contain("Resources/Emoji/Heart.png"));
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
    public void Chat_xaml_uses_the_presentation_message_shape_without_legacy_visual_artifacts()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Not.Contain("ItemsSource=\"{Binding Body}\""),
                "A message body is text, not an embed collection to enumerate per character.");
            Assert.That(xaml, Does.Not.Contain("ReplyTo.Message"));
            Assert.That(xaml, Does.Not.Contain("Author.Image"));
            Assert.That(xaml, Does.Not.Contain("StringFormat=\"{}#{0}\""),
                "Direct-message headers must not be rendered as channels.");
            Assert.That(xaml, Does.Not.Contain("Binding TargetMode}\" Value=\"True\""));
            Assert.That(xaml, Does.Not.Contain("Binding TargetMode}\" Value=\"False\""));
            Assert.That(xaml, Does.Contain("Binding StickerUri"));
            Assert.That(xaml, Does.Contain("Binding Author.Avatar"));
            Assert.That(xaml, Does.Contain(
                "Text=\"{Binding SentAt, Converter={StaticResource TimeFormatConverter}}\""));
        });
    }

    [Test]
    public void Chat_attachment_editor_starts_collapsed_and_opens_on_demand()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            var row = (System.Windows.Controls.RowDefinition)chat.FindName("PART_AttachmentEditorRowDefinition");
            var grid = (System.Windows.Controls.Grid)chat.FindName("PART_AttachmentEditorGrid");

            Assert.Multiple(() =>
            {
                Assert.That(row.Height.Value, Is.EqualTo(0));
                Assert.That(grid.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
            });

            chat.OpenAttachmentsFilePicker();

            Assert.Multiple(() =>
            {
                Assert.That(row.Height.Value, Is.EqualTo(64));
                Assert.That(grid.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_composer_updates_the_conversation_draft_as_the_user_types()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");

            var range = new System.Windows.Documents.TextRange(
                composer.Document.ContentStart,
                composer.Document.ContentEnd);
            range.Text = "typed in the real composer";

            Assert.That(conversation.Draft, Is.EqualTo("typed in the real composer"));
            chat.Close();
        });
    }

    [Test]
    public void Chat_loaded_composer_has_a_usable_editing_surface()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");

            Assert.Multiple(() =>
            {
                Assert.That(composer.ActualHeight, Is.GreaterThanOrEqualTo(30));
                Assert.That(composer.IsEnabled, Is.True);
                Assert.That(composer.IsHitTestVisible, Is.True);
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_loaded_reply_composer_retains_a_usable_editing_surface()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            state.BeginReply(conversation, conversation.Messages[0]);
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var replyTarget = (System.Windows.Controls.Grid)chat.FindName("PART_ReplyTargetContainer");
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");

            Assert.Multiple(() =>
            {
                Assert.That(replyTarget.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
                Assert.That(replyTarget.ActualHeight, Is.GreaterThanOrEqualTo(24));
                Assert.That(composer.ActualHeight, Is.GreaterThanOrEqualTo(30));
                Assert.That(composer.IsEnabled, Is.True);
                Assert.That(composer.IsHitTestVisible, Is.True);
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_open_hydrates_the_composer_from_a_preexisting_conversation_draft()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            conversation.Draft = "draft saved before Chat opened";
            var chat = new Chat(state, conversation, new WindowNavigator(state));

            chat.Show();
            chat.UpdateLayout();
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");
            string composerText = new System.Windows.Documents.TextRange(
                composer.Document.ContentStart,
                composer.Document.ContentEnd).Text
                .TrimEnd(Environment.NewLine.ToCharArray());

            Assert.That(composerText, Is.EqualTo(conversation.Draft));
            chat.Close();
        });
    }

    [Test]
    public void Chat_smiley_toolbar_path_opens_the_emoji_flyout()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var smileyButton = (System.Windows.UIElement)chat.FindName("EmojiButtonGrid");

            smileyButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseUpEvent
            });

            var flyout = (System.Windows.Controls.Primitives.Popup)chat.FindName("EmojiFlyout");
            Assert.That(flyout.IsOpen, Is.True);
            chat.Close();
        });
    }

    [Test]
    public void Chat_emoji_selection_inserts_its_shortcode_and_closes_the_flyout()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var emoji = chat.FindName("SmileEmojiBox") as System.Windows.Controls.Border;
            Assert.That(emoji, Is.Not.Null);

            ((System.Windows.Controls.Primitives.Popup)chat.FindName("EmojiFlyout")).IsOpen = true;
            emoji!.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent
            });

            Assert.Multiple(() =>
            {
                Assert.That(conversation.Draft, Is.EqualTo(":smile:"));
                Assert.That(((System.Windows.Controls.Primitives.Popup)chat.FindName("EmojiFlyout")).IsOpen, Is.False);
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_drawing_toolbar_exposes_the_local_color_picker_route()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
        string codeBehind = File.ReadAllText(GetChatPath("Chat.xaml.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain("x:Name=\"DrawingColorButton\""));
            Assert.That(codeBehind, Does.Contain("new ColorPicker"));
            Assert.That(codeBehind, Does.Contain("DrawingCanvas.DefaultDrawingAttributes.Color"));
        });
    }

    [Test]
    public void Chat_switching_composer_modes_toggles_the_drawing_and_text_surfaces()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");
            var drawing = (System.Windows.Controls.Grid)chat.FindName("DrawingContainer");
            var drawButton = (System.Windows.UIElement)chat.FindName("SwitchToDraw");
            var textButton = (System.Windows.UIElement)chat.FindName("SwitchToText");

            drawButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseUpEvent
            });

            Assert.Multiple(() =>
            {
                Assert.That(drawing.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
                Assert.That(composer.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
            });

            textButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseUpEvent
            });

            Assert.Multiple(() =>
            {
                Assert.That(drawing.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(composer.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });
            chat.Close();
        });
    }

    [Test]
    public void Group_chat_renders_rows_for_each_conversation_participant()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation source = state.Conversations.First(item => item.IsGroup);
            ConversationPresentation conversation = new()
            {
                Id = source.Id,
                Name = "Participants-only visual regression",
                Topic = "",
                IsGroup = true
            };
            foreach (PersonPresentation participant in source.Participants)
                conversation.Participants.Add(participant);

            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            string[] renderedText = VisualDescendants<System.Windows.Controls.TextBlock>(chat)
                .Select(textBlock => textBlock.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.That(
                conversation.Participants.Select(participant => participant.Name),
                Is.SubsetOf(renderedText));
            chat.Close();
        });
    }

    [TestCase(CallSessionState.Ended)]
    [TestCase(CallSessionState.Failed)]
    public void Chat_clears_the_active_call_notification_when_the_session_terminates(
        CallSessionState terminalState)
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            CallSessionPresentation session = state.GetOrCreateCallSession(conversation.Id.ToString());
            var calls = new RecordingCallCoordinator(session);
            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);
            session.Apply("call.ring", null, null, null);
            var notification = chat.ActiveCallNotification;

            if (terminalState == CallSessionState.Ended)
                session.End("remote ended");
            else
                session.Fail("remote failed");
            chat.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.Multiple(() =>
            {
                Assert.That(notification, Is.Not.Null);
                Assert.That(notification!.IsVisible, Is.False);
                Assert.That(chat.ActiveCallNotification, Is.Null);
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_clears_the_composer_after_a_local_message_is_sent()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations.First(item => !item.IsGroup);
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            var composer = (System.Windows.Controls.RichTextBox)chat.FindName("MessageTextBox");
            var range = new System.Windows.Documents.TextRange(
                composer.Document.ContentStart,
                composer.Document.ContentEnd);
            range.Text = "sent from the composer";

            chat.SendDraftAsync().GetAwaiter().GetResult();

            string remainingText = new System.Windows.Documents.TextRange(
                composer.Document.ContentStart,
                composer.Document.ContentEnd).Text.TrimEnd(Environment.NewLine.ToCharArray());
            Assert.Multiple(() =>
            {
                Assert.That(conversation.Messages[^1].Body, Is.EqualTo("sent from the composer"));
                Assert.That(conversation.Draft, Is.Empty);
                Assert.That(remainingText, Is.Empty);
            });
            chat.Close();
        });
    }

    [Test]
    public void Chat_attachment_route_opens_the_matching_packaged_image_preview()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations.Single(item => item.IsGroup);
            MessagePresentation message = conversation.Messages.Single(item => item.AttachmentUri is not null);
            var chat = new Chat(state, conversation, new WindowNavigator(state));

            var previewer = chat.OpenAttachmentPreview(message);

            Assert.Multiple(() =>
            {
                Assert.That(previewer.IsVisible, Is.True);
                Assert.That(previewer.Preview.SourceUri, Is.EqualTo(message.AttachmentUri));
                Assert.That(previewer.Preview, Is.SameAs(
                    state.PreviewImages.Single(preview => preview.SourceUri == message.AttachmentUri)));
                string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
                Assert.That(xaml, Does.Contain(
                    "Source=\"{Binding AttachmentUri}\" MaxWidth=\"360\" MaxHeight=\"220\" HorizontalAlignment=\"Left\" Stretch=\"Uniform\" Margin=\"0,3,0,5\" Cursor=\"Hand\" MouseLeftButtonDown=\"OpenMedia\""));
            });

            previewer.Close();
            chat.Close();
        });
    }

    [Test]
    public void Chat_xaml_uses_the_local_eraser_enum_name()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Not.Contain("Value=\"Kesigomu\""));
            Assert.That(xaml, Does.Contain(
                "Binding DrawingTool, RelativeSource={RelativeSource AncestorType=local:Chat}}\" Value=\"Eraser\""));
        });
    }

    [Test]
    public void Direct_chat_shows_the_retained_two_person_profile_panel()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations.First(item => !item.IsGroup);
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            chat.Show();
            chat.UpdateLayout();
            var panel = chat.FindName("DirectConversationProfilePanel") as System.Windows.Controls.Grid;

            Assert.Multiple(() =>
            {
                Assert.That(panel, Is.Not.Null);
                Assert.That(panel?.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });
            chat.Close();
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

    [Test]
    public void Chat_close_does_not_block_the_dispatcher_on_async_call_cleanup()
    {
        string codeBehind = File.ReadAllText(GetChatPath("Chat.xaml.cs"));

        Assert.That(codeBehind, Does.Not.Contain("GetAwaiter().GetResult()"));
    }

    [Test]
    public void Chat_observes_call_action_failures_instead_of_letting_event_handlers_crash()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var calls = new RecordingCallCoordinator(
                state.GetOrCreateCallSession(conversation.Id.ToString()),
                failStart: true);
            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);

            chat.RunCallActionAsync(() => chat.StartCallAsync()).GetAwaiter().GetResult();

            Assert.That(chat.LastCallError, Is.TypeOf<InvalidOperationException>());
            chat.Close();
        });
    }

    [Test]
    public void Chat_call_bar_presents_explicit_session_states()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations.First(item => !item.IsGroup);
            CallSessionPresentation session = state.GetOrCreateCallSession(conversation.Id.ToString());
            var calls = new RecordingCallCoordinator(session);
            var chat = new Chat(state, conversation, new WindowNavigator(state), liveMessages: null, calls);
            chat.Show();
            chat.UpdateLayout();

            var status = chat.FindName("CallStatusTextBlock") as System.Windows.Controls.TextBlock;
            var start = (System.Windows.Controls.Button)chat.FindName("StartCallButton");
            var accept = (System.Windows.Controls.Button)chat.FindName("AcceptCallButton");
            var mute = (System.Windows.Controls.Button)chat.FindName("MuteCallButton");
            var leave = (System.Windows.Controls.Image)chat.FindName("LeaveCallButton");

            Assert.That(status, Is.Not.Null);
            Assert.That(status!.Text, Is.EqualTo("Voice call"));

            session.BeginOutgoing();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.That(status.Text, Is.EqualTo("Starting call…"));

            session.SetLocalState(CallSessionState.Ringing);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Multiple(() =>
            {
                Assert.That(status!.Text, Is.EqualTo($"Calling {conversation.Name}…"));
                Assert.That(start.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(mute.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(leave.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });

            session.SetLocalState(CallSessionState.Offering);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Multiple(() =>
            {
                Assert.That(status.Text, Is.EqualTo($"Waiting for {conversation.Name}…"));
                Assert.That(accept.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
            });

            session.Apply("call.offer", "remote-offer", null, null);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Multiple(() =>
            {
                Assert.That(session.State, Is.EqualTo(CallSessionState.Incoming));
                Assert.That(accept.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });

            session.SetLocalState(CallSessionState.Offering);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.That(accept.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));

            session.SetLocalState(CallSessionState.Connecting);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Multiple(() =>
            {
                Assert.That(status!.Text, Is.EqualTo("Connecting…"));
                Assert.That(mute.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(leave.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });

            session.SetLocalState(CallSessionState.Connected);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Multiple(() =>
            {
                Assert.That(status.Text, Is.EqualTo("Connected"));
                Assert.That(mute.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
                Assert.That(leave.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
            });

            chat.ToggleMuteCall();
            Assert.Multiple(() =>
            {
                Assert.That(status.Text, Is.EqualTo("Connected · Muted"));
                Assert.That(mute.Content, Is.EqualTo("Unmute"));
            });
            chat.ToggleMuteCall();
            Assert.Multiple(() =>
            {
                Assert.That(status.Text, Is.EqualTo("Connected"));
                Assert.That(mute.Content, Is.EqualTo("Mute"));
            });

            session.Fail("Call setup failed");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
            System.Text.RegularExpressions.Match callBar = System.Text.RegularExpressions.Regex.Match(
                xaml,
                "<StackPanel x:Name=\"CallBar\".*?</StackPanel>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.Multiple(() =>
            {
                Assert.That(status.Text, Is.EqualTo("Call failed"));
                Assert.That(start.Visibility, Is.EqualTo(System.Windows.Visibility.Visible));
                Assert.That(mute.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(leave.Visibility, Is.EqualTo(System.Windows.Visibility.Collapsed));
                Assert.That(callBar.Success, Is.True);
                Assert.That(callBar.Value, Does.Not.Contain("<controls:AudioPlayer"));
            });
            chat.Close();
        });
    }

    [Test]
    public void Application_composition_injects_one_call_coordinator_per_chat()
    {
        string appCode = File.ReadAllText(Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/App.xaml.cs")));
        System.Text.RegularExpressions.Match chatFactory = System.Text.RegularExpressions.Regex.Match(
            appCode,
            @"new Chat\(.*?\)\);",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Multiple(() =>
        {
            Assert.That(chatFactory.Success, Is.True);
            Assert.That(chatFactory.Value, Does.Contain("CreateCallCoordinator"));
            Assert.That(chatFactory.Value, Does.Not.Contain("CreateCallClient"));
            Assert.That(chatFactory.Value, Does.Not.Contain("new RtcPeerEngine"));
            Assert.That(appCode, Does.Contain("conversation.TransportId"));
        });
    }

    [Test]
    public void Chat_xaml_routes_outgoing_message_body_through_message_parser()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
        System.Text.RegularExpressions.Match parserTemplate = System.Text.RegularExpressions.Regex.Match(
            xaml,
            "<controls:MessageParser FontSize=\"13\" Grid.Column=\"1\".*?</controls:MessageParser>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Multiple(() =>
        {
            Assert.That(parserTemplate.Success, Is.True);
            Assert.That(parserTemplate.Value, Does.Not.Contain("Binding IsOutgoing"));
            Assert.That(xaml, Does.Not.Contain(
                "<TextBlock Margin=\"0,4,0,0\" FontSize=\"13\" Grid.Column=\"1\" HorizontalAlignment=\"Stretch\" TextTrimming=\"WordEllipsis\" Text=\"{Binding Body}\">"));
        });
    }

    [Test]
    public void Chat_xaml_preserves_special_message_colours_in_message_parser_style()
    {
        string xaml = File.ReadAllText(GetChatPath("Chat.xaml"));
        System.Text.RegularExpressions.Match parserTemplate = System.Text.RegularExpressions.Regex.Match(
            xaml,
            "<controls:MessageParser FontSize=\"13\" Grid.Column=\"1\".*?</controls:MessageParser>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Multiple(() =>
        {
            Assert.That(parserTemplate.Value, Does.Contain("Binding Body}\" Value=\"GuildMemberJoin"));
            Assert.That(parserTemplate.Value, Does.Contain("Binding Body}\" Value=\"RecipientRemove"));
            Assert.That(parserTemplate.Value, Does.Contain("Binding Body}\" Value=\"TierThreeUserPremiumGuildSubscription"));
        });
    }

    private sealed class RecordingCallCoordinator : ICallCoordinator
    {
        private readonly bool _failStart;

        public RecordingCallCoordinator(CallSessionPresentation session, bool failStart = false)
        {
            Session = session;
            _failStart = failStart;
        }

        public CallSessionPresentation Session { get; }
        public int StartCount { get; private set; }
        public int AcceptCount { get; private set; }
        public int ToggleMuteCount { get; private set; }
        public bool IsMuted { get; private set; }
        public int HangupCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (_failStart)
                throw new InvalidOperationException("call setup failed");
            return Task.CompletedTask;
        }

        public Task AcceptAsync(CancellationToken cancellationToken = default)
        {
            AcceptCount++;
            return Task.CompletedTask;
        }

        public void ToggleMute()
        {
            ToggleMuteCount++;
            IsMuted = !IsMuted;
        }

        public Task HangupAsync(string reason = "local hangup", CancellationToken cancellationToken = default)
        {
            HangupCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static string GetChatPath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/Windows",
            fileName));

    private static IEnumerable<T> VisualDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            System.Windows.DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (T descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }
}
