using System.Security.Cryptography;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;

var builder = WebApplication.CreateBuilder(args);
string publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5080";
byte[] sessionSigningKey = ReadSessionSigningKey(builder.Configuration);
var sessionService = new SessionService(sessionSigningKey, TimeProvider.System);
var providers = CreateProviderDefinitions(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReadOnlyDictionary<string, OAuthProviderDefinition>>(providers);
builder.Services.AddSingleton<OAuthFlowStore>();
builder.Services.AddSingleton<IExternalUserStore, InMemoryExternalUserStore>();
builder.Services.AddSingleton(sessionService);
builder.Services.AddHttpClient<IOAuthProviderClient, OAuthProviderClient>();
builder.Services.AddTransient(sp => new OAuthFlowService(
    sp.GetRequiredService<IReadOnlyDictionary<string, OAuthProviderDefinition>>(),
    sp.GetRequiredService<IOAuthProviderClient>(),
    sp.GetRequiredService<IExternalUserStore>(),
    sp.GetRequiredService<OAuthFlowStore>(),
    sp.GetRequiredService<SessionService>(),
    sp.GetRequiredService<TimeProvider>(),
    publicBaseUrl));

var app = builder.Build();
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapOAuthEndpoints();
app.Run();

static Dictionary<string, OAuthProviderDefinition> CreateProviderDefinitions(IConfiguration configuration)
{
    return new Dictionary<string, OAuthProviderDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = new OAuthProviderDefinition(
            "google",
            configuration["Auth:Google:ClientId"] ?? string.Empty,
            configuration["Auth:Google:ClientSecret"] ?? string.Empty,
            OAuthEndpointDefaults.GoogleAuthorizationEndpoint,
            OAuthEndpointDefaults.GoogleTokenEndpoint,
            OAuthEndpointDefaults.GoogleUserInfoEndpoint,
            ["openid", "profile", "email"]),
        ["github"] = new OAuthProviderDefinition(
            "github",
            configuration["Auth:GitHub:ClientId"] ?? string.Empty,
            configuration["Auth:GitHub:ClientSecret"] ?? string.Empty,
            OAuthEndpointDefaults.GitHubAuthorizationEndpoint,
            OAuthEndpointDefaults.GitHubTokenEndpoint,
            OAuthEndpointDefaults.GitHubUserInfoEndpoint,
            ["read:user", "user:email"]),
        ["discord"] = new OAuthProviderDefinition(
            "discord",
            configuration["Auth:Discord:ClientId"] ?? string.Empty,
            configuration["Auth:Discord:ClientSecret"] ?? string.Empty,
            OAuthEndpointDefaults.DiscordAuthorizationEndpoint,
            OAuthEndpointDefaults.DiscordTokenEndpoint,
            OAuthEndpointDefaults.DiscordUserInfoEndpoint,
            ["identify", "email"])
    };
}

static byte[] ReadSessionSigningKey(IConfiguration configuration)
{
    string? configured = configuration["Auth:SessionSigningKey"];
    if (string.IsNullOrWhiteSpace(configured))
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    try
    {
        byte[] key = Convert.FromBase64String(configured);
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Auth:SessionSigningKey must not be empty.");
        }

        return key;
    }
    catch (FormatException exception)
    {
        throw new InvalidOperationException("Auth:SessionSigningKey must be valid base64.", exception);
    }
}

public partial class Program { }
