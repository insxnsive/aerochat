using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aerochat.Connectivity.Auth;

public sealed class OAuthAuthClient : IAuthClient
{
    private static readonly HashSet<string> AllowedProviders = new(StringComparer.Ordinal)
    {
        "google", "github", "discord"
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _serverUri;
    private readonly ITokenCache _tokenCache;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly Func<ILoopbackCallbackListener> _listenerFactory;
    private readonly TimeSpan _signInTimeout;

    public OAuthAuthClient(
        HttpClient httpClient,
        Uri serverUri,
        ITokenCache tokenCache,
        IBrowserLauncher browserLauncher,
        Func<ILoopbackCallbackListener> listenerFactory,
        TimeSpan? signInTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(tokenCache);
        ArgumentNullException.ThrowIfNull(browserLauncher);
        ArgumentNullException.ThrowIfNull(listenerFactory);
        TimeSpan timeoutValue = signInTimeout ?? TimeSpan.FromMinutes(5);
        if (timeoutValue <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(signInTimeout));

        if (!serverUri.IsAbsoluteUri
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(serverUri.UserInfo)
            || !StringComparer.Ordinal.Equals(serverUri.AbsolutePath, "/")
            || !string.IsNullOrEmpty(serverUri.Query)
            || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            throw new ArgumentException(
                "The authentication server URI must be an unambiguous HTTP(S) origin.",
                nameof(serverUri));
        }

        _httpClient = httpClient;
        _serverUri = new Uri(serverUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _tokenCache = tokenCache;
        _browserLauncher = browserLauncher;
        _listenerFactory = listenerFactory;
        _signInTimeout = timeoutValue;
    }

    public bool IsAvailable => true;

    public Task<string?> LoadCachedTokenAsync(CancellationToken cancellationToken = default) =>
        _tokenCache.LoadAsync(cancellationToken);

    public static OAuthAuthClient Create(Uri serverUri) =>
        new(
            new HttpClient(),
            serverUri,
            new DpapiTokenCache(),
            new ShellBrowserLauncher(),
            () => new LoopbackCallbackListener());

    public async Task<AuthSession> SignInAsync(
        string provider,
        bool rememberSession = true,
        CancellationToken cancellationToken = default)
    {
        string normalizedProvider = NormalizeProvider(provider);
        using var timeout = new CancellationTokenSource(_signInTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await using ILoopbackCallbackListener listener = _listenerFactory();
            listener.Start();
            Uri startUri = new Uri(
                _serverUri,
                $"auth/{normalizedProvider}/start?returnUri={Uri.EscapeDataString(listener.CallbackUri.AbsoluteUri)}");
            _browserLauncher.Open(startUri);

            string handoffCode = await listener.WaitForCodeAsync(linked.Token);
            if (string.IsNullOrWhiteSpace(handoffCode))
                throw new AuthException("The sign-in handoff was invalid.");

            AuthSession session = await ExchangeAsync(handoffCode, linked.Token);
            if (rememberSession)
                await _tokenCache.SaveAsync(session.AccessToken, linked.Token);
            else
                await _tokenCache.ClearAsync(linked.Token);
            return session;
        }
        catch (AuthException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AuthException("The sign-in flow timed out.", exception);
        }
        catch (IOException exception)
        {
            throw ExpectedFailure(exception);
        }
        catch (SocketException exception)
        {
            throw ExpectedFailure(exception);
        }
        catch (Win32Exception exception)
        {
            throw ExpectedFailure(exception);
        }
        catch (CryptographicException exception)
        {
            throw ExpectedFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw ExpectedFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            throw ExpectedFailure(exception);
        }
    }

    private async Task<AuthSession> ExchangeAsync(string handoffCode, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                new Uri(_serverUri, "auth/session/exchange"),
                new HandoffRequest(handoffCode),
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AuthException("The sign-in service could not be reached.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AuthException("The sign-in service rejected the handoff.");

            TokenResponse? tokenResponse;
            try
            {
                tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new AuthException("The sign-in service returned an invalid session.", exception);
            }

            if (tokenResponse is null
                || string.IsNullOrWhiteSpace(tokenResponse.AccessToken)
                || tokenResponse.ExpiresIn <= 0)
            {
                throw new AuthException("The sign-in service returned an invalid session.");
            }

            return new AuthSession(tokenResponse.AccessToken, tokenResponse.ExpiresIn);
        }
    }

    private static AuthException ExpectedFailure(Exception exception) =>
        new("The sign-in flow could not be completed.", exception);

    private static string NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new AuthException("The selected sign-in provider is not supported.");

        string normalized = provider.Trim().ToLowerInvariant();
        if (!AllowedProviders.Contains(normalized))
            throw new AuthException("The selected sign-in provider is not supported.");

        return normalized;
    }

    private sealed record HandoffRequest([property: JsonPropertyName("code")] string Code);

    private sealed record TokenResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
}
