using System.Security.Cryptography;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Aerochat.Server.Gifs;
using Aerochat.Server.Rest;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server;

/// <summary>
/// Shared composition root for the Aerochat server. Used by <see cref="Program"/>
/// and by integration fixtures that host the real pipeline on a loopback port.
/// </summary>
internal static class ServerComposition
{
    internal static void ConfigureBuilder(WebApplicationBuilder builder)
    {
        string publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5080";
        Dictionary<string, OAuthProviderDefinition> providers = CreateProviderDefinitions(builder.Configuration);
        string chatConnectionString = ResolveChatConnectionString(builder.Configuration);

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        GatewayOptions gatewayOptions = new()
        {
            InstanceId = builder.Configuration["Gateway:InstanceId"],
            QueueCapacity = builder.Configuration.GetValue("Gateway:QueueCapacity", 256),
            ReplayCapacity = builder.Configuration.GetValue("Gateway:ReplayCapacity", 4096),
            MaxFrameBytes = builder.Configuration.GetValue("Gateway:MaxFrameBytes", GatewayJson.DefaultMaxFrameBytes)
        };
        builder.Services.AddSingleton(gatewayOptions);
        builder.Services.AddSingleton<GatewayHub>();
        builder.Services.AddSingleton<IReadOnlyDictionary<string, OAuthProviderDefinition>>(providers);
        builder.Services.AddSingleton(sp => new SessionService(
            ReadSessionSigningKey(builder.Configuration),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<OAuthFlowStore>();
        builder.Services.AddDbContext<ChatDb>(options => options.UseSqlite(chatConnectionString));
        builder.Services.AddScoped<IExternalUserStore, EfExternalUserStore>();
        builder.Services.AddScoped<ConversationMessageService>();
        builder.Services.AddHttpClient<IOAuthProviderClient, OAuthProviderClient>();
        builder.Services.AddHttpClient<TenorProxyService>().RemoveAllLoggers();
        builder.Services.AddScoped(sp => new OAuthFlowService(
            sp.GetRequiredService<IReadOnlyDictionary<string, OAuthProviderDefinition>>(),
            sp.GetRequiredService<IOAuthProviderClient>(),
            sp.GetRequiredService<IExternalUserStore>(),
            sp.GetRequiredService<OAuthFlowStore>(),
            sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<TimeProvider>(),
            publicBaseUrl));
    }

    internal static async Task ConfigureAppAsync(WebApplication app)
    {
        app.UseWebSockets();
        using IServiceScope scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatDb>().Database.MigrateAsync();
    }

    internal static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapOAuthEndpoints();
        app.MapConversationEndpoints();
        app.MapGifEndpoints();
        app.MapGatewayEndpoints();
    }

    private static Dictionary<string, OAuthProviderDefinition> CreateProviderDefinitions(IConfiguration configuration)
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

    private static byte[] ReadSessionSigningKey(IConfiguration configuration)
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

    private static string ResolveChatConnectionString(IConfiguration configuration)
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
}
