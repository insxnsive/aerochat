using Aerochat.Controls;
using Aerochat.Connectivity;
using Aerochat.Presentation;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aerochat.VisualShell.Tests;

public sealed class PresentationControlTests
{
    public enum LegacyStatus
    {
        Online,
        Idle,
        Away,
        DoNotDisturb,
        Busy,
        Invisible,
        Offline,
    }

    private sealed class NamedStatus
    {
        private readonly string _name;

        public NamedStatus(string name) => _name = name;

        public override string ToString() => _name;
    }

    private sealed class MessageModel
    {
        public MessageModel(string content, params TestChannel[] mentionedChannels)
        {
            Content = content;
            MentionedChannels = mentionedChannels;
        }

        public string Content { get; }

        public IReadOnlyList<TestChannel> MentionedChannels { get; }
    }

    public sealed class UntrustedStickerMessage
    {
        public string Kind => "sticker";

        public string StickerUri => "https://example.invalid/untrusted-sticker.png";

        public string Body => "untrusted sticker";
    }

    private sealed class TestChannel
    {
        public ulong Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    [TestCase(PresenceStatus.Online, ProfileFrameSize.Large, "LargeFrameActiveAnimation.png")]
    [TestCase(PresenceStatus.Busy, ProfileFrameSize.Small, "SmallFrameDndAnimation.png")]
    [TestCase(PresenceStatus.Away, ProfileFrameSize.ExtraSmall, "XSFrameIdle.png")]
    [TestCase(PresenceStatus.Offline, ProfileFrameSize.ExtraLarge, "XLFrameOffline.png")]
    public void Profile_frame_maps_local_presence_to_full_pack_uri(
        PresenceStatus status, ProfileFrameSize size, string expectedFile)
    {
        string expected = $"pack://application:,,,/Aerochat;component/Resources/Frames/{expectedFile}";

        Assert.That(ProfilePictureFrame.GetFrameUri(status, size).AbsoluteUri,
            Is.EqualTo(expected));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Profile_frame_pack_uri_loads_packaged_resource()
    {
        if (Application.Current is null)
        {
            _ = new Aerochat.App(suppressStartup: true);
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = ProfilePictureFrame.GetFrameUri(
            PresenceStatus.Online, ProfileFrameSize.Large);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();

        Assert.That(image.PixelWidth, Is.GreaterThan(0));
        Assert.That(image.PixelHeight, Is.GreaterThan(0));
    }

    [TestCase("Online", PresenceStatus.Online)]
    [TestCase("Idle", PresenceStatus.Away)]
    [TestCase("Away", PresenceStatus.Away)]
    [TestCase("DoNotDisturb", PresenceStatus.Busy)]
    [TestCase("Busy", PresenceStatus.Busy)]
    [TestCase("Invisible", PresenceStatus.Offline)]
    [TestCase("Offline", PresenceStatus.Offline)]
    public void Profile_frame_normalizes_legacy_status_strings(
        string boundStatus, PresenceStatus expected)
    {
        Assert.That(ProfilePictureFrame.NormalizeStatus(boundStatus), Is.EqualTo(expected));
    }

    [TestCase(LegacyStatus.Online, PresenceStatus.Online)]
    [TestCase(LegacyStatus.Idle, PresenceStatus.Away)]
    [TestCase(LegacyStatus.Away, PresenceStatus.Away)]
    [TestCase(LegacyStatus.DoNotDisturb, PresenceStatus.Busy)]
    [TestCase(LegacyStatus.Busy, PresenceStatus.Busy)]
    [TestCase(LegacyStatus.Invisible, PresenceStatus.Offline)]
    [TestCase(LegacyStatus.Offline, PresenceStatus.Offline)]
    public void Profile_frame_normalizes_legacy_status_enums(
        LegacyStatus boundStatus, PresenceStatus expected)
    {
        Assert.That(ProfilePictureFrame.NormalizeStatus(boundStatus), Is.EqualTo(expected));
    }

    [Test]
    public void Profile_frame_normalizes_named_legacy_status_objects()
    {
        Assert.That(ProfilePictureFrame.NormalizeStatus(new NamedStatus("DoNotDisturb")),
            Is.EqualTo(PresenceStatus.Busy));
        Assert.That(ProfilePictureFrame.NormalizeStatus(new NamedStatus("Invisible")),
            Is.EqualTo(PresenceStatus.Offline));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void User_status_dependency_property_accepts_legacy_bound_values()
    {
        Assert.That(ProfilePictureFrame.UserStatusProperty.PropertyType, Is.EqualTo(typeof(object)));

        var frame = new ProfilePictureFrame
        {
            FrameSize = ProfileFrameSize.ExtraSmall,
            EnableAnimation = false,
        };

        Assert.DoesNotThrow(() => frame.SetValue(ProfilePictureFrame.UserStatusProperty, "Idle"));
        Assert.That(ProfilePictureFrame.NormalizeStatus(frame.GetValue(ProfilePictureFrame.UserStatusProperty)),
            Is.EqualTo(PresenceStatus.Away));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Interop_context_menu_opens_on_left_click_when_configured()
    {
        EnsureWpfApplication();
        var owner = new Border();
        var menu = new InteropContextMenu
        {
            PlacementTarget = owner,
            OpenOn = EOpenOn.LeftClick,
        };

        RaiseMouseUp(owner, MouseButton.Left);

        Assert.That(menu.IsOpen, Is.True);
        menu.Close();
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Interop_context_menu_opens_on_right_click_when_configured()
    {
        EnsureWpfApplication();
        var owner = new Border();
        var menu = new InteropContextMenu
        {
            PlacementTarget = owner,
            OpenOn = EOpenOn.RightClick,
        };

        RaiseMouseUp(owner, MouseButton.Right);

        Assert.That(menu.IsOpen, Is.True);
        menu.Close();
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Interop_context_menu_does_not_open_when_open_on_is_none()
    {
        EnsureWpfApplication();
        var owner = new Border();
        var menu = new InteropContextMenu
        {
            PlacementTarget = owner,
            OpenOn = EOpenOn.None,
        };

        RaiseMouseUp(owner, MouseButton.Left);
        RaiseMouseUp(owner, MouseButton.Right);

        Assert.That(menu.IsOpen, Is.False);
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Interop_context_menu_rebinds_mouse_subscription_without_leaking_old_owner()
    {
        EnsureWpfApplication();
        var firstOwner = new Border();
        var secondOwner = new Border();
        var menu = new InteropContextMenu
        {
            OpenOn = EOpenOn.LeftClick,
            PlacementTarget = firstOwner,
        };

        RaiseMouseUp(firstOwner, MouseButton.Left);
        Assert.That(menu.IsOpen, Is.True);
        menu.Close();

        menu.PlacementTarget = secondOwner;
        RaiseMouseUp(firstOwner, MouseButton.Left);
        Assert.That(menu.IsOpen, Is.False);

        RaiseMouseUp(secondOwner, MouseButton.Left);
        Assert.That(menu.IsOpen, Is.True);
        menu.Close();
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Interop_context_menu_compatibility_population_overload_delegates_to_wpf_items()
    {
        EnsureWpfApplication();
        var child = new InteropMenuItem { Header = "Child" };
        var items = new List<InteropMenuItem>
        {
            new() { Header = "Parent", SubMenuItems = [child] },
            new() { Header = "Command" },
        };
        var menu = new InteropContextMenu();
        MethodInfo? compatibilityOverload = typeof(InteropContextMenu)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == nameof(InteropContextMenu.PopulateMenu)
                && method.GetParameters() is [{ ParameterType: var handleType }, { ParameterType: var itemsType }]
                && handleType == typeof(IntPtr)
                && itemsType == typeof(List<InteropMenuItem>));

        Assert.That(compatibilityOverload, Is.Not.Null);
        Assert.DoesNotThrow(() => compatibilityOverload!.Invoke(
            menu, [new IntPtr(0x1234), items]));
        Assert.That(menu.ContextMenuItems, Is.SameAs(items));
        Assert.That(menu.Items.Count, Is.EqualTo(2));
        Assert.That(((MenuItem)menu.Items[0]).Header, Is.EqualTo("Parent"));
        Assert.That(((MenuItem)menu.Items[0]).Items.Count, Is.EqualTo(1));
        Assert.That(((MenuItem)menu.Items[1]).Header, Is.EqualTo("Command"));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Unresolved_channel_mention_is_visible_text_without_hyperlink_event()
    {
        EnsureWpfApplication();
        var parser = new MessageParser();
        int eventCount = 0;
        parser.HyperlinkClicked += (_, _) => eventCount++;

        parser.Message = new MessageModel("before <#999999> after");

        TextBlock textBlock = RenderedText(parser);
        Assert.That(textBlock.Inlines.OfType<Hyperlink>(), Is.Empty);
        Assert.That(string.Concat(textBlock.Inlines.OfType<Run>().Select(run => run.Text)),
            Does.Contain("<#999999>"));
        Assert.That(eventCount, Is.Zero);
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Resolved_channel_mention_raises_presentation_event_with_channel_object()
    {
        EnsureWpfApplication();
        var channel = new TestChannel { Id = 42, Name = "general" };
        var parser = new MessageParser
        {
            Message = new MessageModel("go to <#42>", channel),
        };
        HyperlinkClickedEventArgs? clicked = null;
        parser.HyperlinkClicked += (_, args) => clicked = args;

        Hyperlink link = RenderedText(parser).Inlines.OfType<Hyperlink>().Single();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

        Assert.That(clicked, Is.Not.Null);
        Assert.That(clicked!.Type, Is.EqualTo(HyperlinkType.Channel));
        Assert.That(clicked.AssociatedObject, Is.SameAs(channel));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Unicode_emoji_uses_matching_packaged_wlm_asset()
    {
        EnsureWpfApplication();
        var parser = new MessageParser
        {
            Message = new MessageModel("hello 😀"),
        };

        Image image = RenderedText(parser).Inlines.OfType<InlineUIContainer>()
            .Select(container => container.Child).OfType<Image>().Single();
        var source = (BitmapImage)image.Source!;

        Assert.That(source.UriSource.AbsoluteUri, Does.EndWith("/Smile.png"));
        Assert.That(image.ToolTip, Is.EqualTo("😀"));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Sticker_message_renders_large_packaged_resource_image()
    {
        EnsureWpfApplication();
        StickerPresentation sticker = StickerCatalog.Items[0];
        var parser = new MessageParser
        {
            Message = new MessagePresentation
            {
                Id = Guid.NewGuid(),
                Author = DemoData.Create().CurrentUser,
                SentAt = DateTimeOffset.UtcNow,
                IsOutgoing = true,
                Body = sticker.ResourceName,
                Kind = "sticker",
                RefPayloadJson = sticker.RefPayloadJson
            }
        };

        Image image = parser.MainPanel.Children.OfType<Image>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(((BitmapImage)image.Source!).UriSource.AbsoluteUri, Is.EqualTo(sticker.ResourceUri));
            Assert.That(image.Width, Is.EqualTo(160));
        });
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Sticker_with_untrusted_absolute_uri_renders_as_inert_text()
    {
        EnsureWpfApplication();
        var parser = new MessageParser { Message = new UntrustedStickerMessage() };

        Assert.That(parser.MainPanel.Children.OfType<Image>(), Is.Empty);
        Assert.That(string.Concat(RenderedText(parser).Inlines.OfType<Run>().Select(run => run.Text)),
            Is.EqualTo("untrusted sticker"));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Locally_sent_shortcode_renders_through_the_message_presentation_body()
    {
        EnsureWpfApplication();
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        conversation.Draft = ":)";

        MessagePresentation sent = state.SendDraft(
            conversation,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero))!;
        var parser = new MessageParser { Message = sent };

        Image image = RenderedText(parser).Inlines.OfType<InlineUIContainer>()
            .Select(container => container.Child).OfType<Image>().Single();

        Assert.That(((BitmapImage)image.Source!).UriSource.AbsoluteUri, Does.EndWith("/Smile.png"));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Message_presentation_body_mutation_refreshes_existing_parser_output()
    {
        EnsureWpfApplication();
        PresentationState state = DemoData.Create();
        MessagePresentation message = state.Conversations[0].Messages[0];
        var parser = new MessageParser { Message = message };

        Assert.That(string.Concat(RenderedText(parser).Inlines.OfType<Run>().Select(run => run.Text)),
            Is.EqualTo(message.Body));

        message.Body = "edited after assignment";

        Assert.That(string.Concat(RenderedText(parser).Inlines.OfType<Run>().Select(run => run.Text)),
            Is.EqualTo("edited after assignment"));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Unloaded_parser_defers_message_mutation_until_loaded_again()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            MessagePresentation message = state.Conversations[0].Messages[0];
            var parser = new MessageParser { Message = message };
            string originalBody = message.Body;

            parser.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            message.Body = "edited while parser was unloaded";

            Assert.That(string.Concat(RenderedText(parser).Inlines.OfType<Run>().Select(run => run.Text)),
                Is.EqualTo(originalBody));

            parser.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Assert.That(string.Concat(RenderedText(parser).Inlines.OfType<Run>().Select(run => run.Text)),
                Is.EqualTo("edited while parser was unloaded"));
        });
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Disposed_parser_ignores_message_changes_and_queued_renders()
    {
        MessagePresentation? message = null;
        MessageParser? parser = null;

        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            message = state.Conversations[0].Messages[0];
            parser = new MessageParser { Message = message };
            parser.MainPanel.Children.Clear();

            Task queuedMutation = Task.Run(() => message.Body = "queued before dispose");
            queuedMutation.GetAwaiter().GetResult();
            parser.Dispose();
            parser.Message = new MessagePresentation
            {
                Id = Guid.NewGuid(),
                Author = state.CurrentUser,
                SentAt = DateTimeOffset.UtcNow,
                IsOutgoing = false,
                Body = "assigned after dispose"
            };
            message.Body = "changed after dispose";

            Assert.That(parser.MainPanel.Children, Is.Empty);
        });

        WpfTestHost.Run(() => Assert.That(parser!.MainPanel.Children, Is.Empty));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Disposed_parser_clears_current_message_and_rejects_later_assignments()
    {
        WpfTestHost.Run(() =>
        {
            var parser = new MessageParser
            {
                Message = new MessageModel("message before dispose")
            };

            Assert.That(parser.MainPanel.Children, Is.Not.Empty);

            parser.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(parser.Message, Is.Null);
                Assert.That(parser.ReadLocalValue(MessageParser.MessageProperty),
                    Is.SameAs(DependencyProperty.UnsetValue));
                Assert.That(parser.MainPanel.Children, Is.Empty);
            });

            parser.Message = new MessageModel("assigned through CLR property after dispose");
            Assert.Multiple(() =>
            {
                Assert.That(parser.Message, Is.Null);
                Assert.That(parser.ReadLocalValue(MessageParser.MessageProperty),
                    Is.SameAs(DependencyProperty.UnsetValue));
                Assert.That(parser.MainPanel.Children, Is.Empty);
            });

            parser.SetValue(MessageParser.MessageProperty,
                new MessageModel("assigned through SetValue after dispose"));
            Assert.Multiple(() =>
            {
                Assert.That(parser.Message, Is.Null);
                Assert.That(parser.ReadLocalValue(MessageParser.MessageProperty),
                    Is.SameAs(DependencyProperty.UnsetValue));
                Assert.That(parser.MainPanel.Children, Is.Empty);
            });
        });
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Special_message_parser_output_applies_the_parser_foreground()
    {
        EnsureWpfApplication();
        PresentationState state = DemoData.Create();
        var parser = new MessageParser { Foreground = Brushes.Maroon };
        parser.Message = new MessagePresentation
        {
            Id = Guid.NewGuid(),
            Author = state.CurrentUser,
            SentAt = DateTimeOffset.UtcNow,
            IsOutgoing = false,
            Body = "RecipientRemove"
        };

        TextBlock rendered = RenderedText(parser);

        Assert.That(((SolidColorBrush)rendered.Foreground).Color,
            Is.EqualTo(Color.FromRgb(0x80, 0x00, 0x00)));
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Gateway_shortcode_maps_into_presentation_and_renders_packaged_emoticon()
    {
        EnsureWpfApplication();
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        ConversationPresentation conversation = state.Conversations.Single(item => item.Id == 2001);

        const string wire =
            "{\"t\":\"message.created\",\"eventId\":\"hub:99\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"10000000-0000-0000-0000-000000000099\",\"authorId\":\"1001\",\"body\":\":D\",\"kind\":\"message\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        Assert.That(GatewayProtocol.TryParseFrame(wire, out GatewayFrame? frame), Is.True);
        Assert.That(GatewayProtocol.TryParseMessage(frame!.Data, out MessageCreatedEventArgs? incoming), Is.True);
        adapter.ApplyMessageCreated(incoming!);

        MessagePresentation received = conversation.Messages[^1];
        var parser = new MessageParser { Message = received };
        Image image = RenderedText(parser).Inlines.OfType<InlineUIContainer>()
            .Select(container => container.Child).OfType<Image>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(received.Body, Is.EqualTo(":D"));
            Assert.That(((BitmapImage)image.Source!).UriSource.AbsoluteUri, Does.EndWith("/Grin.png"));
        });
    }

    [Test, Apartment(ApartmentState.STA)]
    public void Unknown_unicode_emoji_remains_visible_text()
    {
        EnsureWpfApplication();
        var parser = new MessageParser
        {
            Message = new MessageModel("hello 🦄"),
        };

        TextBlock textBlock = RenderedText(parser);
        Assert.That(textBlock.Inlines.OfType<InlineUIContainer>(), Is.Empty);
        Assert.That(string.Concat(textBlock.Inlines.OfType<Run>().Select(run => run.Text)),
            Does.Contain("🦄"));
    }

    [TestCase(":)", "/Smile.png")]
    [TestCase(":D", "/Grin.png")]
    [TestCase(":(", "/Frown.png")]
    [TestCase(":P", "/Tongue.png")]
    [TestCase(";)", "/Wink.png")]
    [TestCase(":O", "/Surprise.png")]
    [Apartment(ApartmentState.STA)]
    public void Wlm_shortcode_uses_matching_packaged_asset(string shortcode, string expectedSuffix)
    {
        EnsureWpfApplication();
        var parser = new MessageParser
        {
            Message = new MessageModel($"hello {shortcode}"),
        };

        Image image = RenderedText(parser).Inlines.OfType<InlineUIContainer>()
            .Select(container => container.Child).OfType<Image>().Single();

        Assert.That(((BitmapImage)image.Source!).UriSource.AbsoluteUri, Does.EndWith(expectedSuffix));
        Assert.That(image.ToolTip, Is.EqualTo(shortcode));
    }

    [TestCase(":d")]
    [TestCase(":unknown:")]
    [Apartment(ApartmentState.STA)]
    public void Unknown_or_non_table_case_shortcode_remains_visible_text(string shortcode)
    {
        EnsureWpfApplication();
        var parser = new MessageParser
        {
            Message = new MessageModel($"hello {shortcode}"),
        };

        TextBlock textBlock = RenderedText(parser);
        Assert.That(textBlock.Inlines.OfType<InlineUIContainer>(), Is.Empty);
        Assert.That(string.Concat(textBlock.Inlines.OfType<Run>().Select(run => run.Text)),
            Does.Contain(shortcode));
    }

    [Test]
    public void Shared_controls_do_not_reference_backend_or_native_packages()
    {
        string root = RepositoryRoot.Path;
        string controls = Path.Combine(root, "Aerochat", "Controls");
        string[] forbidden = ["DSharpPlus", "Aerovoice", "SettingsManager",
            "Vanara", "DllImport", "PInvoke", "WebView2", "HttpClient",
            "Process.Start", "ShellExecute"];

        var offenders = Directory.EnumerateFiles(controls, "*.cs", SearchOption.AllDirectories)
            .Select(path => new { path, text = File.ReadAllText(path) })
            .SelectMany(file => forbidden.Where(file.text.Contains)
                .Select(token => $"{Path.GetRelativePath(root, file.path)}: {token}"))
            .ToArray();

        Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
    }

    private static void EnsureWpfApplication()
    {
        if (Application.Current is null)
        {
            _ = new Aerochat.App(suppressStartup: true);
        }
    }

    private static TextBlock RenderedText(MessageParser parser)
    {
        return (TextBlock)parser.MainPanel.Children.Cast<UIElement>().Single();
    }

    private static void RaiseMouseUp(UIElement owner, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = button == MouseButton.Left
                ? UIElement.PreviewMouseLeftButtonUpEvent
                : UIElement.PreviewMouseRightButtonUpEvent,
        };
        owner.RaiseEvent(args);
    }
}
