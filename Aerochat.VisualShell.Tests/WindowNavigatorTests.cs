using System.IO;
using System.Windows;
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class WindowNavigatorTests
{
    [Test]
    public void Every_retained_route_constructs_without_backend_state()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var windows = new Window[]
            {
                navigator.Create(ShellRoute.Home),
                navigator.Create(ShellRoute.Chat, state.Conversations[0]),
                navigator.Create(ShellRoute.Settings),
                navigator.Create(ShellRoute.About),
                navigator.Create(ShellRoute.Login),
                navigator.Create(ShellRoute.ChangeScene),
                navigator.Create(ShellRoute.ImagePreviewer, state.PreviewImages[0])
            };

            Assert.That(windows.Select(window => window.GetType()), Is.EqualTo(new[]
            {
                typeof(Home), typeof(Chat), typeof(Settings), typeof(About),
                typeof(Login), typeof(ChangeScene), typeof(ImagePreviewer)
            }));
            foreach (Window window in windows) window.Close();
        });
    }

    [Test]
    public void Settings_mutate_only_the_process_local_visual_settings()
    {
        PresentationState state = DemoData.Create();
        bool before = state.Settings.ShowAds;
        state.Settings.ShowAds = !before;
        Assert.That(state.Settings.ShowAds, Is.EqualTo(!before));
    }

    [Test]
    public void Login_uses_fixed_preview_text_and_does_not_accept_credentials()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "Aerochat", "Windows", "Login.xaml.cs"))
            + File.ReadAllText(Path.Combine(RepositoryRoot.Path, "Aerochat", "Windows", "Login.xaml"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("Visual shell preview"));
            Assert.That(source, Does.Not.Contain("TransformTokenForConsumption"));
            Assert.That(source, Does.Not.Contain("BeginLogin"));
            Assert.That(source, Does.Not.Contain("PasswordBox"));
            Assert.That(source, Does.Not.Contain(".Password"));
            Assert.That(source, Does.Not.Contain("WebView"));
        });
    }

    [Test]
    public void Secondary_windows_do_not_launch_external_urls_or_processes()
    {
        string root = Path.Combine(RepositoryRoot.Path, "Aerochat", "Windows");
        string[] files = ["Settings.xaml.cs", "About.xaml.cs", "Login.xaml.cs",
            "ChangeScene.xaml.cs", "ImagePreviewer.xaml.cs", "Notification.xaml.cs",
            "Dialog.xaml.cs", "ColorPicker.xaml.cs", "NonNativeTooltip.xaml.cs",
            "AerochatWindow.xaml.cs", "../Presentation/WindowNavigator.cs"];
        string[] forbidden = ["SettingsManager", "Process.Start", "ShellExecute",
            "WebView", "HttpClient", "File.WriteAll", "Directory.CreateDirectory"];
        var offenders = files.SelectMany(file =>
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            return forbidden.Where(text.Contains).Select(token => $"{file}: {token}");
        }).ToArray();
        Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
    }
}
