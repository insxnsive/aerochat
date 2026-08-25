using System.Security.Cryptography;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
string publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5080";
byte[] sessionSigningKey = ReadSessionSigningKey(builder.Configuration);
var sessionService = new SessionService(sessionSigningKey, TimeProvider.System);
var providers = CreateProviderDefinitions(builder.Configuration);
string chatConnectionString = ResolveChatConnectionString(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReadOnlyDictionary<string, OAuthProviderDefinition>>(providers);
builder.Services.AddSingleton<OAuthFlowStore>();
builder.Services.AddDbContext<ChatDb>(options => options.UseSqlite(chatConnectionString));
builder.Services.AddScoped<IExternalUserStore, EfExternalUserStore>();
builder.Services.AddSingleton(sessionService);
builder.Services.AddHttpClient<IOAuthProviderClient, OAuthProviderClient>();
builder.Services.AddScoped(sp => new OAuthFlowService(
    sp.GetRequiredService<IReadOnlyDictionary<string, OAuthProviderDefinition>>(),
    sp.GetRequiredService<IOAuthProviderClient>(),
    sp.GetRequiredService<IExternalUserStore>(),
    sp.GetRequiredService<OAuthFlowStore>(),
    sp.GetRequiredService<SessionService>(),
    sp.GetRequiredService<TimeProvider>(),
    publicBaseUrl));

var app = builder.Build();
using (IServiceScope scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ChatDb>().Database.Migrate();
}

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

static string ResolveChatConnectionString(IConfiguration configuration)
{
    string? configured = configuration.GetConnectionString("Chat");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return new SqliteConnectionStringBuilder(configured)
        {
            ForeignKeys = true
        }.ToString();
    }

    string localApplicationData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localApplicationData))
    {
        throw new InvalidOperationException(
            "A per-user local application data directory is required when ConnectionStrings:Chat is not configured.");
    }

    string dataDirectory = Path.Combine(localApplicationData, "Aerochat");
    Directory.CreateDirectory(dataDirectory);
    return new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, "server.db"),
        ForeignKeys = true
    }.ToString();
}

public partial class Program { }
