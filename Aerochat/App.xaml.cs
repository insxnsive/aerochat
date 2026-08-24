using Aerochat.Enums;
using Aerochat.Presentation;
using Aerochat.Windows;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using System.Windows;

namespace Aerochat;

public partial class App : Application
{
    private readonly bool _suppressStartup;

    public bool LoggingOut { get; set; }

    public App()
        : this(false)
    {
    }

    public App(bool suppressStartup)
    {
        _suppressStartup = suppressStartup;
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_suppressStartup)
            return;

        PresentationState state = DemoData.Create();
        WindowNavigator navigator = new(state);
        Home home = (Home)navigator.Create(ShellRoute.Home);
        MainWindow = home;
        home.Show();
    }

    // Compatibility shims keep the retained backend windows compiling while their
    // routes are migrated in later visual-shell tasks. They are never used by the
    // local Home startup path.
    public static Task SetStatus(UserStatus status, bool updateUserSettingsProto = true) =>
        Task.CompletedTask;

    public Task<AerochatLoginStatus> BeginLogin(
        string givenToken,
        bool save = false,
        UserStatus? status = null) =>
        Task.FromResult(AerochatLoginStatus.UnknownFailure);

    public Task SignOut()
    {
        LoggingOut = false;
        return Task.CompletedTask;
    }

    public void RebuildJumpLists()
    {
    }
}
