namespace Aerochat.Server.Auth.OAuth;

public sealed record OAuthProviderDefinition(
    string Name,
    string ClientId,
    string ClientSecret,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string UserInfoEndpoint,
    IReadOnlyList<string> Scopes)
{
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
