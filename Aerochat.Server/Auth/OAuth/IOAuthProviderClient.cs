namespace Aerochat.Server.Auth.OAuth;

public interface IOAuthProviderClient
{
    Uri BuildAuthorizationUri(
        OAuthProviderDefinition provider,
        string redirectUri,
        string state,
        string codeVerifier);

    Task<ExternalIdentity> AuthenticateAsync(
        OAuthProviderDefinition provider,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default);
}
