using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Hardening;
using Aerochat.Server.Turn;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aerochat.Server.Tests;

public sealed class TurnTests
{
    // Synthetic fixture only. Never use this value in a deployment.
    private const string Secret = "test-only-turn-secret-0123456789abcdef";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static Dictionary<string, string?> Config() => new()
    {
        ["Turn:Urls:0"] = "turn:relay.example:3478?transport=udp",
        ["Turn:Urls:1"] = "turns:relay.example:5349?transport=tcp",
        ["Turn:SharedSecret"] = Secret
    };
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void Credential_has_known_HMAC_SHA1_vector_and_injected_expiry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1770000000));
        var service = new TurnCredentialService(TurnSettings.FromConfiguration(Configuration(Config())), clock);
        TurnCredentialsResponse response = service.Issue(UserId);
        Assert.Multiple(() =>
        {
            Assert.That(response.Ttl, Is.EqualTo(3600));
            Assert.That(response.IceServers, Has.Length.EqualTo(1));
            Assert.That(response.IceServers[0].Urls, Is.EqualTo(new[] { Config()["Turn:Urls:0"], Config()["Turn:Urls:1"] }));
            Assert.That(response.IceServers[0].Username, Is.EqualTo("1770003600:11111111111111111111111111111111"));
            Assert.That(response.IceServers[0].Credential, Is.EqualTo("ACB8/divVU8S0G1jg3MDVqrtCPc="));
        });
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.That(service.Issue(UserId).IceServers[0].Username, Does.StartWith("1770003607:"));
        Assert.That(service.Issue(Guid.Parse("22222222-2222-2222-2222-222222222222")).IceServers[0].Credential,
            Is.Not.EqualTo(response.IceServers[0].Credential));
    }

    [TestCase(60)]
    [TestCase(86400)]
    public void Configured_TTL_endpoints_are_accepted(int ttl)
    {
        var config = Config(); config["Turn:CredentialTtlSeconds"] = ttl.ToString();
        var service = new TurnCredentialService(TurnSettings.FromConfiguration(Configuration(config)),
            new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1770000000)));
        Assert.That(service.Issue(UserId).Ttl, Is.EqualTo(ttl));
        Assert.That(service.Issue(UserId).IceServers[0].Username, Does.StartWith((1770000000L + ttl) + ":"));
    }

    [TestCase("turn:relay.example")]
    [TestCase("turns:relay.example:5349?transport=tcp")]
    [TestCase("turn:127.0.0.1:3478?transport=udp")]
    [TestCase("turn:[::1]:3478?transport=tcp")]
    [TestCase("TURNS:relay.example:5349")]
    public void Valid_TURN_URL_is_retained(string url)
    {
        var config = Config(); config["Turn:Urls:0"] = url;
        Assert.That(TurnSettings.FromConfiguration(Configuration(config)).Urls[0], Is.EqualTo(url));
    }

    [TestCase("https://relay.example")]
    [TestCase("stun:relay.example")]
    [TestCase("turn:")]
    [TestCase("turn://relay.example")]
    [TestCase("turn:user@relay.example")]
    [TestCase("turn:relay.example/path")]
    [TestCase("turn:relay.example#fragment")]
    [TestCase("turn:relay.example:0")]
    [TestCase("turn:relay.example:65536")]
    [TestCase("turn:relay.example?transport=invalid")]
    [TestCase("turn:relay..example")]
    [TestCase("turn:[not-ipv6]")]
    [TestCase("turn:relay.example\r\n")]
    [TestCase("")]
    public void Invalid_TURN_URL_is_rejected(string url)
    {
        var config = Config(); config["Turn:Urls:0"] = url;
        Assert.Throws<InvalidOperationException>(() => TurnSettings.FromConfiguration(Configuration(config)));
    }

    [TestCase("ttl-low")]
    [TestCase("ttl-high")]
    [TestCase("ttl-text")]
    [TestCase("limit-zero")]
    [TestCase("window-zero")]
    [TestCase("secret-only")]
    [TestCase("urls-only")]
    [TestCase("short-secret")]
    [TestCase("session-secret-reuse")]
    [TestCase("scalar-urls")]
    public void Invalid_settings_fail_validation(string kind)
    {
        var config = Config();
        switch (kind)
        {
            case "ttl-low": config["Turn:CredentialTtlSeconds"] = "59"; break;
            case "ttl-high": config["Turn:CredentialTtlSeconds"] = "86401"; break;
            case "ttl-text": config["Turn:CredentialTtlSeconds"] = "bad"; break;
            case "limit-zero": config["Turn:RateLimit:Limit"] = "0"; break;
            case "window-zero": config["Turn:RateLimit:WindowSeconds"] = "0"; break;
            case "secret-only": config.Remove("Turn:Urls:0"); config.Remove("Turn:Urls:1"); break;
            case "urls-only": config.Remove("Turn:SharedSecret"); break;
            case "short-secret": config["Turn:SharedSecret"] = "short"; break;
            case "session-secret-reuse": config["Auth:SessionSigningKey"] = Secret; break;
            case "scalar-urls": config["Turn:Urls"] = "turn:relay.example"; break;
        }
        Assert.Throws<InvalidOperationException>(() => TurnSettings.FromConfiguration(Configuration(config)));
    }

    [TestCase("Turn:Urls:0")]
    [TestCase("Turn:CredentialTtlSeconds")]
    [TestCase("Turn:RateLimit:Limit")]
    [TestCase("Turn:RateLimit:WindowSeconds")]
    [TestCase("Turn:SharedSecret")]
    public void Startup_errors_and_logged_exceptions_do_not_echo_secret(string key)
    {
        var config = Config();
        config[key] = key == "Turn:SharedSecret" ? Secret + "\n" : Secret;
        config["ConnectionStrings:Chat"] = "Data Source=:memory:";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(config);
        var logs = new CaptureProvider();
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs);
        Exception? error = Assert.Throws<InvalidOperationException>(() => ServerComposition.ConfigureBuilder(builder));
        Assert.That(error!.ToString(), Does.Not.Contain(Secret));
        Assert.That(error.InnerException, Is.Null);
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        factory.CreateLogger("startup-probe").LogError(error, "Startup rejected");
        Assert.That(logs.Lines, Is.Not.Empty);
        Assert.That(string.Join("\n", logs.Lines), Does.Not.Contain(Secret));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Authenticated_response_has_expected_shape_and_no_store(bool enabled)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var source = new ApiWebApplicationFactory { Clock = clock };
        var logs = new CaptureProvider();
        using var factory = source.WithWebHostBuilder(builder =>
        {
            if (enabled) foreach (var pair in Config()) builder.UseSetting(pair.Key, pair.Value);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        await Seed(factory, source, "turn-user", UserId);
        using var client = Authorized(factory, "turn-user");
        using var response = await client.GetAsync("/turn/credentials");
        string json = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(json, Does.Not.Contain(Secret));
            Assert.That(string.Join("\n", logs.Lines), Does.Not.Contain(Secret));
        });
        using var body = JsonDocument.Parse(json);
        Assert.That(body.RootElement.GetProperty("ttl").GetInt32(), Is.EqualTo(enabled ? 3600 : 0));
        var servers = body.RootElement.GetProperty("iceServers");
        Assert.That(servers.GetArrayLength(), Is.EqualTo(enabled ? 1 : 0));
        if (enabled)
        {
            Assert.That(servers[0].GetProperty("urls").GetArrayLength(), Is.EqualTo(2));
            Assert.That(servers[0].GetProperty("username").GetString(), Is.EqualTo((clock.GetUtcNow().ToUnixTimeSeconds() + 3600) + ":" + UserId.ToString("N")));
            Assert.That(servers[0].GetProperty("credential").GetString(), Is.Not.Empty);
        }
    }

    [TestCase("absent")]
    [TestCase("invalid")]
    [TestCase("blank")]
    [TestCase("basic")]
    [TestCase("unknown-user")]
    [TestCase("expired")]
    public async Task Credentials_require_a_live_session_and_local_user(string kind)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var source = new ApiWebApplicationFactory { Clock = clock };
        using var factory = source.WithWebHostBuilder(builder =>
        { foreach (var pair in Config()) builder.UseSetting(pair.Key, pair.Value); });
        if (kind == "expired") await Seed(factory, source, "auth-user", UserId);
        using var client = factory.CreateClient();
        string? authorization = kind switch
        {
            "absent" => null, "invalid" => "Bearer invalid", "blank" => "Bearer ", "basic" => "Basic abc",
            _ => "Bearer " + factory.Services.GetRequiredService<SessionService>().Issue(new Identity("github", "auth-user", "User"))
        };
        if (kind == "expired") clock.Advance(TimeSpan.FromHours(2));
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.GetAsync("/turn/credentials");
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    [TestCase(6, 60)]
    [TestCase(2, 10)]
    public async Task TURN_quota_is_per_user_independent_and_resets_on_injected_time(int limit, int window)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var source = new ApiWebApplicationFactory { Clock = clock };
        using var factory = source.WithWebHostBuilder(builder =>
        {
            foreach (var pair in Config()) builder.UseSetting(pair.Key, pair.Value);
            if (limit != 6)
            {
                builder.UseSetting("Turn:RateLimit:Limit", limit.ToString());
                builder.UseSetting("Turn:RateLimit:WindowSeconds", window.ToString());
            }
        });
        await Seed(factory, source, "quota-user", UserId);
        await Seed(factory, source, "other-user", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        using var unauthenticated = factory.CreateClient();
        for (int i = 0; i < 8; i++)
        {
            using var response = await unauthenticated.GetAsync("/turn/credentials");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        // Exhaust the existing message/call limiter: TURN must have a separate quota.
        var global = factory.Services.GetRequiredService<FixedWindowRateLimiter>();
        for (int i = 0; i < 31; i++) global.TryAcquire(UserId.ToString("N"));
        using var client = Authorized(factory, "quota-user");
        for (int i = 0; i < limit; i++)
        {
            using var response = await client.GetAsync("/turn/credentials");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        using var limited = await client.GetAsync("/turn/credentials");
        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await limited.Content.ReadAsStringAsync(), Is.EqualTo("{\"error\":\"rate_limited\"}"));
        Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(window)));
        Assert.That(limited.Headers.CacheControl?.NoStore, Is.True);
        using var other = Authorized(factory, "other-user");
        using var independent = await other.GetAsync("/turn/credentials");
        Assert.That(independent.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        clock.Advance(TimeSpan.FromSeconds(window - 1));
        using var stillLimited = await client.GetAsync("/turn/credentials");
        Assert.That(stillLimited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(stillLimited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(1));
        using var reset = await client.GetAsync("/turn/credentials");
        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static async Task Seed(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        ApiWebApplicationFactory source, string providerId, Guid id)
    {
        _ = source;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Aerochat.Server.Data.ChatDb>();
        db.Users.Add(new ExternalUserEntity { Id = id, Provider = "github", ProviderUserId = providerId,
            DisplayName = "TURN User", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
    private static HttpClient Authorized(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string providerId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<SessionService>().Issue(new Identity("github", providerId, "TURN User")));
        return client;
    }
    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Lines);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception) + exception);
        }
    }
}
