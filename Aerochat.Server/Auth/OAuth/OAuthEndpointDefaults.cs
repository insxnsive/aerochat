namespace Aerochat.Server.Auth.OAuth;

public static class OAuthEndpointDefaults
{
    public const string GoogleAuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string GoogleUserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    public const string GitHubAuthorizationEndpoint = "https://github.com/login/oauth/authorize";
    public const string GitHubTokenEndpoint = "https://github.com/login/oauth/access_token";
    public const string GitHubUserInfoEndpoint = "https://api.github.com/user";

    public const string DiscordAuthorizationEndpoint = "https://discord.com/oauth2/authorize";
    public const string DiscordTokenEndpoint = "https://discord.com/api/oauth2/token";
    public const string DiscordUserInfoEndpoint = "https://discord.com/api/users/@me";
}
