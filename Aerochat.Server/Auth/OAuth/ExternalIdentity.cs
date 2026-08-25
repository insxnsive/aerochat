namespace Aerochat.Server.Auth.OAuth;

public sealed record ExternalIdentity(
    string Provider,
    string ProviderUserId,
    string DisplayName,
    string? Email,
    string? AvatarUrl);
