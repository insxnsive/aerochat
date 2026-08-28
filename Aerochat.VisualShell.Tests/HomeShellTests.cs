using Aerochat.Presentation;
using Aerochat.Connectivity;
using Aerochat.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class HomeShellTests
{
    [Test]
    public void Home_live_transport_applies_presence_and_routes_messages()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var transport = new FakeTransport();
            var home = new Home(state, new WindowNavigator(state), transport,
                new Uri("http://localhost:5080/"), "test-token");

            home.ConnectLiveAsync().GetAwaiter().GetResult();
            transport.RaisePresence("1001", "Busy");
            transport.RaiseMessage("9001", Guid.NewGuid().ToString(), "1001", "Live hello");

            Assert.That(transport.Connected, Is.True);
            Assert.That(state.ContactGroups.SelectMany(group => group.Items)
                .Single(item => item.Person.Id == 1001).Person.Presence.Status, Is.EqualTo(PresenceStatus.Busy));
            Assert.That(state.Conversations.Single(item => item.Id == 9001).Messages.Single().Body,
                Is.EqualTo("Live hello"));
            home.Close();
        });
    }

    [Test]
    public void Home_connects_the_gateway_before_hydrating_server_conversations()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var order = new List<string>();
            var transport = new FakeTransport(order);
            var conversationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var catalog = new FakeConversationCatalog(
                order,
                [new ServerConversationSummary(
                    conversationId,
                    "group",
                    "Live RTC Room",
                    new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero))]);
            var home = new Home(
                state,
                new WindowNavigator(state),
                transport,
                new Uri("http://localhost:5080/"),
                "session-token",
                conversationCatalog: catalog);

            home.ConnectLiveAsync().GetAwaiter().GetResult();

            ConversationPresentation live = state.Conversations.Single(
                conversation => conversation.WireId == conversationId.ToString("D"));
            Assert.Multiple(() =>
            {
                Assert.That(order, Is.EqualTo(new[] { "connect", "catalog" }));
                Assert.That(live.Id, Is.EqualTo(StableIdMapper.Map(conversationId)));
                Assert.That(live.TransportId, Is.EqualTo(conversationId.ToString("D")));
                Assert.That(live.IsServerBacked, Is.True);
                Assert.That(live.Name, Is.EqualTo("Live RTC Room"));
                Assert.That(state.ContactGroups.Single(group => group.IsServerBacked)
                    .Items.Single().ConversationId, Is.EqualTo(live.Id));
            });
            home.Close();
        });
    }

    private sealed class FakeTransport(List<string>? order = null) : IChatTransport
    {
        public event EventHandler<MessageCreatedEventArgs>? MessageCreated;
        public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;
#pragma warning disable CS0067 // interface member; raised by real transports only
        public event EventHandler<CallSignalEventArgs>? CallSignalReceived;
#pragma warning restore CS0067
        public bool Connected { get; private set; }
        public Task ConnectAsync(Uri server, string token, CancellationToken cancellationToken = default)
        { order?.Add("connect"); Connected = true; return Task.CompletedTask; }
        public Task SendAsync(string conversationId, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTypingAsync(string conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaisePresence(string id, string status) => PresenceUpdated?.Invoke(this, new(id, status));
        public void RaiseMessage(string conversation, string message, string author, string body) =>
            MessageCreated?.Invoke(this, new(conversation, message, author, body, DateTimeOffset.UtcNow));
    }

    private sealed class FakeConversationCatalog(
        List<string> order,
        IReadOnlyList<ServerConversationSummary> conversations) : IConversationCatalogClient
    {
        public Task<IReadOnlyList<ServerConversationSummary>> LoadAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            order.Add("catalog");
            return Task.FromResult(conversations);
        }
    }

    [Test]
    public void Home_constructs_from_demo_state_without_backend_services()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var home = new Home(state, navigator);

            Assert.That(home.DataContext, Is.SameAs(state));
            Assert.That(state.FilteredContactGroups, Is.Not.Empty);

            home.Close();
        });
    }

    [Test]
    public void Home_default_constructor_creates_demo_state_and_navigator()
    {
        WpfTestHost.Run(() =>
        {
            var home = new Home();

            Assert.That(home.State, Is.Not.Null);
            Assert.That(home.Navigator.State, Is.SameAs(home.State));
            Assert.That(home.State.ContactGroups, Is.Not.Empty);
            home.Close();
        });
    }

    [Test]
    public void Home_applies_search_without_mutating_source_groups()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));
            int sourceCount = state.ContactGroups.Sum(group => group.Items.Count);

            home.ApplySearch("maya");

            var filteredContacts = state.FilteredContactGroups.SelectMany(group => group.Items).ToList();
            Assert.That(filteredContacts, Has.Count.EqualTo(1));
            Assert.That(filteredContacts
                .All(item => item.Person.Name.Contains("maya", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(state.ContactGroups.Sum(group => group.Items.Count), Is.EqualTo(sourceCount));
            home.Close();
        });
    }

    [Test]
    public void Home_loaded_status_placeholder_is_visible_for_empty_custom_status_when_not_editing()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            state.CurrentUser.Presence.CustomStatus = "";
            state.IsEditingStatus = false;
            var home = new Home(state, new WindowNavigator(state));

            home.Show();
            home.UpdateLayout();
            var placeholder = (System.Windows.Controls.TextBlock)home.FindName("PART_StatusPlaceholder");

            Assert.That(placeholder.Visibility, Is.EqualTo(Visibility.Visible));
            home.Close();
        });
    }

    [Test]
    public void Home_loaded_no_match_search_shows_explanatory_empty_result_visual()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));

            home.Show();
            home.UpdateLayout();
            var contactRegion = (DependencyObject)System.Windows.Media.VisualTreeHelper.GetParent(
                FindVisualChildren<Aerochat.Controls.HomeTreeView>(home).Single())!;
            string[] beforeSearch = VisibleTextBlocks(contactRegion);

            home.ApplySearch("no-contact-matches-this-query");
            home.UpdateLayout();
            string[] afterSearch = VisibleTextBlocks(contactRegion);
            string[] explanatoryText = afterSearch
                .Except(beforeSearch, StringComparer.Ordinal)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(state.FilteredContactGroups, Is.Empty);
                Assert.That(explanatoryText, Is.Not.Empty,
                    "A no-match search must render explanatory text in the contact region.");
            });
            home.Close();
        });
    }

    [Test]
    public void Home_changes_presence_locally()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));

            home.SetPresence(PresenceStatus.Busy);

            Assert.That(state.CurrentUser.Presence.Status, Is.EqualTo(PresenceStatus.Busy));
            home.Close();
        });
    }

    [Test]
    public void Home_commits_personal_message_locally()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));

            home.EditPersonalMessage("  Local-only note  ");
            Assert.That(state.CurrentUser.Presence.CustomStatus, Is.EqualTo("Available"));

            home.CommitPersonalMessage();

            Assert.That(state.CurrentUser.Presence.CustomStatus, Is.EqualTo("Local-only note"));
            home.Close();
        });
    }

    [Test]
    public void Home_toggles_group_collapse_locally()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));
            ContactGroupPresentation group = state.ContactGroups[0];

            home.ToggleGroupCollapse(group);
            Assert.That(group.IsCollapsed, Is.True);
            home.ToggleGroupCollapse(group);
            Assert.That(group.IsCollapsed, Is.False);
            home.Close();
        });
    }

    [Test]
    public void Home_dismisses_notice_locally()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));

            home.DismissNotice();

            Assert.That(state.Notices, Is.Empty);
            home.Close();
        });
    }

    [Test]
    public void Home_cycles_ad_locally()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));
            AdPresentation first = state.Ads[0];

            home.CycleAd();

            Assert.That(state.CurrentAd, Is.SameAs(state.Ads[1]));
            Assert.That(state.CurrentAd, Is.Not.SameAs(first));
            home.Close();
        });
    }

    [Test]
    public void Home_code_behind_stays_inside_the_presentation_boundary()
    {
        string homePath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/Windows/Home.xaml.cs"));
        string source = File.ReadAllText(homePath);
        string[] forbiddenTokens =
        [
            "Discord", "DSharpPlus", "HttpClient", "System.Net", "SettingsManager",
            "Hoarder", "Vanara", "ShellExecute", "Process.Start", "Task.Run",
            "System.Timers", "Timer", "UpdateRemote", "File.WriteAll", "File.ReadAll",
            "Application.Current.Windows"
        ];

        Assert.Multiple(() =>
        {
            foreach (string token in forbiddenTokens)
                Assert.That(source, Does.Not.Contain(token), $"Home.xaml.cs contains forbidden token {token}");
        });
    }

    [Test]
    public void Home_search_input_wires_typing_to_typed_search_filtering()
    {
        string homePath = GetHomePath("Home.xaml");
        string codeBehindPath = GetHomePath("Home.xaml.cs");
        string xaml = File.ReadAllText(homePath);
        string codeBehind = File.ReadAllText(codeBehindPath);

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain("x:Name=\"SearchInput\" TextChanged=\"SearchInput_TextChanged\""));
            Assert.That(codeBehind, Does.Contain("private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)"));
            Assert.That(codeBehind, Does.Contain("ApplySearch(SearchInput.Text)"));
        });
    }

    [Test]
    public void Home_presentation_models_expose_observable_local_selection_and_visibility()
    {
        PresentationState state = DemoData.Create();
        ContactGroupPresentation group = state.ContactGroups[0];
        ContactPresentation contact = group.Items[0];

        Assert.Multiple(() =>
        {
            Assert.That(typeof(ContactGroupPresentation).GetProperty(nameof(ContactGroupPresentation.IsSelected)), Is.Not.Null);
            Assert.That(typeof(ContactGroupPresentation).GetProperty(nameof(ContactGroupPresentation.IsVisibleProperty)), Is.Not.Null);
            Assert.That(typeof(ContactPresentation).GetProperty(nameof(ContactPresentation.IsSelected)), Is.Not.Null);
        });

        var selected = typeof(ContactPresentation).GetProperty(nameof(ContactPresentation.IsSelected));
        var visible = typeof(ContactGroupPresentation).GetProperty(nameof(ContactGroupPresentation.IsVisibleProperty));
        selected!.SetValue(contact, true);
        visible!.SetValue(group, true);

        Assert.Multiple(() =>
        {
            Assert.That(selected.GetValue(contact), Is.EqualTo(true));
            Assert.That(visible.GetValue(group), Is.EqualTo(true));
        });
    }

    [Test]
    public void Home_status_triggers_use_local_presence_values()
    {
        string xaml = File.ReadAllText(GetHomePath("Home.xaml"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain("Value=\"Online\""));
            Assert.That(xaml, Does.Contain("Value=\"Busy\""));
            Assert.That(xaml, Does.Contain("Value=\"Away\""));
            Assert.That(xaml, Does.Contain("Value=\"Offline\""));
            Assert.That(xaml, Does.Not.Contain("Value=\"DoNotDisturb\""));
            Assert.That(xaml, Does.Not.Contain("Value=\"Idle\""));
            Assert.That(xaml, Does.Not.Contain("Value=\"Invisible\""));
        });
    }

    [Test]
    public void Home_uses_animated_ad_control_bound_to_current_ad()
    {
        string xaml = File.ReadAllText(GetHomePath("Home.xaml"));

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain("<controls:AdImage"));
            Assert.That(xaml, Does.Contain("DataContext=\"{Binding CurrentAd"));
            Assert.That(xaml, Does.Not.Contain("<Image x:Name=\"AdImage\""));
        });
    }

    [Test]
    public void Home_filtered_group_collapse_synchronizes_with_source_group()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var home = new Home(state, new WindowNavigator(state));

            home.ApplySearch("maya");
            ContactGroupPresentation sourceGroup = state.ContactGroups[0];
            ContactGroupPresentation filteredGroup = state.FilteredContactGroups[0];

            filteredGroup.IsCollapsed = true;
            Assert.That(sourceGroup.IsCollapsed, Is.True);

            sourceGroup.IsCollapsed = false;
            Assert.That(filteredGroup.IsCollapsed, Is.False);

            home.Close();
        });
    }

    [Test]
    public void Navigator_creates_home_with_the_shared_state()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var home = navigator.Create(ShellRoute.Home);

            Assert.That(home, Is.TypeOf<Home>());
            Assert.That(home.DataContext, Is.SameAs(state));

            home.Close();
        });
    }

    [Test]
    public void Navigator_creates_all_retained_routes()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            foreach (ShellRoute route in Enum.GetValues<ShellRoute>())
            {
                Window window = navigator.Create(route,
                    route == ShellRoute.Chat ? state.Conversations[0] :
                    route == ShellRoute.ImagePreviewer ? state.PreviewImages[0] : null);
                Assert.That(window, Is.Not.Null);
                window.Close();
            }
        });
    }

    [Test]
    public void Localization_defaults_to_process_local_en_us()
    {
        var localization = new Aerochat.Localization.LocalizationManager();

        Assert.That(localization.LanguageCode, Is.EqualTo("en-US"));
        Assert.That(localization["MissingKey"], Is.EqualTo("MissingKey"));
    }

    [Test]
    public void Localization_resolves_representative_keys_from_compiled_output()
    {
        string englishLocale = Path.Combine(
            AppContext.BaseDirectory,
            "Locales",
            "en.json");
        var localization = new Aerochat.Localization.LocalizationManager();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(englishLocale), Is.True,
                $"Expected runtime locale at {englishLocale}");
            Assert.That(localization["AppNameMessenger"], Is.EqualTo("Windows Live Messenger"));
            Assert.That(localization["StatusAvailable"], Is.EqualTo("Available"));
            Assert.That(localization["HomeWhatsNew"], Is.EqualTo("What's new"));
            Assert.That(localization["ChatAttribution"], Is.EqualTo("Aerochat by nullptr"));
        });
    }

    [Test]
    public void Localization_rejects_path_like_language_codes()
    {
        var localization = new Aerochat.Localization.LocalizationManager();
        string absoluteCode = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "outside-locale"));
        string[] invalidCodes =
        [
            "../",
            @"..\\",
            "fr/../../secret",
            @"fr\\..\\secret",
            absoluteCode
        ];

        Assert.Multiple(() =>
        {
            foreach (string code in invalidCodes)
            {
                Assert.That(
                    () => localization.LoadLanguage(code),
                    Throws.TypeOf<ArgumentException>(),
                    $"Expected path-like locale code '{code}' to be rejected.");
            }
        });
    }

    [TestCase("en")]
    [TestCase("en-US")]
    [TestCase("fr")]
    public void Localization_accepts_safe_language_codes(string code)
    {
        var localization = new Aerochat.Localization.LocalizationManager();

        Assert.DoesNotThrow(() => localization.LoadLanguage(code));
        Assert.That(localization.LanguageCode, Is.EqualTo(code));
    }

    [Test, Timeout(15000)]
    public void Aerochat_executable_starts_the_real_home_window()
    {
        string executablePath = Path.GetFullPath(Path.Combine(
            RepositoryRoot.Path,
            "Aerochat",
            "bin",
            "x64",
            "Debug",
            "net8.0-windows10.0.17763",
            "Aerochat.exe"));
        Assert.That(File.Exists(executablePath), Is.True, $"Expected Debug x64 executable at {executablePath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        bool started = false;
        try
        {
            started = process.Start();
            Assert.That(started, Is.True, "The real Aerochat.exe process did not start.");

            if (process.WaitForExit(3000))
            {
                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                Assert.Fail(
                    $"Aerochat.exe exited before the Home smoke window stayed open. " +
                    $"ExitCode={process.ExitCode}; stdout={standardOutput}; stderr={standardError}");
            }

            Assert.That(process.HasExited, Is.False, "Aerochat.exe exited during the Home startup smoke window.");
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between HasExited and Kill.
                }
            }
        }
    }

    [Test]
    public void Home_xaml_uses_presentation_state_bindings_without_visual_drift()
    {
        string homePath = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/Windows/Home.xaml"));
        string xaml = File.ReadAllText(homePath);

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Not.Contain("Theme.Scene"));
            Assert.That(xaml, Does.Not.Contain("FilteredCategories"));
            Assert.That(xaml, Does.Not.Contain("xmlns:viewmodels"));
            Assert.That(xaml, Does.Not.Contain("HomeWindowViewModel"));
            Assert.That(xaml, Does.Contain("d:DataContext=\"{d:DesignInstance Type=presentation:PresentationState}\""));
            Assert.That(xaml, Does.Contain("CurrentScene.Color"));
            Assert.That(xaml, Does.Contain("CurrentScene.TextColor"));
            Assert.That(xaml, Does.Contain("CurrentScene.File"));
            Assert.That(xaml, Does.Contain("CurrentScene.IsDefault"));
            Assert.That(xaml, Does.Contain("CurrentUser.Presence.Status"));
            Assert.That(xaml, Does.Contain("Person.Presence.Status"));
            Assert.That(xaml, Does.Contain("Person.Presence.Activity"));
            Assert.That(xaml, Does.Contain("FilteredContactGroups"));
            Assert.That(xaml, Does.Contain("Settings.ShowAds"));
            Assert.That(xaml, Does.Contain("Settings.ShowNews"));
            Assert.That(xaml, Does.Contain("Settings.ShowEyecandy"));
            Assert.That(xaml, Does.Contain("News[0].Body"));
            Assert.That(xaml, Does.Contain("Notices[0].Message"));
            Assert.That(xaml, Does.Contain("CurrentAd.ImageUri"));

            Assert.That(xaml, Does.Contain("Height=\"650\" Width=\"400\""));
            Assert.That(xaml, Does.Contain("MinWidth=\"300\""));
            Assert.That(xaml, Does.Contain("MinHeight=\"500\""));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Home/SearchBar.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Home/TreeHover.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/Separator.png"));
            Assert.That(xaml, Does.Contain("/Aerochat;component/Resources/Message/Background.png"));
        });
    }

    private static string[] VisibleTextBlocks(DependencyObject root) =>
        FindVisualChildren<System.Windows.Controls.TextBlock>(root)
            .Where(textBlock => textBlock.Visibility == Visibility.Visible &&
                                !string.IsNullOrWhiteSpace(textBlock.Text))
            .Select(textBlock => textBlock.Text.Trim())
            .ToArray();

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

    private static string GetHomePath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "../../../../../Aerochat/Windows",
            fileName));
}
