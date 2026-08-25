namespace Aerochat.Server.Auth;

public sealed record SessionClaims(
    string Provider,
    string ProviderUserId,
    string DisplayName,
    DateTimeOffset ExpiresAt);
