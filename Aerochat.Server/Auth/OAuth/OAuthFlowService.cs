using System.Net;

namespace Aerochat.Server.Auth.OAuth;

public sealed class OAuthFlowService
{
    private const int BadRequest = 400;
    private const int NotFound = 404;
    private const int ServiceUnavailable = 503;
    private readonly IReadOnlyDictionary<string, OAuthProviderDefinition> _providers;
    private readonly IOAuthProviderClient _providerClient;
    private readonly IExternalUserStore _externalUsers;
    private readonly OAuthFlowStore _flowStore;
    private readonly SessionService _sessions;
    private readonly TimeProvider _clock;
    private readonly Uri _publicBaseUri;

    public OAuthFlowService(
        IReadOnlyDictionary<string, OAuthProviderDefinition> providers,
        IOAuthProviderClient providerClient,
        IExternalUserStore externalUsers,
        OAuthFlowStore flowStore,
        SessionService sessions,
        TimeProvider clock,
        string publicBaseUrl)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        _externalUsers = externalUsers ?? throw new ArgumentNullException(nameof(externalUsers));
        _flowStore = flowStore ?? throw new ArgumentNullException(nameof(flowStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(publicBaseUrl);
        _publicBaseUri = new Uri(publicBaseUrl, UriKind.Absolute);
    }

    public OAuthStartResult Start(string provider, string returnUri)
    {
        OAuthProviderDefinition definition = GetProvider(provider);
        Uri validatedReturnUri = ValidateLoopbackReturnUri(returnUri);
        string normalizedProvider = definition.Name.ToLowerInvariant();
        string codeVerifier = OAuthPkce.CreateVerifier();
        Uri callbackUri = BuildCallbackUri(normalizedProvider);
        string state;
        try
        {
            state = _flowStore.CreateAuthorizationState(
                normalizedProvider,
                codeVerifier,
                validatedReturnUri);
        }
        catch (OAuthFlowCapacityException exception)
        {
            throw AtCapacity(exception);
        }
        Uri authorizationUri = _providerClient.BuildAuthorizationUri(
            definition,
            callbackUri.AbsoluteUri,
            state,
            codeVerifier);

        return new OAuthStartResult(
            normalizedProvider,
            authorizationUri,
            state,
            _clock.GetUtcNow().Add(OAuthFlowStore.AuthorizationStateTtl));
    }

    public async Task<OAuthCompletionResult> CompleteAsync(
        string provider,
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state)
            || !_flowStore.TryConsumeAuthorizationState(state, out OAuthAuthorizationState? authorizationState)
            || authorizationState is null)
        {
            throw InvalidRequest("invalid_state");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw InvalidRequest("invalid_code");
        }

        string normalizedProvider = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!StringComparer.OrdinalIgnoreCase.Equals(authorizationState.Provider, normalizedProvider))
        {
            throw InvalidRequest("invalid_state");
        }

        OAuthProviderDefinition definition = GetProvider(normalizedProvider);

        ExternalIdentity identity;
        try
        {
            identity = await _providerClient.AuthenticateAsync(
                definition,
                code,
                authorizationState.CodeVerifier,
                BuildCallbackUri(normalizedProvider).AbsoluteUri,
                cancellationToken);
        }
        catch (OAuthProviderException exception)
        {
            throw new OAuthFlowException(
                ServiceUnavailable,
                "provider_unavailable",
                exception);
        }

        identity = identity with { Provider = normalizedProvider };
        ExternalUser user = _externalUsers.Upsert(identity, _clock.GetUtcNow());
        string accessToken = _sessions.Issue(
            new Identity(user.Provider, user.ProviderUserId, user.DisplayName));
        string handoffCode;
        try
        {
            handoffCode = _flowStore.CreateHandoff(
                accessToken,
                checked((int)_sessions.DefaultTtl.TotalSeconds));
        }
        catch (OAuthFlowCapacityException exception)
        {
            throw AtCapacity(exception);
        }
        Uri redirectUri = BuildHandoffRedirectUri(authorizationState.ReturnUri, handoffCode);

        return new OAuthCompletionResult(redirectUri, handoffCode);
    }

    public OAuthSessionExchangeResult ExchangeHandoff(string code)
    {
        if (string.IsNullOrWhiteSpace(code)
            || !_flowStore.TryConsumeHandoff(code, out OAuthHandoff? handoff)
            || handoff is null)
        {
            throw InvalidRequest("invalid_handoff");
        }

        return new OAuthSessionExchangeResult(handoff.AccessToken, handoff.ExpiresIn);
    }

    public static Uri ValidateLoopbackReturnUri(string returnUri)
    {
        bool isNumericLoopback = false;
        if (Uri.TryCreate(returnUri, UriKind.Absolute, out Uri? parsedUri)
            && IPAddress.TryParse(parsedUri.DnsSafeHost, out IPAddress? address))
        {
            isNumericLoopback = IPAddress.Loopback.Equals(address)
                || IPAddress.IPv6Loopback.Equals(address);
        }

        if (!Uri.TryCreate(returnUri, UriKind.Absolute, out Uri? uri)
            || !uri.IsAbsoluteUri
            || !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || returnUri.Contains('?', StringComparison.Ordinal)
            || returnUri.Contains('#', StringComparison.Ordinal)
            || !isNumericLoopback
            || !HasExactNumericLoopbackHost(returnUri))
        {
            throw InvalidRequest("invalid_return_uri");
        }

        return uri;
    }

    private static bool HasExactNumericLoopbackHost(string returnUri)
    {
        int schemeSeparator = returnUri.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        int authorityStart = schemeSeparator + 3;
        int authorityLength = returnUri[authorityStart..]
            .IndexOfAny(['/', '?', '#']);
        int authorityEnd = authorityLength < 0
            ? returnUri.Length
            : authorityStart + authorityLength;
        string authority = returnUri[authorityStart..authorityEnd];
        if (authority.Length == 0 || authority.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

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

    private OAuthProviderDefinition GetProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || !_providers.TryGetValue(provider, out OAuthProviderDefinition? definition))
        {
            throw new OAuthFlowException(NotFound, "unsupported_provider");
        }

        if (!definition.HasCredentials)
        {
            throw new OAuthFlowException(ServiceUnavailable, "provider_not_configured");
        }

        return definition;
    }

    private Uri BuildCallbackUri(string provider)
    {
        return new Uri(_publicBaseUri, $"/auth/{Uri.EscapeDataString(provider)}/callback");
    }

    private static Uri BuildHandoffRedirectUri(Uri returnUri, string handoffCode)
    {
        var builder = new UriBuilder(returnUri)
        {
            Query = $"code={WebUtility.UrlEncode(handoffCode)}"
        };
        return builder.Uri;
    }

    private static OAuthFlowException AtCapacity(OAuthFlowCapacityException exception) =>
        new(ServiceUnavailable, "oauth_capacity", exception);

    private static OAuthFlowException InvalidRequest(string errorCode) =>
        new(BadRequest, errorCode);
}
