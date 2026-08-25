using System.Net.Http;
using System.Windows;
using Aerochat.Connectivity.Auth;
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat;

public partial class App : Application
{
    private readonly bool _suppressStartup;

    public App() : this(false) { }

    public App(bool suppressStartup)
    {
        _suppressStartup = suppressStartup;
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_suppressStartup) return;
        PresentationState state = DemoData.Create();
        IAuthClient authClient = CreateAuthClient();
        WindowNavigator navigator = new(
            state,
            (currentState, currentNavigator) => new Login(currentState, currentNavigator, authClient));
        MainWindow = new Home(state, navigator);
        MainWindow.Show();
    }

    private static IAuthClient CreateAuthClient()
    {
        string? configuredServer = Environment.GetEnvironmentVariable("AEROCHAT_SERVER_URL");
        if (!Uri.TryCreate(configuredServer, UriKind.Absolute, out Uri? serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(serverUri.UserInfo)
            || !StringComparer.Ordinal.Equals(serverUri.AbsolutePath, "/")
            || !string.IsNullOrEmpty(serverUri.Query)
            || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            return new NullAuthClient();
        }

        return new OAuthAuthClient(
            new HttpClient(),
            serverUri,
            new Aerochat.Connectivity.DpapiTokenCache(),
            new ShellBrowserLauncher(),
            () => new LoopbackCallbackListener());
    }
}
