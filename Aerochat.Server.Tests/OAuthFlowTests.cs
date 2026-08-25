using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;

namespace Aerochat.Server.Tests;

public sealed class OAuthFlowTests
{
    private static OAuthProviderDefinition GoogleDefinition(string clientSecret = "google-secret") =>
        new(
            "google",
            "google-client",
            clientSecret,
            "https://accounts.example/authorize",
            "https://accounts.example/token",
            "https://accounts.example/userinfo",
            ["openid", "profile", "email"]);

    private static OAuthProviderDefinition DiscordDefinition() =>
        new(
            "discord",
            "discord-client",
            "discord-secret",
            "https://discord.example/authorize",
            "https://discord.example/token",
            "https://discord.example/users/@me",
            ["identify", "email"]);

    private static (OAuthFlowService Service, FakeOAuthProviderClient Client, MutableTimeProvider Clock, InMemoryExternalUserStore Users) CreateService(
        Func<string, ExternalIdentity>? identityFactory = null,
        Func<TimeProvider, OAuthFlowStore>? flowStoreFactory = null)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var client = new FakeOAuthProviderClient(identityFactory);
        var users = new InMemoryExternalUserStore();
        var flowStore = flowStoreFactory?.Invoke(clock) ?? new OAuthFlowStore(clock);
        var sessions = new SessionService(
            [
                0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
                0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F,
                0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
                0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
            ],
            clock);

