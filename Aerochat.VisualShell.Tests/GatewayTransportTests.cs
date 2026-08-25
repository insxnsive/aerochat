using Aerochat.Connectivity;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class GatewayTransportTests
{
    [Test]
    public void Presentation_adapter_maps_messages_and_presence_onto_existing_objects()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        ConversationPresentation conversation = state.Conversations.Single(item => item.Id == 2001);
        int originalMessageCount = conversation.Messages.Count;

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            "2001",
            "10000000-0000-0000-0000-000000000001",
            "1001",
            "A gateway message",
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));
        adapter.ApplyPresenceUpdated(new PresenceUpdatedEventArgs("1001", "Busy"));

        Assert.Multiple(() =>
        {
            Assert.That(conversation.Messages, Has.Count.EqualTo(originalMessageCount + 1));
            Assert.That(conversation.Messages[^1].Body, Is.EqualTo("A gateway message"));
            Assert.That(conversation.Messages[^1].Author.Id, Is.EqualTo(1001));
            Assert.That(state.Conversations.SelectMany(item => item.Participants)
                .Single(person => person.Id == 1001).Presence.Status, Is.EqualTo(PresenceStatus.Busy));
        });
    }

    [Test]
    public void Presentation_adapter_ignores_unresolvable_or_invalid_events()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        ConversationPresentation conversation = state.Conversations.Single(item => item.Id == 2001);
        int originalMessageCount = conversation.Messages.Count;

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            "not-a-conversation",
            "not-a-message",
            "not-a-user",
            "ignored",
            DateTimeOffset.UtcNow));
        adapter.ApplyPresenceUpdated(new PresenceUpdatedEventArgs("1001", "not-a-status"));

        Assert.That(conversation.Messages, Has.Count.EqualTo(originalMessageCount));
    }

    [Test]
    public void Gateway_frame_parser_reads_wire_shape_without_event_side_effects()
    {
        bool parsed = GatewayProtocol.TryParseFrame(
            "{\"t\":\"message.created\",\"eventId\":\"instance:7\",\"d\":{\"value\":42}}",
            out GatewayFrame? frame);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.Type, Is.EqualTo("message.created"));
            Assert.That(frame.EventId, Is.EqualTo("instance:7"));
            Assert.That(frame.Data.GetProperty("value").GetInt32(), Is.EqualTo(42));
        });
    }

    [TestCase("")]
    [TestCase("{}")]
    [TestCase("{\"t\":\"message.created\",\"eventId\":null,\"d\":null}")]
    [TestCase("{\"t\":\"message.created\",\"eventId\":5,\"d\":{}}")]
    public void Gateway_frame_parser_rejects_invalid_wire_shapes(string json)
    {
        Assert.That(GatewayProtocol.TryParseFrame(json, out _), Is.False);
    }

    [Test]
    public void Gateway_uri_uses_websocket_scheme_and_escapes_resume_values()
    {
        Uri uri = GatewayClient.BuildGatewayUri(
            new Uri("https://server.example/"),
            "token with/slash",
            "instance:7/next");

        Assert.That(
            uri.AbsoluteUri,
            Is.EqualTo("wss://server.example/ws?token=token%20with%2Fslash&lastEventId=instance%3A7%2Fnext"));
    }

    [Test]
    public async Task Gateway_client_send_operations_report_push_only_deviation()
    {
        await using var client = new GatewayClient();

        NotSupportedException send = Assert.ThrowsAsync<NotSupportedException>(
            async () => await client.SendAsync("conversation", "hello"))!;
        NotSupportedException typing = Assert.ThrowsAsync<NotSupportedException>(
            async () => await client.SetTypingAsync("conversation"))!;

        Assert.Multiple(() =>
        {
            Assert.That(send.Message, Does.Contain("push-only"));
            Assert.That(typing.Message, Does.Contain("push-only"));
        });
    }

    [TestCase(0, 1000)]
    [TestCase(1, 2000)]
    [TestCase(2, 4000)]
    [TestCase(3, 8000)]
    [TestCase(4, 16000)]
    [TestCase(5, 30000)]
    [TestCase(6, 30000)]
    [TestCase(30, 30000)]
    [TestCase(int.MaxValue, 30000)]
    public void Exponential_backoff_follows_curve_and_cap(int attempt, int expectedMilliseconds)
    {
        Assert.That(
            ExponentialBackoff.GetDelay(attempt),
            Is.EqualTo(TimeSpan.FromMilliseconds(expectedMilliseconds)));
    }

    [Test]
    public void Exponential_backoff_adds_injected_deterministic_jitter()
    {
        TimeSpan delay = ExponentialBackoff.GetDelay(
            2,
            attempt => TimeSpan.FromMilliseconds(attempt * 25));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMilliseconds(4050)));
    }

    [Test]
    public void Exponential_backoff_rejects_negative_attempts()
    {
        Assert.That(
            () => ExponentialBackoff.GetDelay(-1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
