using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class HardeningIntegrationTests
{
    [Test]
    public async Task Message_rate_limit_returns_retry_after_and_resets()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var factory = new ApiWebApplicationFactory
        {
            Clock = clock,
            RateLimit = 1,
            RateLimitWindowSeconds = 60
        };
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId, Provider = "github", ProviderUserId = "rate-user",
                DisplayName = "Rate User", CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "rate-user");
        static StringContent Message(string body) =>
            new(JsonSerializer.Serialize(new { body, kind = "message" }), Encoding.UTF8, "application/json");

        using HttpResponseMessage first = await client.PostAsync($"/conversations/{conversationId}/messages", Message("one"));
        using HttpResponseMessage limited = await client.PostAsync($"/conversations/{conversationId}/messages", Message("two"));
        string limitedBody = await limited.Content.ReadAsStringAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        using HttpResponseMessage reset = await client.PostAsync($"/conversations/{conversationId}/messages", Message("three"));

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(limitedBody, Is.EqualTo("{\"error\":\"rate_limited\"}"));
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        });
    }

    [Test]
    public async Task Message_rate_limit_counts_case_variant_route_as_the_same_protected_endpoint()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var factory = new ApiWebApplicationFactory
        {
            Clock = clock,
            RateLimit = 1,
            RateLimitWindowSeconds = 60
        };
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId, Provider = "github", ProviderUserId = "rate-case-user",
                DisplayName = "Rate Case User", CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "rate-case-user");
        static StringContent Message(string body) =>
            new(JsonSerializer.Serialize(new { body, kind = "message" }), Encoding.UTF8, "application/json");

        using HttpResponseMessage first = await client.PostAsync(
            $"/conversations/{conversationId}/messages", Message("one"));
        using HttpResponseMessage casingVariant = await client.PostAsync(
            $"/CONVERSATIONS/{conversationId}/MESSAGES", Message("two"));

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(casingVariant.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(casingVariant.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromMinutes(1)));
        });
    }

    [Test]
    public async Task History_rate_limit_returns_retry_after_and_resets()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var factory = new ApiWebApplicationFactory
        {
            Clock = clock,
            RateLimit = 1,
            RateLimitWindowSeconds = 60
        };
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId, Provider = "github", ProviderUserId = "history-rate-user",
                DisplayName = "History Rate User", CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid(), ConversationId = conversationId, AuthorId = userId,
                Body = "history-message", Kind = "message", CreatedAt = now
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "history-rate-user");
        factory.MessageQueryLimits.Clear();

        using HttpResponseMessage first = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=10");
        int queriesAfterFirst = factory.MessageQueryLimits.Count;
        using HttpResponseMessage limited = await client.GetAsync(
            $"/conversations/{conversationId.ToString().ToUpperInvariant()}/MESSAGES?limit=10");
        int queriesAfterLimited = factory.MessageQueryLimits.Count;

        clock.Advance(TimeSpan.FromMinutes(1));
        factory.MessageQueryLimits.Clear();
        using HttpResponseMessage reset = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=10");

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(queriesAfterFirst, Is.GreaterThan(0));
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(queriesAfterLimited, Is.EqualTo(queriesAfterFirst));
            Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(factory.MessageQueryLimits, Is.Not.Empty);
        });
        Assert.That(await limited.Content.ReadAsStringAsync(), Is.EqualTo("{\"error\":\"rate_limited\"}"));
    }

    [TestCase("start", "lower")]
    [TestCase("start", "upper")]
    [TestCase("stop", "lower")]
    [TestCase("stop", "upper")]
    public async Task Screen_actions_share_rate_budget_and_reject_without_publish(
        string blockedAction, string casing)
    {
        const string ScreenStarted = "call.screen.started";
        const string ScreenStopped = "call.screen.stopped";
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var factory = new ApiWebApplicationFactory
        {
            Clock = clock,
            RateLimit = 2,
            RateLimitWindowSeconds = 60
        };
        Guid callerId = Guid.NewGuid();
        Guid calleeId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = callerId, Provider = "github", ProviderUserId = "screen-rate-caller",
                    DisplayName = "Screen Rate Caller", CreatedAt = now, UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = calleeId, Provider = "github", ProviderUserId = "screen-rate-callee",
                    DisplayName = "Screen Rate Callee", CreatedAt = now, UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants =
                {
                    new ParticipantEntity { UserId = callerId, JoinedAt = now },
                    new ParticipantEntity { UserId = calleeId, JoinedAt = now }
                }
            });
            return Task.CompletedTask;
        });

        GatewayHub hub = factory.Services.GetRequiredService<GatewayHub>();
        var callerSink = new RecordingSink("screen-rate-caller-sink", callerId);
        var calleeSink = new RecordingSink("screen-rate-callee-sink", calleeId);
        hub.Register(callerSink);
        hub.Register(calleeSink);

        using HttpClient caller = CreateAuthorizedClient(factory, "screen-rate-caller");
        using HttpClient callee = CreateAuthorizedClient(factory, "screen-rate-callee");

        try
        {
            string ScreenPath(string action) => casing == "upper"
                ? $"/CONVERSATIONS/{conversationId}/CALL/SCREEN/{action.ToUpperInvariant()}"
                : $"/conversations/{conversationId}/call/screen/{action}";

            async Task<HttpStatusCode> PostAsync(HttpClient client, string path, object body)
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
                return response.StatusCode;
            }

            async Task<HttpStatusCode> PostScreenAsync(HttpClient client, string action) =>
                await PostAsync(client, ScreenPath(action), new { reason = "screen" });

            int Events(RecordingSink sink, string type) =>
                sink.Events.Count(envelope => envelope.Type == type);

            Assert.That(
                await PostAsync(caller, $"/conversations/{conversationId}/call/ring", new { reason = "start" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                await PostAsync(caller, $"/conversations/{conversationId}/call/offer", new { sdp = "offer-sdp" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                await PostAsync(callee, $"/conversations/{conversationId}/call/answer", new { sdp = "answer-sdp" }),
                Is.EqualTo(HttpStatusCode.OK));

            if (blockedAction == "stop")
            {
                int callerStarted = Events(callerSink, ScreenStarted);
                Assert.That(await PostScreenAsync(callee, "start"), Is.EqualTo(HttpStatusCode.OK));
                Assert.That(Events(callerSink, ScreenStarted), Is.EqualTo(callerStarted + 1));
            }

            clock.Advance(TimeSpan.FromSeconds(60));

            string firstAction = blockedAction == "stop" ? "stop" : "start";
            string secondAction = blockedAction == "stop" ? "start" : "stop";

            int callerBeforeFirst = callerSink.Events.Count;
            string firstType = firstAction == "start" ? ScreenStarted : ScreenStopped;
            int calleeFirstCount = Events(calleeSink, firstType);
            Assert.That(await PostScreenAsync(caller, firstAction), Is.EqualTo(HttpStatusCode.OK));
            Assert.Multiple(() =>
            {
                Assert.That(Events(calleeSink, firstType), Is.EqualTo(calleeFirstCount + 1));
                Assert.That(callerSink.Events, Has.Count.EqualTo(callerBeforeFirst));
            });

            int callerBeforeSecond = callerSink.Events.Count;
            string secondType = secondAction == "start" ? ScreenStarted : ScreenStopped;
            int calleeSecondCount = Events(calleeSink, secondType);
            Assert.That(await PostScreenAsync(caller, secondAction), Is.EqualTo(HttpStatusCode.OK));
            Assert.Multiple(() =>
            {
                Assert.That(Events(calleeSink, secondType), Is.EqualTo(calleeSecondCount + 1));
                Assert.That(callerSink.Events, Has.Count.EqualTo(callerBeforeSecond));
            });

            int callerSnapshot = callerSink.Events.Count;
            int calleeSnapshot = calleeSink.Events.Count;
            using HttpResponseMessage limited = await caller.PostAsJsonAsync(
                ScreenPath(blockedAction), new { reason = "screen" });

            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            string limitedBody = await limited.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(limitedBody, Is.EqualTo("{\"error\":\"rate_limited\"}"));
                Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(60)));
                Assert.That(callerSink.Events, Has.Count.EqualTo(callerSnapshot));
                Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeSnapshot));
            });

            clock.Advance(TimeSpan.FromSeconds(60));
            string expectedType = blockedAction == "start" ? ScreenStarted : ScreenStopped;
            int calleeAfterExhaustion = Events(calleeSink, expectedType);
            Assert.That(await PostScreenAsync(caller, blockedAction), Is.EqualTo(HttpStatusCode.OK));
            Assert.Multiple(() =>
            {
                Assert.That(Events(calleeSink, expectedType), Is.EqualTo(calleeAfterExhaustion + 1));
                Assert.That(callerSink.Events.Count, Is.EqualTo(callerSnapshot));
            });
        }
        finally
        {
            hub.Remove(callerSink.ConnectionId, callerSink);
            hub.Remove(calleeSink.ConnectionId, calleeSink);
        }
    }

    private static HttpClient CreateAuthorizedClient(ApiWebApplicationFactory factory, string providerUserId)
    {
        HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", providerUserId, providerUserId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class RecordingSink(string connectionId, Guid userId) : IGatewaySink
    {
        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public List<GatewayEnvelope> Events { get; } = [];
        public bool TryEnqueue(GatewayEnvelope envelope) { Events.Add(envelope); return true; }
        public void Abort(GatewayAbortReason reason) { }
        public void Complete() { }
    }
}
