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
    public void Search_filters_contacts_without_mutating_source_groups()
    {
        PresentationState state = DemoData.Create();
        int sourceCount = state.ContactGroups.Sum(group => group.Items.Count);

        state.ApplySearch("Mara");

        Assert.That(state.FilteredContactGroups.SelectMany(group => group.Items)
            .All(item => item.Person.Name.Contains("Mara", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(state.ContactGroups.Sum(group => group.Items.Count), Is.EqualTo(sourceCount));
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
