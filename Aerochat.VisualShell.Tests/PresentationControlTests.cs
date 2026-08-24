using Aerochat.Controls;
using Aerochat.Presentation;
using System.IO;
using System.Threading;
using System.Windows;
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
            _ = new Application();
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
}
