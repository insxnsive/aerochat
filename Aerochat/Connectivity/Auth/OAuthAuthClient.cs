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
            || (serverUri.Scheme != Uri.UriSchemeHttps
                && !(serverUri.Scheme == Uri.UriSchemeHttp && HasExactNumericLoopbackHost(serverUri)))
            || !string.IsNullOrEmpty(serverUri.UserInfo)
            || !StringComparer.Ordinal.Equals(serverUri.AbsolutePath, "/")
            || !string.IsNullOrEmpty(serverUri.Query)
            || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            throw new ArgumentException(
                "The authentication server URI must be an unambiguous HTTPS origin or exact numeric loopback HTTP origin.",
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

    public static OAuthAuthClient Create(Uri serverUri, string? tokenCachePath = null) =>
        new(
            new HttpClient(),
            serverUri,
            new DpapiTokenCache(serverUri, tokenCachePath),
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

            AuthSession session = await ExchangeAsync(handoffCode, listener.CallbackUri.AbsoluteUri, linked.Token);
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

    private async Task<AuthSession> ExchangeAsync(
        string handoffCode,
        string returnUri,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                new Uri(_serverUri, "auth/session/exchange"),
                new HandoffRequest(handoffCode, returnUri),
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

    internal static bool HasExactNumericLoopbackHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            return false;

        string raw = uri.OriginalString;
        int schemeSeparator = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
            return false;

        int authorityStart = schemeSeparator + 3;
        int authorityLength = raw[authorityStart..].IndexOfAny(['/', '?', '#']);
        int authorityEnd = authorityLength < 0
            ? raw.Length
            : authorityStart + authorityLength;
        string authority = raw[authorityStart..authorityEnd];
        if (authority.Length == 0 || authority.Contains('@', StringComparison.Ordinal))
            return false;

        if (authority[0] == '[')
        {
            int closingBracket = authority.IndexOf(']');
            return closingBracket == 4
                && StringComparer.Ordinal.Equals(authority[1..closingBracket], "::1")
                && (closingBracket == authority.Length - 1
                    || authority[closingBracket + 1] == ':');
        }

        int portSeparator = authority.IndexOf(':');
        string host = portSeparator < 0
            ? authority
            : authority[..portSeparator];
        return StringComparer.Ordinal.Equals(host, "127.0.0.1")
            && (portSeparator < 0 || portSeparator == host.Length);
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

    private sealed record HandoffRequest(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("returnUri")] string ReturnUri);

    private sealed record TokenResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
}
