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
    public void Chat_route_resolves_the_numeric_conversation_id_used_by_home_contacts()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation expected = state.Conversations.Single(conversation => conversation.IsGroup);
            var navigator = new WindowNavigator(state);
            var chat = (Chat)navigator.Create(ShellRoute.Chat, expected.Id);

            Assert.That(chat.Conversation, Is.SameAs(expected));
            chat.Close();
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

    [TestCase(DialogIcon.Information, "/Resources/Home/InfoIcon.png")]
    [TestCase(DialogIcon.Warning, "/Resources/Home/WarningIcon.png")]
    [TestCase(DialogIcon.Error, "/Resources/Home/ErrorIcon.png")]
    public void Dialog_uses_the_packaged_status_icon(DialogIcon icon, string expectedSuffix)
    {
        WpfTestHost.Run(() =>
        {
            var dialog = new Dialog("Visual audit", "Rendered dialog", icon);

            Assert.That(dialog.Icon?.ToString(), Does.EndWith(expectedSuffix));
            dialog.Close();
        });
    }

    [Test]
    public void Settings_loaded_heading_tracks_the_selected_category()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var settings = new Settings(state);
            settings.Show();
            settings.UpdateLayout();
            var categories = (ListBox)settings.FindName("CategoriesListBox");
            TextBlock heading = FindVisualChildren<TextBlock>(settings)
                .Single(text => text.FontWeight == FontWeights.Bold &&
                                ReferenceEquals(text.DataContext, settings.ViewModel));

            categories.SelectedIndex = 1;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            settings.UpdateLayout();

            Assert.That(heading.Text, Is.EqualTo("Visual"));
            settings.Close();
        });
    }

    [Test]
    public void Settings_loaded_general_selectors_show_the_current_values()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var settings = new Settings(state);
            settings.Show();
            settings.UpdateLayout();
            ComboBox[] selectors = FindVisualChildren<ComboBox>(settings)
                .Where(comboBox => comboBox.IsVisible)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(selectors, Has.Length.EqualTo(2));
                Assert.That(selectors[0].SelectedItem, Is.EqualTo(state.Settings.Language));
                Assert.That(selectors[1].SelectedItem, Is.EqualTo(state.Settings.TimeFormat));
            });
            settings.Close();
        });
    }

    [Test]
    public void Change_scene_loaded_tiles_use_their_scene_colors()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var changeScene = new ChangeScene(state);
            changeScene.Show();
            changeScene.UpdateLayout();
            System.Windows.Media.Color[] tileColors = FindVisualChildren<System.Windows.Shapes.Rectangle>(changeScene)
                .Where(rectangle => rectangle.DataContext is SceneChoice &&
                                    rectangle.Width == 96 && rectangle.Height == 48)
                .Select(rectangle => ((System.Windows.Media.SolidColorBrush)rectangle.Fill).Color)
                .ToArray();

            Assert.That(tileColors, Is.EqualTo(state.Scenes.Select(scene => scene.Color)));
            changeScene.Close();
        });
    }

    [Test]
    public void Change_scene_loaded_selection_frame_tracks_the_selected_choice()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var changeScene = new ChangeScene(state);
            changeScene.Show();
            changeScene.UpdateLayout();
            Aerochat.Controls.NineSlice[] selectionFrames =
                FindVisualChildren<Aerochat.Controls.NineSlice>(changeScene)
                    .Where(frame => frame.DataContext is SceneChoice)
                    .ToArray();

            changeScene.Scenes[0].Selected = false;
            changeScene.Scenes[1].Selected = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.Multiple(() =>
            {
                Assert.That(selectionFrames[0].Image, Is.Null);
                Assert.That(selectionFrames[1].Image?.ToString(), Does.EndWith("/Resources/ChangeScene/Active.png"));
            });
            changeScene.Close();
        });
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
    public void Successful_login_refreshes_the_existing_home_composition()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            int refreshCount = 0;
            var login = new Login(
                state,
                navigator,
                new CompletingAuthClient(),
                () =>
                {
                    refreshCount++;
                    return Task.CompletedTask;
                });
            login.Show();

            ((Button)login.FindName("GoogleSignIn")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.Multiple(() =>
            {
                Assert.That(refreshCount, Is.EqualTo(1));
                Assert.That(login.IsVisible, Is.False);
            });
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
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

    private sealed class CompletingAuthClient : IAuthClient
    {
        public bool IsAvailable => true;

        public Task<AuthSession> SignInAsync(
            string provider,
            bool rememberSession = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthSession("session-token", 3600));
    }
}
