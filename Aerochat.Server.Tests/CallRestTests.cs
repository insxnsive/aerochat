using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class CallRestTests
{
    [Test]
    public async Task Ring_without_bearer_returns_401()
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/conversations/{Guid.NewGuid()}/call/ring", new { reason = "start" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
    }

    [Test]
    public async Task Known_non_member_returns_403()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        await SeedConversationAsync(factory, conversationId, "call-member", "call-non-member");
        using HttpClient client = AuthorizedClient(factory, "call-non-member");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/ring", new { reason = "start" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Happy_path_publishes_one_event_per_step()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid memberId, _) = await SeedConversationAsync(factory, conversationId, "call-happy", null);
        var sink = new RecordingSink("call-happy-sink", memberId);
        factory.Services.GetRequiredService<GatewayHub>().Register(sink);
        using HttpClient client = AuthorizedClient(factory, "call-happy");

        (string Path, object Body)[] steps =
        [
            ($"ring", new { reason = "start" }),
            ($"offer", new { sdp = "offer-sdp" }),
            ($"answer", new { sdp = "answer-sdp" }),
            ($"ice", new { candidate = "candidate" }),
            ($"hangup", new { reason = "done" })
        ];
        foreach ((string path, object body) in steps)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{path}", body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        }

        Assert.That(
            sink.Events.Where(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
                .Select(envelope => envelope.Type),
            Is.EqualTo(new[]
            {
                GatewayEventType.CallRing,
                GatewayEventType.CallOffer,
                GatewayEventType.CallAnswer,
                GatewayEventType.CallIce,
                GatewayEventType.CallHangup
            }));
        JsonElement data = JsonDocument.Parse(
            GatewayJson.Serialize(sink.Events.Single(envelope => envelope.Type == GatewayEventType.CallOffer)))
            .RootElement.GetProperty("d");
        Assert.That(data.GetProperty("conversationId").GetGuid(), Is.EqualTo(conversationId));
        Assert.That(data.GetProperty("sdp").GetString(), Is.EqualTo("offer-sdp"));
    }

    [Test]
    public async Task Illegal_sequence_returns_409_without_publishing()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid memberId, _) = await SeedConversationAsync(factory, conversationId, "call-illegal", null);
        var sink = new RecordingSink("call-illegal-sink", memberId);
        factory.Services.GetRequiredService<GatewayHub>().Register(sink);
        using HttpClient client = AuthorizedClient(factory, "call-illegal");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/answer", new { sdp = "answer" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(sink.Events.Any(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task Oversized_payload_returns_400_without_publishing()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid memberId, _) = await SeedConversationAsync(factory, conversationId, "call-large", null);
        var sink = new RecordingSink("call-large-sink", memberId);
        factory.Services.GetRequiredService<GatewayHub>().Register(sink);
        using HttpClient client = AuthorizedClient(factory, "call-large");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/offer", new { sdp = new string('x', 64 * 1024 + 1) });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(sink.Events.Any(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal)), Is.False);
    }

    private static async Task<(Guid UserId, DateTimeOffset Now)> SeedConversationAsync(
        ApiWebApplicationFactory factory, Guid conversationId, string memberProviderId, string? otherProviderId)
    {
        Guid memberId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = memberId, Provider = "github", ProviderUserId = memberProviderId,
                DisplayName = memberProviderId, CreatedAt = now, UpdatedAt = now
            });
            var conversation = new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = memberId, JoinedAt = now } }
            };
            if (otherProviderId is not null)
            {
                Guid otherId = Guid.NewGuid();
                db.Users.Add(new ExternalUserEntity
                {
                    Id = otherId, Provider = "github", ProviderUserId = otherProviderId,
                    DisplayName = otherProviderId, CreatedAt = now, UpdatedAt = now
                });
            }

            db.Conversations.Add(conversation);
            return Task.CompletedTask;
        });
        return (memberId, now);
    }

    private static HttpClient AuthorizedClient(ApiWebApplicationFactory factory, string providerUserId)
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
