namespace Aerochat.Server.Auth;

public sealed record Identity(string Provider, string ProviderUserId, string DisplayName);
