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
        using HttpClient callerClient = AuthorizedClient(factory, "call-happy-sender");
        using HttpClient calleeClient = AuthorizedClient(factory, "call-happy-receiver");

        (string Path, HttpClient Client, object Body)[] steps =
        [
            ("ring", callerClient, new { reason = "start" }),
            ("offer", callerClient, new { sdp = "offer-sdp" }),
            ("answer", calleeClient, new { sdp = "answer-sdp" }),
            ("ice", callerClient, new { candidate = "candidate" }),
            ("hangup", callerClient, new { reason = "done" })
        ];
        foreach ((string path, HttpClient stepClient, object body) in steps)
        {
            using HttpResponseMessage response = await stepClient.PostAsJsonAsync(
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
                GatewayEventType.CallIce,
                GatewayEventType.CallHangup
            }));
        Assert.That(
            senderSink.Events.Where(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
                .Select(envelope => envelope.Type),
            Is.EqualTo(new[]
            {
                GatewayEventType.CallAnswer
            }));
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
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, "call-illegal-caller", "call-illegal-callee");
        var sink = new RecordingSink("call-illegal-sink", calleeId);
        factory.Services.GetRequiredService<GatewayHub>().Register(sink);
        using HttpClient client = AuthorizedClient(factory, "call-illegal-callee");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/answer", new { sdp = "answer" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(sink.Events.Any(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal)), Is.False);
        Assert.That(callerId, Is.Not.EqualTo(calleeId));
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

    [TestCase("GroupWithTwoMembers")]
    [TestCase("DmWithOneMember")]
    [TestCase("DmWithThreeMembers")]
    [TestCase("CalleeOffering")]
    [TestCase("CallerAnswering")]
    [TestCase("ValidTwoPartyLifecycle")]
    [TestCase("ReplacedParticipant")]
    public async Task Dm_call_identity_enforces_topology_roles_and_recipient(string scenario)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var factory = new ApiWebApplicationFactory { Clock = clock };
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        string prefix = $"call-identity-{scenario.ToLowerInvariant()}";
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        Guid conversationId = Guid.NewGuid();
        string kind = scenario == "GroupWithTwoMembers" ? "group" : "dm";
        int memberCount = scenario switch
        {
            "DmWithOneMember" => 1,
            "DmWithThreeMembers" => 3,
            _ => 2
        };
        string[] providers = Enumerable.Range(0, memberCount)
            .Select(index => $"{prefix}-member-{index}")
            .ToArray();
        Guid[] memberIds = providers.Select(_ => Guid.NewGuid()).ToArray();

        await factory.SeedAsync(db =>
        {
            for (int index = 0; index < memberIds.Length; index++)
            {
                db.Users.Add(new ExternalUserEntity
                {
                    Id = memberIds[index], Provider = "github", ProviderUserId = providers[index],
                    DisplayName = providers[index], CreatedAt = now, UpdatedAt = now
                });
            }

            var conversation = new ConversationEntity
            {
                Id = conversationId, Kind = kind, CreatedAt = now
            };
            foreach (Guid memberId in memberIds)
            {
                conversation.Participants.Add(new ParticipantEntity { UserId = memberId, JoinedAt = now });
            }

            db.Conversations.Add(conversation);
            return Task.CompletedTask;
        });

        var sinks = new Dictionary<Guid, RecordingSink>();
        var clients = new List<HttpClient>();
        try
        {
            foreach (Guid memberId in memberIds)
            {
                string providerId = providers[Array.IndexOf(memberIds, memberId)];
                var sink = new RecordingSink($"{providerId}-sink", memberId);
                gateway.Register(sink);
                sinks[memberId] = sink;
                clients.Add(AuthorizedClient(factory, providerId));
            }

            int MemberEvents(Guid memberId) => sinks[memberId].Events
                .Count(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal));
            int CallEvents(RecordingSink sink) => sink.Events
                .Count(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal));
            int AllEvents() => memberIds.Sum(MemberEvents);
            async Task<HttpStatusCode> PostAsync(HttpClient client, string action, object body)
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(
                    $"/conversations/{conversationId}/call/{action}", body);
                return response.StatusCode;
            }

            if (scenario is "GroupWithTwoMembers" or "DmWithOneMember" or "DmWithThreeMembers")
            {
                using HttpResponseMessage response = await clients[0].PostAsJsonAsync(
                    $"/conversations/{conversationId}/call/ring", new { reason = "start" });

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("call_invalid_conversation"));
                Assert.That(AllEvents(), Is.Zero);
                return;
            }

            HttpClient Caller = clients[0];
            HttpClient Callee = clients[1];
            Guid CallerId = memberIds[0];
            Guid CalleeId = memberIds[1];

            if (scenario == "CalleeOffering")
            {
                Assert.That(
                    await PostAsync(Caller, "ring", new { reason = "start" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(MemberEvents(CalleeId), Is.EqualTo(1));

                Assert.That(
                    await PostAsync(Callee, "offer", new { sdp = "callee-offer" }),
                    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(MemberEvents(CalleeId), Is.EqualTo(1));
                Assert.That(MemberEvents(CallerId), Is.Zero);

                Assert.That(
                    await PostAsync(Caller, "offer", new { sdp = "caller-offer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(MemberEvents(CalleeId), Is.EqualTo(2));

                Assert.That(
                    await PostAsync(Callee, "answer", new { sdp = "callee-answer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(MemberEvents(CallerId), Is.EqualTo(1));
                return;
            }

            if (scenario == "CallerAnswering")
            {
                Assert.That(
                    await PostAsync(Caller, "ring", new { reason = "start" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    await PostAsync(Caller, "offer", new { sdp = "caller-offer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(MemberEvents(CalleeId), Is.EqualTo(2));

                Assert.That(
                    await PostAsync(Caller, "answer", new { sdp = "caller-answer" }),
                    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(MemberEvents(CalleeId), Is.EqualTo(2));
                Assert.That(MemberEvents(CallerId), Is.Zero);

                Assert.That(
                    await PostAsync(Callee, "answer", new { sdp = "callee-answer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(MemberEvents(CallerId), Is.EqualTo(1));
                return;
            }

            if (scenario == "ValidTwoPartyLifecycle")
            {
                Assert.That(
                    await PostAsync(Caller, "ring", new { reason = "start" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    await PostAsync(Caller, "offer", new { sdp = "caller-offer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    await PostAsync(Callee, "answer", new { sdp = "callee-answer" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    await PostAsync(Callee, "ice", new { candidate = "callee-candidate" }),
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    await PostAsync(Caller, "hangup", new { reason = "done" }),
                    Is.EqualTo(HttpStatusCode.OK));

                Assert.That(
                    sinks[CalleeId].Events
                        .Where(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
                        .Select(envelope => envelope.Type),
                    Is.EqualTo(new[]
                    {
                        GatewayEventType.CallRing,
                        GatewayEventType.CallOffer,
                        GatewayEventType.CallHangup
                    }));
                Assert.That(
                    sinks[CallerId].Events
                        .Where(envelope => envelope.Type.StartsWith("call.", StringComparison.Ordinal))
                        .Select(envelope => envelope.Type),
                    Is.EqualTo(new[]
                    {
                        GatewayEventType.CallAnswer,
                        GatewayEventType.CallIce
                    }));
                return;
            }

            Assert.That(
                await PostAsync(Caller, "ring", new { reason = "start" }),
                Is.EqualTo(HttpStatusCode.OK));
            Assert.That(MemberEvents(CalleeId), Is.EqualTo(1));

            Guid replacementId = Guid.NewGuid();
            string replacementProvider = $"{prefix}-replacement";
            await factory.SeedAsync(db =>
            {
                ParticipantEntity? replaced = db.Participants.SingleOrDefault(
                    participant => participant.ConversationId == conversationId
                        && participant.UserId == CalleeId);
                if (replaced is not null)
                {
                    db.Participants.Remove(replaced);
                }

                db.Users.Add(new ExternalUserEntity
                {
                    Id = replacementId, Provider = "github", ProviderUserId = replacementProvider,
                    DisplayName = replacementProvider, CreatedAt = now, UpdatedAt = now
                });
                db.Participants.Add(new ParticipantEntity
                {
                    ConversationId = conversationId, UserId = replacementId, JoinedAt = now
                });
                return Task.CompletedTask;
            });
            var replacementSink = new RecordingSink($"{replacementProvider}-sink", replacementId);
            gateway.Register(replacementSink);
            using HttpClient replacement = AuthorizedClient(factory, replacementProvider);

            Assert.That(
                await PostAsync(replacement, "answer", new { sdp = "replacement-answer" }),
                Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(CallEvents(replacementSink), Is.Zero);

            Assert.That(
                await PostAsync(Callee, "answer", new { sdp = "removed-callee-answer" }),
                Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(MemberEvents(CalleeId), Is.EqualTo(1));

            Assert.That(
                await PostAsync(Caller, "ice", new { candidate = "caller-candidate" }),
                Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(CallEvents(replacementSink), Is.Zero);
            Assert.That(MemberEvents(CalleeId), Is.EqualTo(1));
        }
        finally
        {
            foreach (HttpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    [TestCase("Caller")]
    [TestCase("Callee")]
    public async Task Connected_screen_share_start_stop_publish_to_other_participants_only(string actor)
    {
        const string ScreenStarted = "call.screen.started";
        const string ScreenStopped = "call.screen.stopped";
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        string prefix = $"screen-{actor.ToLowerInvariant()}";
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, $"{prefix}-caller", $"{prefix}-callee");
        var callerSink = new RecordingSink($"{prefix}-caller-sink", callerId);
        var calleeSink = new RecordingSink($"{prefix}-callee-sink", calleeId);
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        gateway.Register(callerSink);
        gateway.Register(calleeSink);
        using HttpClient caller = AuthorizedClient(factory, $"{prefix}-caller");
        using HttpClient callee = AuthorizedClient(factory, $"{prefix}-callee");
        bool callerIsActor = actor == "Caller";
        HttpClient screening = callerIsActor ? caller : callee;
        HttpClient other = callerIsActor ? callee : caller;
        RecordingSink otherSink = callerIsActor ? calleeSink : callerSink;
        RecordingSink actorSink = callerIsActor ? callerSink : calleeSink;

        async Task<HttpStatusCode> PostAsync(HttpClient client, string action)
        {
            object body = action is "offer" or "answer"
                ? new { sdp = $"{action}-sdp" }
                : new { reason = "screen" };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{action}", body);
            return response.StatusCode;
        }

        int ScreenEvents(RecordingSink sink) => sink.Events
            .Count(envelope => envelope.Type is ScreenStarted or ScreenStopped);

        Assert.That(await PostAsync(screening, "screen/start"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await PostAsync(screening, "screen/stop"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(ScreenEvents(otherSink), Is.Zero);

        Assert.That(await PostAsync(caller, "ring"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PostAsync(caller, "offer"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PostAsync(callee, "answer"), Is.EqualTo(HttpStatusCode.OK));

        int otherBefore = ScreenEvents(otherSink);
        Assert.That(await PostAsync(screening, "screen/start"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 1));
        Assert.That(otherSink.Events.Last().Type, Is.EqualTo(ScreenStarted));
        Assert.That(ScreenEvents(actorSink), Is.Zero);

        Assert.That(await PostAsync(screening, "screen/start"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 1));

        Assert.That(await PostAsync(screening, "screen/stop"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 2));
        Assert.That(otherSink.Events.Last().Type, Is.EqualTo(ScreenStopped));

        Assert.That(await PostAsync(screening, "screen/stop"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 2));

        Assert.That(await PostAsync(screening, "screen/start"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PostAsync(screening, "screen/stop"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 4));

        Assert.That(await PostAsync(caller, "hangup"), Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await PostAsync(screening, "screen/start"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await PostAsync(screening, "screen/stop"), Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(ScreenEvents(otherSink), Is.EqualTo(otherBefore + 4));
    }

    [Test]
    public async Task Screen_share_endpoints_enforce_membership_identity_and_topology()
    {
        const string ScreenStarted = "call.screen.started";
        using var factory = new ApiWebApplicationFactory();
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        Guid dmId = Guid.NewGuid();
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, dmId, "screen-guard-caller", "screen-guard-callee");
        var calleeSink = new RecordingSink("screen-guard-callee-sink", calleeId);
        gateway.Register(calleeSink);

        (Guid outsiderId, _) = await SeedConversationAsync(
            factory, Guid.NewGuid(), "screen-guard-outsider", null);
        using HttpClient outsider = AuthorizedClient(factory, "screen-guard-outsider");
        using HttpResponseMessage outsiderResponse = await outsider.PostAsJsonAsync(
            $"/conversations/{dmId}/call/screen/start", new { reason = "screen" });
        Assert.That(outsiderId, Is.Not.EqualTo(callerId));
        Assert.That(outsiderResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(calleeSink.Events.Any(envelope => envelope.Type == ScreenStarted), Is.False);

        using HttpClient caller = AuthorizedClient(factory, "screen-guard-caller");
        using HttpClient callee = AuthorizedClient(factory, "screen-guard-callee");
        foreach ((HttpClient client, string action, object body) in new (HttpClient, string, object)[]
                 {
                     (caller, "ring", new { reason = "start" }),
                     (caller, "offer", new { sdp = "offer-sdp" }),
                     (callee, "answer", new { sdp = "answer-sdp" })
                 })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{dmId}/call/{action}", body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), action);
        }

        Guid replacementId = Guid.NewGuid();
        await factory.SeedAsync(db =>
        {
            ParticipantEntity? replaced = db.Participants.SingleOrDefault(
                participant => participant.ConversationId == dmId && participant.UserId == calleeId);
            if (replaced is not null)
            {
                db.Participants.Remove(replaced);
            }

            db.Users.Add(new ExternalUserEntity
            {
                Id = replacementId, Provider = "github", ProviderUserId = "screen-guard-replacement",
                DisplayName = "screen-guard-replacement", CreatedAt = now, UpdatedAt = now
            });
            db.Participants.Add(new ParticipantEntity
            {
                ConversationId = dmId, UserId = replacementId, JoinedAt = now
            });
            return Task.CompletedTask;
        });
        var replacementSink = new RecordingSink("screen-guard-replacement-sink", replacementId);
        gateway.Register(replacementSink);
        using HttpClient replacement = AuthorizedClient(factory, "screen-guard-replacement");

        using HttpResponseMessage changedMember = await replacement.PostAsJsonAsync(
            $"/conversations/{dmId}/call/screen/start", new { reason = "screen" });
        using HttpResponseMessage changedPair = await caller.PostAsJsonAsync(
            $"/conversations/{dmId}/call/screen/start", new { reason = "screen" });
        Assert.Multiple(() =>
        {
            Assert.That(changedMember.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(changedPair.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(replacementSink.Events.Any(envelope => envelope.Type == ScreenStarted), Is.False);
        });

        Guid groupId = Guid.NewGuid();
        Guid groupCallerId = Guid.NewGuid();
        Guid groupOtherId = Guid.NewGuid();
        await factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = groupCallerId, Provider = "github", ProviderUserId = "screen-guard-group-caller",
                    DisplayName = "screen-guard-group-caller", CreatedAt = now, UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = groupOtherId, Provider = "github", ProviderUserId = "screen-guard-group-other",
                    DisplayName = "screen-guard-group-other", CreatedAt = now, UpdatedAt = now
                });
            var group = new ConversationEntity { Id = groupId, Kind = "group", CreatedAt = now };
            group.Participants.Add(new ParticipantEntity { UserId = groupCallerId, JoinedAt = now });
            group.Participants.Add(new ParticipantEntity { UserId = groupOtherId, JoinedAt = now });
            db.Conversations.Add(group);
            return Task.CompletedTask;
        });
        var groupSink = new RecordingSink("screen-guard-group-sink", groupOtherId);
        gateway.Register(groupSink);
        using HttpClient groupCaller = AuthorizedClient(factory, "screen-guard-group-caller");

        using HttpResponseMessage groupResponse = await groupCaller.PostAsJsonAsync(
            $"/conversations/{groupId}/call/screen/start", new { reason = "screen" });
        Assert.That(groupResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(
            await groupResponse.Content.ReadAsStringAsync(),
            Does.Contain("call_invalid_conversation"));
        Assert.That(groupSink.Events.Any(envelope => envelope.Type == ScreenStarted), Is.False);
    }

    [Test]
    public async Task Call_generation_rejects_mismatched_ice_without_publish()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, "call-generation-caller", "call-generation-callee");
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        var callerSink = new RecordingSink("call-generation-caller-sink", callerId);
        var calleeSink = new RecordingSink("call-generation-callee-sink", calleeId);
        gateway.Register(callerSink);
        gateway.Register(calleeSink);
        using HttpClient caller = AuthorizedClient(factory, "call-generation-caller");
        using HttpClient callee = AuthorizedClient(factory, "call-generation-callee");

        using HttpResponseMessage ring = await caller.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/ring", new { reason = "start" });
        Assert.That(ring.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Guid liveCallId = CallIdFromResponse(await ring.Content.ReadAsStringAsync());
        Assert.That(liveCallId, Is.Not.EqualTo(Guid.Empty));

        foreach ((HttpClient client, string action, object body) in new (HttpClient, string, object)[]
                 {
                     (caller, "offer", new { sdp = "offer-sdp" }),
                     (callee, "answer", new { sdp = "answer-sdp" })
                 })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{action}", body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), action);
        }

        int callerEventCount = callerSink.Events.Count;
        int calleeEventCount = calleeSink.Events.Count;

        Guid staleCallId = Guid.NewGuid();
        while (staleCallId == liveCallId)
        {
            staleCallId = Guid.NewGuid();
        }

        using HttpResponseMessage stale = await caller.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/ice",
            new { candidate = "stale-candidate", callId = staleCallId });

        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(
            await stale.Content.ReadAsStringAsync(),
            Does.Contain("call_stale_generation"));
        Assert.Multiple(() =>
        {
            Assert.That(callerSink.Events, Has.Count.EqualTo(callerEventCount));
            Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeEventCount));
        });

        using HttpResponseMessage legacy = await caller.PostAsJsonAsync(
            $"/conversations/{conversationId}/call/ice", new { candidate = "legacy-candidate" });

        Assert.That(legacy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeEventCount + 1));
            Assert.That(callerSink.Events, Has.Count.EqualTo(callerEventCount));
            Assert.That(calleeSink.Events[^1].Type, Is.EqualTo(GatewayEventType.CallIce));
        });
    }

    [TestCase("offer")]
    [TestCase("answer")]
    [TestCase("ice")]
    [TestCase("hangup")]
    [TestCase("screen/start")]
    [TestCase("screen/stop")]
    public async Task Call_generation_rejects_stale_action_after_replacement(string action)
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, "call-replacement-caller", "call-replacement-callee");
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        var callerSink = new RecordingSink("call-replacement-caller-sink", callerId);
        var calleeSink = new RecordingSink("call-replacement-callee-sink", calleeId);
        gateway.Register(callerSink);
        gateway.Register(calleeSink);
        using HttpClient caller = AuthorizedClient(factory, "call-replacement-caller");
        using HttpClient callee = AuthorizedClient(factory, "call-replacement-callee");

        async Task<(HttpStatusCode Status, string Body)> PostAsync(
            HttpClient client, string path, object body)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{path}", body);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        (HttpStatusCode ringA, string ringABody) = await PostAsync(
            caller, "ring", new { reason = "start" });
        Assert.That(ringA, Is.EqualTo(HttpStatusCode.OK));
        Guid generationA = CallIdFromResponse(ringABody);
        Assert.That(generationA, Is.Not.EqualTo(Guid.Empty));
        Assert.That(CallIdFromEvent(calleeSink.Events[^1]), Is.EqualTo(generationA));

        Assert.That(
            (await PostAsync(caller, "offer", new { sdp = "a-offer" })).Status,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            (await PostAsync(callee, "answer", new { sdp = "a-answer" })).Status,
            Is.EqualTo(HttpStatusCode.OK));

        int calleeBeforeHangup = calleeSink.Events.Count;
        (HttpStatusCode hangupA, string hangupABody) = await PostAsync(
            caller, "hangup", new { reason = "end-a" });
        Assert.That(hangupA, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallIdFromResponse(hangupABody), Is.EqualTo(generationA));
        Assert.Multiple(() =>
        {
            Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeBeforeHangup + 1));
            Assert.That(calleeSink.Events[^1].Type, Is.EqualTo(GatewayEventType.CallHangup));
            Assert.That(CallIdFromEvent(calleeSink.Events[^1]), Is.EqualTo(generationA));
        });

        (HttpStatusCode ringB, string ringBBody) = await PostAsync(
            caller, "ring", new { reason = "start" });
        Assert.That(ringB, Is.EqualTo(HttpStatusCode.OK));
        Guid generationB = CallIdFromResponse(ringBBody);
        Assert.That(generationB, Is.Not.EqualTo(generationA));
        Assert.That(CallIdFromEvent(calleeSink.Events[^1]), Is.EqualTo(generationB));

        switch (action)
        {
            case "answer":
                Assert.That(
                    (await PostAsync(caller, "offer", new { sdp = "b-offer" })).Status,
                    Is.EqualTo(HttpStatusCode.OK));
                break;
            case "screen/start":
            case "screen/stop":
                Assert.That(
                    (await PostAsync(caller, "offer", new { sdp = "b-offer" })).Status,
                    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(
                    (await PostAsync(callee, "answer", new { sdp = "b-answer" })).Status,
                    Is.EqualTo(HttpStatusCode.OK));
                if (action == "screen/stop")
                {
                    Assert.That(
                        (await PostAsync(caller, "screen/start", new { reason = "share-b" })).Status,
                        Is.EqualTo(HttpStatusCode.OK));
                }

                break;
        }

        string expectedEvent = action switch
        {
            "offer" => GatewayEventType.CallOffer,
            "answer" => GatewayEventType.CallAnswer,
            "ice" => GatewayEventType.CallIce,
            "hangup" => GatewayEventType.CallHangup,
            "screen/start" => GatewayEventType.CallScreenStarted,
            _ => GatewayEventType.CallScreenStopped
        };

        HttpClient actor = action == "answer" ? callee : caller;
        HttpClient peer = action == "answer" ? caller : callee;
        RecordingSink actorSink = action == "answer" ? calleeSink : callerSink;
        RecordingSink peerSink = action == "answer" ? callerSink : calleeSink;
        int actorSnapshot = actorSink.Events.Count;
        int peerSnapshot = peerSink.Events.Count;

        object staleBody = action switch
        {
            "offer" or "answer" => new { sdp = "stale-sdp", callId = generationA },
            "ice" => new { candidate = "stale-candidate", callId = generationA },
            "hangup" => new { reason = "stale-hangup", callId = generationA },
            _ => new { reason = "stale-screen", callId = generationA }
        };
        (HttpStatusCode staleStatus, string staleText) = await PostAsync(actor, action, staleBody);
        Assert.That(staleStatus, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(staleText, Does.Contain("call_stale_generation"));
        Assert.Multiple(() =>
        {
            Assert.That(actorSink.Events, Has.Count.EqualTo(actorSnapshot));
            Assert.That(peerSink.Events, Has.Count.EqualTo(peerSnapshot));
        });

        object validBody = action switch
        {
            "offer" or "answer" => new { sdp = "fresh-sdp", callId = generationB },
            "ice" => new { candidate = "fresh-candidate", callId = generationB },
            "hangup" => new { reason = "end-b", callId = generationB },
            _ => new { reason = "fresh-screen", callId = generationB }
        };
        (HttpStatusCode validStatus, string validText) = await PostAsync(actor, action, validBody);
        Assert.That(validStatus, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallIdFromResponse(validText), Is.EqualTo(generationB));
        Assert.Multiple(() =>
        {
            Assert.That(peerSink.Events, Has.Count.EqualTo(peerSnapshot + 1));
            Assert.That(peerSink.Events[^1].Type, Is.EqualTo(expectedEvent));
            Assert.That(CallIdFromEvent(peerSink.Events[^1]), Is.EqualTo(generationB));
            Assert.That(actorSink.Events, Has.Count.EqualTo(actorSnapshot));
        });

        if (action == "hangup")
        {
            (HttpStatusCode afterHangup, string afterHangupText) = await PostAsync(
                caller, "offer", new { sdp = "late-offer", callId = generationB });
            Assert.That(afterHangup, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(afterHangupText, Does.Contain("call_invalid_state"));
        }
    }

    [Test]
    public async Task Ring_rejects_supplied_generation_and_empty_ids_are_invalid()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid conversationId = Guid.NewGuid();
        (Guid callerId, Guid calleeId) = await SeedTwoMemberConversationAsync(
            factory, conversationId, "call-generation-input-caller", "call-generation-input-callee");
        GatewayHub gateway = factory.Services.GetRequiredService<GatewayHub>();
        var calleeSink = new RecordingSink("call-generation-input-callee-sink", calleeId);
        gateway.Register(calleeSink);
        using HttpClient caller = AuthorizedClient(factory, "call-generation-input-caller");
        int baselineEvents = calleeSink.Events.Count;

        async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object body)
        {
            using HttpResponseMessage response = await caller.PostAsJsonAsync(
                $"/conversations/{conversationId}/call/{path}", body);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        (HttpStatusCode suppliedRing, string suppliedRingText) = await PostAsync(
            "ring", new { reason = "start", callId = Guid.NewGuid() });
        Assert.That(suppliedRing, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(suppliedRingText, Does.Contain("invalid_request"));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(baselineEvents));

        (HttpStatusCode emptyRing, string emptyRingText) = await PostAsync(
            "ring", new { reason = "start", callId = Guid.Empty });
        Assert.That(emptyRing, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(emptyRingText, Does.Contain("invalid_request"));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(baselineEvents));

        (HttpStatusCode malformedRing, _) = await PostAsync(
            "ring", new { reason = "start", callId = "not-a-guid" });
        Assert.That(malformedRing, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(baselineEvents));

        (HttpStatusCode legacyRing, string legacyRingBody) = await PostAsync(
            "ring", new { reason = "start" });
        Assert.That(legacyRing, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(CallIdFromResponse(legacyRingBody), Is.Not.EqualTo(Guid.Empty));

        int calleeEvents = calleeSink.Events.Count;
        (HttpStatusCode emptyOffer, string emptyOfferText) = await PostAsync(
            "offer", new { sdp = "offer-sdp", callId = Guid.Empty });
        Assert.That(emptyOffer, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(emptyOfferText, Does.Contain("invalid_request"));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeEvents));

        (HttpStatusCode malformedOffer, _) = await PostAsync(
            "offer", new { sdp = "offer-sdp", callId = 12345 });
        Assert.That(malformedOffer, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeEvents));

        (HttpStatusCode legacyOffer, _) = await PostAsync(
            "offer", new { sdp = "offer-sdp" });
        Assert.That(legacyOffer, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(calleeSink.Events, Has.Count.EqualTo(calleeEvents + 1));
    }

    private static Guid CallIdFromResponse(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        return document.RootElement.GetProperty("callId").GetGuid();
    }

    private static Guid CallIdFromEvent(GatewayEnvelope envelope)
    {
        using JsonDocument document = JsonDocument.Parse(GatewayJson.Serialize(envelope));
        return document.RootElement.GetProperty("d").GetProperty("callId").GetGuid();
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
