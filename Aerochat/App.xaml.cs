using System.Windows;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Auth;
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat;

public partial class App : Application
{
    private readonly bool _suppressStartup;
    private IChatTransport? _chatTransport;
    private PresentationAdapter? _presentationAdapter;

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
        _chatTransport = CreateChatTransport();
        _presentationAdapter = new PresentationAdapter(
            state,
            _chatTransport,
            action => Dispatcher.Invoke(action));
        WindowNavigator navigator = new(
            state,
            (currentState, currentNavigator) => new Login(currentState, currentNavigator, authClient));
        MainWindow = new Home(state, navigator);
        MainWindow.Closed += (_, _) => _ = DisposeConnectivityAsync();
        MainWindow.Show();
    }

    private static IAuthClient CreateAuthClient()
    {
        if (!TryGetConfiguredServer(out Uri? serverUri) || serverUri is null)
            return new NullAuthClient();

        return OAuthAuthClient.Create(serverUri);
    }

    private static IChatTransport CreateChatTransport() =>
        TryGetConfiguredServer(out _)
            ? new GatewayClient()
            : new NullTransport();

    private static bool TryGetConfiguredServer(out Uri? serverUri)
    {
        string? configuredServer = Environment.GetEnvironmentVariable("AEROCHAT_SERVER_URL");
        if (!Uri.TryCreate(configuredServer, UriKind.Absolute, out serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(serverUri.UserInfo)
            || !StringComparer.Ordinal.Equals(serverUri.AbsolutePath, "/")
            || !string.IsNullOrEmpty(serverUri.Query)
            || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            serverUri = null;
            return false;
        }

        return true;
    }

    private async Task DisposeConnectivityAsync()
    {
        _presentationAdapter?.Dispose();
        if (_chatTransport is not null)
            await _chatTransport.DisposeAsync();
    }
}
