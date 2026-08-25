using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Aerochat.Server.Tests;

/// <summary>
/// End-to-end gateway tests over genuine Kestrel WebSockets on a numeric loopback
/// endpoint (127.0.0.1:0). Covers the Task 8 integration contract: authenticated
/// upgrade, push-only policy enforcement with hub cleanup, REST persistence fanout,
/// reconnect replay, and resync/restart close codes.
/// </summary>
public sealed class GatewayWebSocketIntegrationTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Real_loopback_request_without_valid_token_returns_401_bearer_challenge()
    {
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync();
        using HttpClient client = new();

        using HttpResponseMessage response = await client.GetAsync($"{fixture.BaseUrl}/ws?token=not-a-session");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
        });
    }

    [Test]
    public async Task Authenticated_real_socket_gets_ready_and_inbound_text_is_closed_as_policy_violation()
    {
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync();
        (Guid userId, string token) = await CreateUserAsync(fixture, "ws-policy");

        GatewayHub hub = fixture.Services.GetRequiredService<GatewayHub>();
        using WebSocket socket = await ConnectAsync(fixture.BaseUrl, token);
        string ready = await ReceiveTextFrameAsync(socket);

        using (JsonDocument document = JsonDocument.Parse(ready))
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.ready"));
                Assert.That(document.RootElement.GetProperty("eventId").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(document.RootElement.GetProperty("d").GetProperty("userId").GetString(),
                    Is.EqualTo(userId.ToString()));
            });
        }

        Assert.That(hub.ActiveConnectionCount, Is.EqualTo(1));

        byte[] outbound = Encoding.UTF8.GetBytes("inbound text is forbidden");
        await socket.SendAsync(outbound, WebSocketMessageType.Text, true, CancellationToken.None);

        WebSocketReceiveResult result = await ReceiveRawAsync(socket);
        Assert.Multiple(() =>
        {
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
        });

        await WaitForHubDrainAsync(hub, expectedActive: 0);
    }

    [Test]
    public async Task Rest_message_send_persists_once_and_fans_out_one_identical_event_to_each_participant_only()
    {
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync();
        (Guid alice, string aliceToken) = await CreateUserAsync(fixture, "ws-alice");
        (Guid bob, string bobToken) = await CreateUserAsync(fixture, "ws-bob");
        (Guid _, string outsiderToken) = await CreateUserAsync(fixture, "ws-outsider");
        Guid conversation = await CreateDirectConversationAsync(fixture, alice, bob);

        using WebSocket aliceSocket = await ConnectAsync(fixture.BaseUrl, aliceToken);
        using WebSocket bobSocket = await ConnectAsync(fixture.BaseUrl, bobToken);
        using WebSocket outsiderSocket = await ConnectAsync(fixture.BaseUrl, outsiderToken);
        await ReceiveTextFrameAsync(aliceSocket);
        await ReceiveTextFrameAsync(bobSocket);
        await ReceiveTextFrameAsync(outsiderSocket);

        using HttpClient client = new();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
        using HttpResponseMessage response = await client.PostAsync(
            $"{fixture.BaseUrl}/conversations/{conversation}/messages",
            new StringContent("{\"body\":\"hello gateway\",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        string aliceFrame = await ReceiveTextFrameAsync(aliceSocket);
        string bobFrame = await ReceiveTextFrameAsync(bobSocket);
        Guid messageId = (await response.Content.ReadFromJsonAsync<RestMessage>())!.Id;

        using (JsonDocument aliceDocument = JsonDocument.Parse(aliceFrame))
        using (JsonDocument bobDocument = JsonDocument.Parse(bobFrame))
        {
            JsonElement aliceMessage = aliceDocument.RootElement.GetProperty("d").GetProperty("message");
            Assert.Multiple(() =>
            {
                Assert.That(aliceDocument.RootElement.GetProperty("t").GetString(), Is.EqualTo("message.created"));
                Assert.That(bobDocument.RootElement.GetProperty("t").GetString(), Is.EqualTo("message.created"));
                Assert.That(aliceDocument.RootElement.GetProperty("eventId").GetString(),
                    Is.EqualTo(bobDocument.RootElement.GetProperty("eventId").GetString()));
                Assert.That(aliceMessage.GetProperty("id").GetString(), Is.EqualTo(messageId.ToString()));
                Assert.That(aliceMessage.GetProperty("body").GetString(), Is.EqualTo("hello gateway"));
                Assert.That(aliceMessage.GetProperty("authorId").GetString(), Is.EqualTo(alice.ToString()));
                Assert.That(aliceDocument.RootElement.GetProperty("d").GetProperty("conversationId").GetString(),
                    Is.EqualTo(conversation.ToString()));
            });
        }

        await using AsyncServiceScope scope = fixture.Services.CreateAsyncScope();
        ChatDb db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        int persistedMessages = await db.Messages
            .Where(message => message.ConversationId == conversation)
            .CountAsync();
        Assert.That(persistedMessages, Is.EqualTo(1));

        // The non-participant must stay silent; prove it with a bounded window instead
        // of an unbounded wait.
        var buffer = new byte[16 * 1024];
        bool outsiderReceived = false;
        using (var silence = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try
            {
                await outsiderSocket.ReceiveAsync(new ArraySegment<byte>(buffer), silence.Token);
                outsiderReceived = true;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.That(outsiderReceived, Is.False);
    }

    [Test]
    public async Task Reconnect_with_last_event_id_replays_only_later_eligible_events()
    {
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync();
        (Guid alice, string aliceToken) = await CreateUserAsync(fixture, "replay-alice");
        (Guid bob, string _) = await CreateUserAsync(fixture, "replay-bob");
        Guid conversation = await CreateDirectConversationAsync(fixture, alice, bob);

        using HttpClient client = new();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);

        using WebSocket firstSocket = await ConnectAsync(fixture.BaseUrl, aliceToken);
        await ReceiveTextFrameAsync(firstSocket);

        string firstBody = await PostMessageAsync(client, fixture.BaseUrl, conversation);
        string firstFrame = await ReceiveTextFrameAsync(firstSocket);
        string firstEventId = JsonDocument.Parse(firstFrame).RootElement.GetProperty("eventId").GetString()!;

        string secondBody = await PostMessageAsync(client, fixture.BaseUrl, conversation);
        string secondFrame = await ReceiveTextFrameAsync(firstSocket);
        string secondEventId = JsonDocument.Parse(secondFrame).RootElement.GetProperty("eventId").GetString()!;

        await CloseGracefullyAsync(firstSocket);

        using WebSocket reconnected = await ConnectAsync(fixture.BaseUrl, aliceToken, firstEventId);
        string ready = await ReceiveTextFrameAsync(reconnected);
        Assert.That(JsonDocument.Parse(ready).RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.ready"));

        string replayed = await ReceiveTextFrameAsync(reconnected);
        using (JsonDocument replayedDocument = JsonDocument.Parse(replayed))
        {
            Assert.Multiple(() =>
            {
                Assert.That(replayedDocument.RootElement.GetProperty("t").GetString(), Is.EqualTo("message.created"));
                Assert.That(replayedDocument.RootElement.GetProperty("eventId").GetString(), Is.EqualTo(secondEventId));
                Assert.That(replayedDocument.RootElement.GetProperty("d").GetProperty("message").GetProperty("body").GetString(),
                    Is.EqualTo(secondBody));
            });
        }

        Assert.That(replayed, Does.Not.Contain(firstBody));
    }

    [Test]
    public async Task Expired_cursor_receives_ready_and_resync_controls_then_close_1000()
    {
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync(
            instanceId: "resync", replayCapacity: 2);
        (Guid alice, string aliceToken) = await CreateUserAsync(fixture, "resync-alice");
        (Guid bob, string _) = await CreateUserAsync(fixture, "resync-bob");
        Guid conversation = await CreateDirectConversationAsync(fixture, alice, bob);

        using HttpClient client = new();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
        for (int i = 0; i < 3; i++)
        {
            await PostMessageAsync(client, fixture.BaseUrl, conversation);
        }

        // Three events with a two-slot ring leaves sequences {2, 3}; a cursor below the
        // oldest retained boundary can no longer be replayed and must trigger resync.
        using WebSocket socket = await ConnectAsync(fixture.BaseUrl, aliceToken, "resync:0");
        string ready = await ReceiveTextFrameAsync(socket);
        Assert.That(JsonDocument.Parse(ready).RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.ready"));

        string resync = await ReceiveTextFrameAsync(socket);
        using (JsonDocument resyncDocument = JsonDocument.Parse(resync))
        {
            Assert.Multiple(() =>
            {
                Assert.That(resyncDocument.RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.resync_required"));
                Assert.That(resyncDocument.RootElement.GetProperty("eventId").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(resyncDocument.RootElement.GetProperty("d").GetProperty("reason").GetString(),
                    Is.EqualTo("cursor_too_old"));
            });
        }

        WebSocketReceiveResult close = await ReceiveRawAsync(socket);
        Assert.Multiple(() =>
        {
            Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(close.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
        });
    }

    [Test]
    public async Task Cursor_from_previous_server_instance_receives_controls_then_close_1012()
    {
        // Production mints a fresh gateway instance id every boot; a cursor carrying a
        // previous boot's id can never be honored and must force a resync with 1012.
        using LoopbackServerFixture fixture = await LoopbackServerFixture.StartAsync(instanceId: "boot-b");
        (Guid _, string token) = await CreateUserAsync(fixture, "restart-user");

        using WebSocket socket = await ConnectAsync(fixture.BaseUrl, token, "boot-a:1");

        string ready = await ReceiveTextFrameAsync(socket);
        Assert.That(JsonDocument.Parse(ready).RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.ready"));

        string resync = await ReceiveTextFrameAsync(socket);
        using (JsonDocument resyncDocument = JsonDocument.Parse(resync))
        {
            Assert.Multiple(() =>
            {
                Assert.That(resyncDocument.RootElement.GetProperty("t").GetString(), Is.EqualTo("gateway.resync_required"));
                Assert.That(resyncDocument.RootElement.GetProperty("d").GetProperty("reason").GetString(),
                    Is.EqualTo("server_restarted"));
            });
        }

        WebSocketReceiveResult close = await ReceiveRawAsync(socket);
        Assert.Multiple(() =>
        {
            Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(close.CloseStatus, Is.EqualTo((WebSocketCloseStatus)1012));
        });
    }

    private static async Task<string> PostMessageAsync(HttpClient client, string baseUrl, Guid conversation)
    {
        string body = $"payload-{Guid.NewGuid():N}";
        using HttpResponseMessage response = await client.PostAsync(
            $"{baseUrl}/conversations/{conversation}/messages",
            new StringContent(FormattableString.Invariant($"{{\"body\":\"{body}\",\"kind\":\"message\"}}"), Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        return body;
    }

    private static async Task<(Guid UserId, string Token)> CreateUserAsync(
        LoopbackServerFixture fixture,
        string providerUserId)
    {
        Guid userId = Guid.NewGuid();
        await fixture.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = providerUserId,
                DisplayName = providerUserId,
                CreatedAt = SeedTime,
                UpdatedAt = SeedTime
            });
            return Task.CompletedTask;
        });

        string token = fixture.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", providerUserId, providerUserId));
        return (userId, token);
    }

    private static async Task<Guid> CreateDirectConversationAsync(
        LoopbackServerFixture fixture,
        params Guid[] userIds)
    {
        Guid conversationId = Guid.NewGuid();
        await fixture.SeedAsync(db =>
        {
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = SeedTime
            });
            foreach (Guid id in userIds)
            {
                db.Conversations.Local.Single(conversation => conversation.Id == conversationId)
                    .Participants.Add(new ParticipantEntity { UserId = id, JoinedAt = SeedTime });
            }

            return Task.CompletedTask;
        });
        return conversationId;
    }

    private static async Task<WebSocket> ConnectAsync(string baseUrl, string token, string? lastEventId = null)
    {
        var client = new ClientWebSocket();
        string wsBase = baseUrl.StartsWith("http://", StringComparison.Ordinal)
            ? string.Concat("ws://", baseUrl.AsSpan("http://".Length))
            : baseUrl;
        string query = $"token={Uri.EscapeDataString(token)}";
        if (lastEventId is not null)
        {
            query += $"&lastEventId={Uri.EscapeDataString(lastEventId)}";
        }

        try
        {
            await client.ConnectAsync(new Uri($"{wsBase}/ws?{query}"), CancellationToken.None);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    private static async Task<WebSocketReceiveResult> ReceiveRawAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        return await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
    }

    private static async Task<string> ReceiveTextFrameAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Assert.Fail($"Unexpected close frame: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return text.ToString();
    }

    private static async Task CloseGracefullyAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        WebSocketReceiveResult result = await ReceiveRawAsync(socket);
        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
    }

    private static async Task WaitForHubDrainAsync(GatewayHub hub, int expectedActive)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (hub.ActiveConnectionCount == expectedActive)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.That(hub.ActiveConnectionCount, Is.EqualTo(expectedActive), "Gateway hub did not drain in time.");
    }

    private sealed record RestMessage(Guid Id, Guid ConversationId, Guid AuthorId);
}
