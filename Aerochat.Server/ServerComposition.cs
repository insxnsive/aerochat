using System.Security.Cryptography;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Calls;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Aerochat.Server.Gifs;
using Aerochat.Server.Hardening;
using Aerochat.Server.Rest;
using Aerochat.Server.Turn;
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
        // Hosting request-start/finish records include raw query strings. /ws authenticates
        // via a query token, so this category must not reach any logging provider.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            const string category = "Microsoft.AspNetCore.Hosting.Diagnostics";
            string?[] providers = options.Rules.Select(rule => rule.ProviderName)
                .Append(null).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string? provider in providers)
            {
                options.Rules.Add(new LoggerFilterRule(provider, category, LogLevel.None, null));
            }
        });

        string publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5080";
        Dictionary<string, OAuthProviderDefinition> providers = CreateProviderDefinitions(builder.Configuration);
        string chatConnectionString = ResolveChatConnectionString(builder.Configuration);

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(TurnSettings.FromConfiguration(builder.Configuration));
        builder.Services.AddSingleton<TurnCredentialService>();
        RateLimitOptions rateLimitOptions = new()
        {
            Limit = builder.Configuration.GetValue("RateLimit:Limit", 30),
            Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimit:WindowSeconds", 60))
        };
        builder.Services.AddSingleton(rateLimitOptions);
        builder.Services.AddSingleton(sp => new FixedWindowRateLimiter(
            rateLimitOptions.Limit,
            rateLimitOptions.Window,
            sp.GetRequiredService<TimeProvider>()));
        GatewayOptions gatewayOptions = new()
        {
            InstanceId = builder.Configuration["Gateway:InstanceId"],
            QueueCapacity = builder.Configuration.GetValue("Gateway:QueueCapacity", 256),
            ReplayCapacity = builder.Configuration.GetValue("Gateway:ReplayCapacity", 4096),
            MaxFrameBytes = builder.Configuration.GetValue("Gateway:MaxFrameBytes", GatewayJson.DefaultMaxFrameBytes),
            MaxConnectionsPerUser = builder.Configuration.GetValue("Gateway:MaxConnectionsPerUser", 4),
            MaxConnectionsTotal = builder.Configuration.GetValue("Gateway:MaxConnectionsTotal", 256),
            AllowedOrigins = ParseAllowedOrigins(builder.Configuration["Gateway:AllowedOrigins"])
        };
        builder.Services.AddSingleton(gatewayOptions);
        builder.Services.AddSingleton<MeshRooms>();
        builder.Services.AddHostedService<MeshRosterNotifier>();
        builder.Services.AddSingleton<CallRegistry>();
        builder.Services.AddSingleton<GatewayHub>(sp =>
        {
            CallRegistry registry = sp.GetRequiredService<CallRegistry>();
            registry.EnableGatewayPresence();
            return new GatewayHub(sp.GetRequiredService<GatewayOptions>())
            {
                PresenceChanged = (user, connected) =>
                {
                    registry.SetGatewayPresence(user, connected);
                    sp.GetRequiredService<MeshRooms>().SetPresence(user, connected);
                }
            };
        });
        builder.Services.AddSingleton<ConnectedCallCleanupService>();
        builder.Services.AddHostedService<ConnectedCallCleanupService>(sp =>
            sp.GetRequiredService<ConnectedCallCleanupService>());
        builder.Services.AddSingleton<IReadOnlyDictionary<string, OAuthProviderDefinition>>(providers);
        builder.Services.AddSingleton(sp => new SessionService(
            ReadSessionSigningKey(builder.Configuration),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<OAuthFlowStore>();
        builder.Services.AddSingleton<OAuthStartRateLimiter>();
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
        app.UseMiddleware<RateLimitingMiddleware>();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapOAuthEndpoints();
        app.MapConversationEndpoints();
        app.MapCallEndpoints();
        app.MapMeshEndpoints();
        app.MapGifEndpoints();
        app.MapGatewayEndpoints();
        app.MapTurnEndpoints();
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

    private static IReadOnlySet<string> ParseAllowedOrigins(string? configured) =>
        new HashSet<string>(
            (configured ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

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
