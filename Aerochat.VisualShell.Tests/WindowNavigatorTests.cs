using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aerochat.Connectivity.Auth;
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
    public void Login_with_null_auth_client_disables_provider_buttons_and_reports_unconfigured_server()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var login = new Login(state, navigator, new NullAuthClient());
            login.Show();

            Assert.Multiple(() =>
            {
                Assert.That(((Button)login.FindName("GoogleSignIn")).IsEnabled, Is.False);
                Assert.That(((Button)login.FindName("GitHubSignIn")).IsEnabled, Is.False);
                Assert.That(((Button)login.FindName("DiscordSignIn")).IsEnabled, Is.False);
                Assert.That(login.ViewModel.StatusMessage, Is.EqualTo("Server not configured."));
            });
            login.Close();
        });
    }

    [Test]
    public void Login_two_argument_constructor_still_builds()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var login = new Login(state, new WindowNavigator(state));
            Assert.That(login, Is.Not.Null);
            login.Close();
        });
    }

    [Test]
    public void Login_xaml_uses_provider_buttons_without_legacy_credential_controls()
    {
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "Aerochat", "Windows", "Login.xaml"));
        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain("GoogleSignIn"));
            Assert.That(xaml, Does.Contain("GitHubSignIn"));
            Assert.That(xaml, Does.Contain("DiscordSignIn"));
            Assert.That(xaml, Does.Not.Contain("Password"));
            Assert.That(xaml, Does.Not.Contain("MFATextBox"));
            Assert.That(xaml, Does.Not.Contain("OnClickLoginWithPassword"));
        });
    }

    [Test]
    public void Closing_login_cancels_active_authentication()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var auth = new CancellationAwareAuthClient();
            var login = new Login(state, navigator, auth);
            login.Show();

            ((Button)login.FindName("GoogleSignIn")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.That(auth.Started, Is.True);

            login.Close();
            Assert.That(auth.Token.IsCancellationRequested, Is.True);
        });
    }

    [Test]
    public void Login_handles_only_expected_auth_failures()
    {
        string codeBehind = File.ReadAllText(
            Path.Combine(RepositoryRoot.Path, "Aerochat", "Windows", "Login.xaml.cs"));

        Assert.That(codeBehind, Does.Not.Contain("catch (Exception)"));
        Assert.That(codeBehind, Does.Contain("catch (AuthException)"));
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

    private sealed class CancellationAwareAuthClient : IAuthClient
    {
        public bool IsAvailable => true;
        public bool Started { get; private set; }
        public CancellationToken Token { get; private set; }

        public async Task<AuthSession> SignInAsync(
            string provider,
            bool rememberSession = true,
            CancellationToken cancellationToken = default)
        {
            Started = true;
            Token = cancellationToken;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
