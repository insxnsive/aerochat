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
    public async Task Happy_path_publishes_each_step_to_other_participants_only()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid senderId, Guid receiverId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, "call-happy-sender", "call-happy-receiver");
        var senderSink = new RecordingSink("call-happy-sender-sink", senderId);
        var receiverSink = new RecordingSink("call-happy-receiver-sink", receiverId);
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        gateway.Register(senderSink);
        gateway.Register(receiverSink);
        using HttpClient client = AuthorizedClient(factory, "call-happy-sender");

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
            receiverSink.Events.Where(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
                .Select(envelope => envelope.Type),
            Is.EqualTo(new[]
            {
                GatewayEventType.CallRing,
                GatewayEventType.CallOffer,
                GatewayEventType.CallAnswer,
                GatewayEventType.CallIce,
                GatewayEventType.CallHangup
            }));
        Assert.That(
            senderSink.Events.Any(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal)),
            Is.False);
        JsonElement data = JsonDocument.Parse(
            GatewayJson.Serialize(receiverSink.Events.Single(envelope => envelope.Type == GatewayEventType.CallOffer)))
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

    [TestCase("Ringing")]
    [TestCase("Offering")]
    [TestCase("Connected")]
    public async Task Call_setup_deadlines_expire_pending_sessions_but_preserve_connected_calls(
        string scenario)
    {
        TimeSpan deadline = TimeSpan.FromSeconds(30);
        TimeSpan oneTick = TimeSpan.FromTicks(1);
        var clock = new ManualTimeProvider(new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var factory = new ApiWebApplicationFactory { Clock = clock };
        Guid conversationId = Guid.NewGuid();
        string prefix = $"call-expiry-{scenario.ToLowerInvariant()}";
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, $"{prefix}-caller", $"{prefix}-callee");
        var callerSink = new RecordingSink($"{prefix}-caller-sink", callerId);
        var calleeSink = new RecordingSink($"{prefix}-callee-sink", calleeId);
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        gateway.Register(callerSink);
        gateway.Register(calleeSink);
        using HttpClient caller = AuthorizedClient(factory, $"{prefix}-caller");
        using HttpClient callee = AuthorizedClient(factory, $"{prefix}-callee");

        async Task<HttpStatusCode> PostAsync(HttpClient client, string action, object body)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{action}", body);
            return response.StatusCode;
        }

        int CallEvents() =>
            callerSink.Events.Count(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
            + calleeSink.Events.Count(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal));

        Assert.That(await PostAsync(caller, "ring", new { reason = "start" }), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallEvents(), Is.EqualTo(1));

        if (scenario == "Ringing")
        {
            clock.Advance(deadline - oneTick);
            Assert.That(
                await PostAsync(caller, "ring", new { reason = "duplicate" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(1));

            clock.Advance(oneTick);
            Assert.That(
                await PostAsync(caller, "offer", new { sdp = "late-offer" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(1));

            Assert.That(
                await PostAsync(caller, "ring", new { reason = "replacement" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CallEvents(), Is.EqualTo(2));

            Assert.That(
                await PostAsync(caller, "ring", new { reason = "duplicate" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(2));
            return;
        }

        if (scenario == "Offering")
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.That(
                await PostAsync(caller, "offer", new { sdp = "offer-sdp" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CallEvents(), Is.EqualTo(2));

            clock.Advance(deadline - oneTick);
            Assert.That(
                await PostAsync(caller, "ring", new { reason = "duplicate" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(2));

            clock.Advance(oneTick);
            Assert.That(
                await PostAsync(callee, "answer", new { sdp = "late-answer" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(2));

            Assert.That(
                await PostAsync(caller, "ring", new { reason = "replacement" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CallEvents(), Is.EqualTo(3));

            Assert.That(
                await PostAsync(caller, "ring", new { reason = "duplicate" }),
                Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(CallEvents(), Is.EqualTo(3));
            return;
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.That(
            await PostAsync(caller, "offer", new { sdp = "offer-sdp" }),
            Is.EqualTo(HttpStatusCode.OK));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.That(
            await PostAsync(callee, "answer", new { sdp = "answer-sdp" }),
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallEvents(), Is.EqualTo(3));

        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.That(
            await PostAsync(caller, "ring", new { reason = "duplicate" }),
            Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(CallEvents(), Is.EqualTo(3));

        Assert.That(
            await PostAsync(caller, "ice", new { candidate = "candidate" }),
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallEvents(), Is.EqualTo(4));
    }

    [TestCase("Ringing")]
    [TestCase("Offering")]
    public async Task Negotiating_ice_is_relayed_without_changing_state_or_extending_setup_deadline(
        string scenario)
    {
        TimeSpan deadline = TimeSpan.FromSeconds(30);
        TimeSpan oneTick = TimeSpan.FromTicks(1);
        var clock = new ManualTimeProvider(new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var factory = new ApiWebApplicationFactory { Clock = clock };
        string prefix = $"call-trickle-{scenario.ToLowerInvariant()}";
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();

        // Success path: early ICE must not advance state or make the next handshake illegal.
        Guid successId = Guid.NewGuid();
        (Guid successCallerId, Guid successCalleeId) = await SeedTwoMemberConversationAsync(
            factory, successId, $"{prefix}-success-caller", $"{prefix}-success-callee");
        var successCallerSink = new RecordingSink($"{prefix}-success-caller-sink", successCallerId);
        var successCalleeSink = new RecordingSink($"{prefix}-success-callee-sink", successCalleeId);
        gateway.Register(successCallerSink);
        gateway.Register(successCalleeSink);
        using HttpClient successCaller = AuthorizedClient(factory, $"{prefix}-success-caller");
        using HttpClient successCallee = AuthorizedClient(factory, $"{prefix}-success-callee");

        // Deadline path: a second, independent conversation.
        Guid deadlineId = Guid.NewGuid();
        (Guid deadlineCallerId, Guid deadlineCalleeId) = await SeedTwoMemberConversationAsync(
            factory, deadlineId, $"{prefix}-deadline-caller", $"{prefix}-deadline-callee");
        var deadlineCallerSink = new RecordingSink($"{prefix}-deadline-caller-sink", deadlineCallerId);
        var deadlineCalleeSink = new RecordingSink($"{prefix}-deadline-callee-sink", deadlineCalleeId);
        gateway.Register(deadlineCallerSink);
        gateway.Register(deadlineCalleeSink);
        using HttpClient deadlineCaller = AuthorizedClient(factory, $"{prefix}-deadline-caller");
        using HttpClient deadlineCallee = AuthorizedClient(factory, $"{prefix}-deadline-callee");

        async Task<HttpStatusCode> PostAsync(
            HttpClient client, Guid conversationId, string action, object body)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{action}", body);
            return response.StatusCode;
        }

        int IceEvents(RecordingSink sink) =>
            sink.Events.Count(envelope => envelope.Type == GatewayEventType.CallIce);

        Assert.That(
            await PostAsync(successCaller, successId, "ring", new { reason = "start" }),
            Is.EqualTo(HttpStatusCode.OK));
        if (scenario == "Offering")
        {
            Assert.That(
                await PostAsync(successCaller, successId, "offer", new { sdp = "offer-sdp" }),
                Is.EqualTo(HttpStatusCode.OK));
        }

        int successCallerIce = IceEvents(successCallerSink);
        int successCalleeIce = IceEvents(successCalleeSink);
        Assert.That(
            await PostAsync(successCallee, successId, "ice", new { candidate = "early-candidate" }),
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(IceEvents(successCallerSink), Is.EqualTo(successCallerIce + 1));
        Assert.That(IceEvents(successCalleeSink), Is.EqualTo(successCalleeIce));

        GatewayEnvelope relayedIce = successCallerSink.Events.Last(
            envelope => envelope.Type == GatewayEventType.CallIce);
        JsonElement relayedData = JsonDocument
            .Parse(GatewayJson.Serialize(relayedIce)).RootElement.GetProperty("d");
        Assert.That(relayedData.GetProperty("conversationId").GetGuid(), Is.EqualTo(successId));
        Assert.That(relayedData.GetProperty("candidate").GetString(), Is.EqualTo("early-candidate"));

        if (scenario == "Ringing")
        {
            Assert.That(
                await PostAsync(successCaller, successId, "offer", new { sdp = "offer-after-ice" }),
                Is.EqualTo(HttpStatusCode.OK));
        }

        Assert.That(
            await PostAsync(successCallee, successId, "answer", new { sdp = "answer-sdp" }),
            Is.EqualTo(HttpStatusCode.OK));

        Assert.That(
            await PostAsync(deadlineCaller, deadlineId, "ring", new { reason = "start" }),
            Is.EqualTo(HttpStatusCode.OK));
        if (scenario == "Offering")
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.That(
                await PostAsync(deadlineCaller, deadlineId, "offer", new { sdp = "offer-sdp" }),
                Is.EqualTo(HttpStatusCode.OK));
        }

        clock.Advance(deadline - oneTick);

        int callerIceBeforeDeadline = IceEvents(deadlineCallerSink);
        Assert.That(
            await PostAsync(deadlineCallee, deadlineId, "ice", new { candidate = "one-tick-early" }),
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(IceEvents(deadlineCallerSink), Is.EqualTo(callerIceBeforeDeadline + 1));
        Assert.That(IceEvents(deadlineCalleeSink), Is.Zero);

        clock.Advance(oneTick);

        int callerIceAtDeadline = IceEvents(deadlineCallerSink);
        Assert.That(
            await PostAsync(deadlineCallee, deadlineId, "ice", new { candidate = "at-deadline" }),
            Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(IceEvents(deadlineCallerSink), Is.EqualTo(callerIceAtDeadline));

        Assert.That(
            await PostAsync(deadlineCaller, deadlineId, "ring", new { reason = "replacement" }),
            Is.EqualTo(HttpStatusCode.OK));
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static async Task<(Guid SenderId, Guid ReceiverId)> SeedTwoMemberConversationAsync(
        ApiWebApplicationFactory factory,
        Guid conversationId,
        string senderProviderId,
        string receiverProviderId)
    {
        Guid senderId = Guid.NewGuid();
        Guid receiverId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = senderId, Provider = "github", ProviderUserId = senderProviderId,
                    DisplayName = senderProviderId, CreatedAt = now, UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = receiverId, Provider = "github", ProviderUserId = receiverProviderId,
                    DisplayName = receiverProviderId, CreatedAt = now, UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants =
                {
                    new ParticipantEntity { UserId = senderId, JoinedAt = now },
                    new ParticipantEntity { UserId = receiverId, JoinedAt = now }
                }
            });
            return Task.CompletedTask;
        });
        return (senderId, receiverId);
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
