namespace Aerochat.Server.Auth.OAuth;

public sealed record ExternalUser(
    Guid Id,
    string Provider,
    string ProviderUserId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
