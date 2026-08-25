using Aerochat.Controls;
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
