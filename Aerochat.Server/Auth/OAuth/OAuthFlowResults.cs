namespace Aerochat.Server.Auth.OAuth;

public sealed record OAuthStartResult(
    string Provider,
    Uri AuthorizationUri,
    string State,
    DateTimeOffset ExpiresAt);

public sealed record OAuthCompletionResult(
    Uri RedirectUri,
    string HandoffCode);

public sealed record OAuthSessionExchangeResult(
    string AccessToken,
    int ExpiresIn);