        var service = new OAuthFlowService(
            new Dictionary<string, OAuthProviderDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["google"] = GoogleDefinition()
            },
            client,
            users,
            flowStore,
            sessions,
            clock,
            "http://localhost:5080");

        return (service, client, clock, users);
    }

    [Test]
    public void Non_loopback_return_uri_is_rejected()
    {
        var (service, _, _, _) = CreateService();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => service.Start("google", "https://evil.example/oauth/callback"),
                Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
            Assert.That(
                () => service.Start("google", "http://localhost:4321/oauth/callback"),
                Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
            Assert.That(service.Start("google", "http://127.0.0.1:4321/oauth/callback"), Is.Not.Null);
            Assert.That(service.Start("google", "http://[::1]:4321/oauth/callback"), Is.Not.Null);
        });
    }

    [Test]
    public void Authorization_state_capacity_is_bounded_and_recovers_after_ttl()
    {
        var (service, _, clock, _) = CreateService(
            flowStoreFactory: time => new OAuthFlowStore(
                time,
                maxPendingAuthorizationStates: 1,
                maxPendingHandoffs: 1));

        service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        Assert.That(
            () => service.Start("google", "http://127.0.0.1:4321/oauth/callback"),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(503));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.That(
            service.Start("google", "http://127.0.0.1:4321/oauth/callback"),
            Is.Not.Null);
    }

    [Test]
    public async Task Handoff_capacity_is_bounded_and_recovers_after_ttl()
    {
        var (service, _, clock, _) = CreateService(
            flowStoreFactory: time => new OAuthFlowStore(
                time,
                maxPendingAuthorizationStates: 2,
                maxPendingHandoffs: 1));

        var first = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        await service.CompleteAsync("google", "provider-code", first.State);

        var second = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", second.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(503));

        clock.Advance(TimeSpan.FromSeconds(60));
        var third = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        Assert.That(
            await service.CompleteAsync("google", "provider-code", third.State),
            Is.Not.Null);
    }

    [Test]
    public async Task Missing_callback_inputs_are_bad_requests()
    {
        var (service, _, _, _) = CreateService();
        var start = service.Start("google", "http://127.0.0.1:4321/oauth/callback");

        Assert.That(
            async () => await service.CompleteAsync("google", string.Empty, start.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", string.Empty),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
        Assert.That(
            () => service.ExchangeHandoff(string.Empty),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
    }

    [Test]
    public async Task Bad_or_expired_state_is_rejected()
    {
        var (service, _, clock, _) = CreateService();
        var start = service.Start("google", "http://127.0.0.1:4321/oauth/callback");

        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", "not-the-state"),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", start.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
    }

    [Test]
    public async Task Mismatched_provider_consumes_state()
    {
        var (service, client, _, _) = CreateService();
        var start = service.Start("google", "http://127.0.0.1:4321/oauth/callback");

        Assert.That(
            async () => await service.CompleteAsync("github", "provider-code", start.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", start.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
        Assert.That(client.AuthenticationCount, Is.Zero);
    }

    [Test]
    public async Task State_is_single_use()
    {
        var (service, client, _, _) = CreateService();
        var start = service.Start("google", "http://127.0.0.1:4321/oauth/callback");

        var completed = await service.CompleteAsync("google", "provider-code", start.State);

        Assert.That(completed.RedirectUri.Query, Does.Contain("code="));
        Assert.That(
            async () => await service.CompleteAsync("google", "provider-code", start.State),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
        Assert.That(client.AuthenticationCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Unverified_email_is_dropped()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { access_token = "provider-access-token" });
            }

            return JsonResponse(new
            {
                sub = "google-user-1",
                name = "Google User",
                email = "user@example.com",
                email_verified = false,
                picture = "https://images.example/user.png"
            });
        });
        using var httpClient = new HttpClient(handler);
        var client = new OAuthProviderClient(httpClient);

        var identity = await client.AuthenticateAsync(
            GoogleDefinition(),
            "provider-code",
            new string('v', 43),
            "http://localhost:5080/auth/google/callback",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(identity.Provider, Is.EqualTo("google"));
            Assert.That(identity.ProviderUserId, Is.EqualTo("google-user-1"));
            Assert.That(identity.Email, Is.Null);
            Assert.That(identity.AvatarUrl, Is.EqualTo("https://images.example/user.png"));
        });
    }

    [Test]
    public async Task Discord_verified_email_is_preserved()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { access_token = "provider-access-token" });
            }

            return JsonResponse(new
            {
                id = "discord-user-1",
                username = "nate",
                email = "user@example.com",
                verified = true
            });
        });
        using var httpClient = new HttpClient(handler);
        var client = new OAuthProviderClient(httpClient);

        var identity = await client.AuthenticateAsync(
            DiscordDefinition(),
            "provider-code",
            new string('v', 43),
            "http://localhost:5080/auth/discord/callback",
            CancellationToken.None);

        Assert.That(identity.Email, Is.EqualTo("user@example.com"));
    }

    [Test]
    public async Task Duplicate_login_upserts_same_local_user()
    {
        var (service, client, _, users) = CreateService(code =>
            new ExternalIdentity("google", "same-provider-id", code == "first" ? "First Name" : "Updated Name", "user@example.com", null));

        var first = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        await service.CompleteAsync("google", "first", first.State);

        var second = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        await service.CompleteAsync("google", "second", second.State);

        Assert.That(users.TryGet("google", "same-provider-id", out var user), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(user!.DisplayName, Is.EqualTo("Updated Name"));
            Assert.That(client.AuthenticationCount, Is.EqualTo(2));
        });

        var third = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        await service.CompleteAsync("google", "second", third.State);
        Assert.That(users.TryGet("google", "same-provider-id", out var sameUser), Is.True);
        Assert.That(sameUser!.Id, Is.EqualTo(user!.Id));
    }

    [Test]
    public async Task Handoff_is_single_use_and_expires_after_60_seconds()
    {
        var (service, _, clock, _) = CreateService();
        var first = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        var firstCompletion = await service.CompleteAsync("google", "provider-code", first.State);

        var exchanged = service.ExchangeHandoff(firstCompletion.HandoffCode);
        Assert.Multiple(() =>
        {
            Assert.That(exchanged.AccessToken, Is.Not.Null.And.Not.Empty);
            Assert.That(exchanged.ExpiresIn, Is.EqualTo(3600));
        });
        Assert.That(
            () => service.ExchangeHandoff(firstCompletion.HandoffCode),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));

        var second = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        var secondCompletion = await service.CompleteAsync("google", "provider-code", second.State);
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.That(
            () => service.ExchangeHandoff(secondCompletion.HandoffCode),
            Throws.TypeOf<OAuthFlowException>().And.Property("StatusCode").EqualTo(400));
    }

    [Test]
    public void Authorization_uri_contains_state_and_s256_pkce_challenge_but_not_verifier_or_client_secret()
    {
        var (service, _, _, _) = CreateService();

        var start = service.Start("google", "http://127.0.0.1:4321/oauth/callback");
        var query = ParseQuery(start.AuthorizationUri.Query);

        Assert.Multiple(() =>
        {
            Assert.That(query["state"], Is.EqualTo(start.State));
            Assert.That(query["code_challenge_method"], Is.EqualTo("S256"));
            Assert.That(query["code_challenge"], Is.Not.Null.And.Not.Empty);
            Assert.That(start.AuthorizationUri.AbsoluteUri, Does.Not.Contain("code_verifier"));
            Assert.That(start.AuthorizationUri.AbsoluteUri, Does.Not.Contain("google-secret"));
        });
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString((parts.Length == 2 ? parts[1] : string.Empty).Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };

    private sealed class FakeOAuthProviderClient : IOAuthProviderClient
    {
        private readonly Func<string, ExternalIdentity> _identityFactory;

        public FakeOAuthProviderClient(Func<string, ExternalIdentity>? identityFactory)
        {
            _identityFactory = identityFactory ?? (_ => new ExternalIdentity("google", "google-user-1", "Google User", null, null));
        }

        public int AuthenticationCount { get; private set; }

        public Uri BuildAuthorizationUri(
            OAuthProviderDefinition provider,
            string redirectUri,
            string state,
            string codeVerifier) =>
            OAuthProviderClient.BuildAuthorizationUriCore(provider, redirectUri, state, codeVerifier);

        public Task<ExternalIdentity> AuthenticateAsync(
            OAuthProviderDefinition provider,
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken = default)
        {
            AuthenticationCount++;
            return Task.FromResult(_identityFactory(code));
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
