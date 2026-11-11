using System.Net.Http;
using System.Windows;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Auth;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat;

public partial class App : Application
{
    private readonly bool _suppressStartup;
    private IChatTransport? _chatTransport;
    private PresentationAdapter? _presentationAdapter;
    private ConversationCatalogClient? _conversationCatalog;
    private TurnCredentialProvider? _turnCredentials;

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
        _conversationCatalog = CreateConversationCatalog();
        _presentationAdapter = new PresentationAdapter(
            state,
            _chatTransport,
            action => Dispatcher.Invoke(action));
        Home? home = null;
        WindowNavigator navigator = new(
            state,
            (currentState, currentNavigator) => new Login(
                currentState,
                currentNavigator,
                authClient,
                () =>
                {
                    _turnCredentials?.Dispose(); _turnCredentials = null;
                    return home?.ConnectLiveAsync() ?? Task.CompletedTask;
                }),
            (currentState, conversation, currentNavigator) => new Chat(
                currentState,
                conversation,
                currentNavigator,
                CreateMessageClient(authClient),
                CreateCallCoordinator(currentState, conversation, authClient)));
        home = new Home(
            state,
            navigator,
            _chatTransport,
            TryGetConfiguredServer(out Uri? configuredServer) ? configuredServer : null,
            tokenLoader: authClient is OAuthAuthClient oauth
                ? oauth.LoadCachedTokenAsync
                : null,
            conversationCatalog: _conversationCatalog);
        MainWindow = home;
        MainWindow.Closed += (_, _) => _ = DisposeConnectivityAsync();
        MainWindow.Show();
    }

    private static IAuthClient CreateAuthClient()
    {
        if (!TryGetConfiguredServer(out Uri? serverUri) || serverUri is null)
            return new NullAuthClient();

        return OAuthAuthClient.Create(
            serverUri,
            Environment.GetEnvironmentVariable("AEROCHAT_SESSION_CACHE_PATH"));
    }

    private static IChatTransport CreateChatTransport() =>
        TryGetConfiguredServer(out _)
            ? new GatewayClient()
            : new NullTransport();

    private static ChatMessageClient? CreateMessageClient(IAuthClient authClient)
    {
        if (!TryGetConfiguredServer(out Uri? server) || server is null)
            return null;
        return authClient is OAuthAuthClient oauth
            ? new ChatMessageClient(new HttpClient(), server, oauth.LoadCachedTokenAsync)
            : null;
    }

    private static ConversationCatalogClient? CreateConversationCatalog()
    {
        if (!TryGetConfiguredServer(out Uri? server) || server is null)
            return null;
        return new ConversationCatalogClient(new HttpClient(), server);
    }

    private static CallSignalingClient? CreateCallClient(IAuthClient authClient)
    {
        if (!TryGetConfiguredServer(out Uri? server) || server is null
            || authClient is not OAuthAuthClient oauth)
            return null;
        return new CallSignalingClient(new HttpClient(), server, oauth.LoadCachedTokenAsync);
    }

    private ICallCoordinator? CreateCallCoordinator(
        PresentationState state,
        ConversationPresentation conversation,
        IAuthClient authClient)
    {
        if (RtcOptIn.IsBlocked(
                TryGetConfiguredServer(out _),
                Environment.GetEnvironmentVariable(RtcOptIn.EnvironmentVariable)))
        {
            return null;
        }

        CallSignalingClient? signaling = CreateCallClient(authClient);
        if (signaling is null || _chatTransport is null)
            return new OfflineCallCoordinator(state, conversation.Id.ToString());

        if (_turnCredentials is null && authClient is OAuthAuthClient oauth &&
            TryGetConfiguredServer(out Uri? turnServer) && turnServer is not null)
            _turnCredentials = new TurnCredentialProvider(new HttpClient(), turnServer, oauth.LoadCachedTokenAsync);
        var engine = new RtcPeerEngine();
        if (_turnCredentials is not null) engine.UseTurn(_turnCredentials);
        return new CallCoordinator(
            state,
            conversation.TransportId,
            signaling,
            engine,
            _chatTransport,
            action => Dispatcher.Invoke(action));
    }

    private static bool TryGetConfiguredServer(out Uri? serverUri) =>
        TryValidateConfiguredServerUri(Environment.GetEnvironmentVariable("AEROCHAT_SERVER_URL"), out serverUri);

    internal static bool TryValidateConfiguredServerUri(string? configuredServer, out Uri? serverUri)
    {
        if (!Uri.TryCreate(configuredServer, UriKind.Absolute, out serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttps
                && !(serverUri.Scheme == Uri.UriSchemeHttp && OAuthAuthClient.HasExactNumericLoopbackHost(serverUri)))
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
        _turnCredentials?.Dispose();
        _presentationAdapter?.Dispose();
        _conversationCatalog?.Dispose();
        if (_chatTransport is not null)
            await _chatTransport.DisposeAsync();
    }
}
