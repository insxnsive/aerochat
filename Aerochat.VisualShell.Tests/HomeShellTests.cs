using Aerochat.Presentation;
using Aerochat.Windows;

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
}
