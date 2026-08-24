using Aerochat.Presentation;
using Aerochat.Windows;
using System.IO;

namespace Aerochat.VisualShell.Tests;

public sealed class HomeShellTests
{
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
    public void Navigator_stages_unmigrated_routes_as_not_supported()
    {
        WpfTestHost.Run(() =>
        {
            var navigator = new WindowNavigator(DemoData.Create());

            Assert.Throws<NotSupportedException>(() => navigator.Create(ShellRoute.Chat, 42UL));
            Assert.Throws<NotSupportedException>(() => navigator.Create(ShellRoute.Settings));
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
}
