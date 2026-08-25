using System.Text.Json;
using Aerochat.Server.Gateway;

namespace Aerochat.Server.Tests;

public sealed class GatewayProtocolTests
{
    [Test]
    public void Replayable_message_envelope_uses_stable_lower_camel_wire_shape()
    {
        Guid conversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid messageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid authorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var message = new GatewayMessageData(
            messageId,
            conversationId,
            authorId,
            "hello",
            "message",
            null,
            DateTimeOffset.Parse("2026-08-25T12:00:00+00:00"),
            null,
            null);

        string json = GatewayJson.Serialize(
            GatewayEnvelope.Replayable(
                GatewayEventType.MessageCreated,
                "hub:42",
                new MessageCreatedData(conversationId, message)));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("t").GetString(), Is.EqualTo("message.created"));
            Assert.That(root.GetProperty("eventId").GetString(), Is.EqualTo("hub:42"));
            Assert.That(root.GetProperty("d").GetProperty("conversationId").GetGuid(), Is.EqualTo(conversationId));
            Assert.That(root.GetProperty("d").GetProperty("message").GetProperty("id").GetGuid(), Is.EqualTo(messageId));
            Assert.That(root.GetProperty("d").GetProperty("message").GetProperty("authorId").GetGuid(), Is.EqualTo(authorId));
            Assert.That(root.GetProperty("d").GetProperty("message").GetProperty("refPayload").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("d").GetProperty("message").GetProperty("createdAt").GetString(), Is.EqualTo("2026-08-25T12:00:00+00:00"));
        });
    }

    [Test]
    public void Control_envelope_has_null_event_id_and_preserves_optional_fields()
    {
        var data = new GatewayReadyData(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "hub",
            "hub:42",
            null);

        string json = GatewayJson.Serialize(GatewayEnvelope.Control(GatewayEventType.Ready, data));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("t").GetString(), Is.EqualTo("gateway.ready"));
            Assert.That(root.GetProperty("eventId").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("d").GetProperty("userId").GetGuid(), Is.EqualTo(data.UserId));
            Assert.That(root.GetProperty("d").GetProperty("currentEventId").GetString(), Is.EqualTo("hub:42"));
            Assert.That(root.GetProperty("d").GetProperty("replayedFrom").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [TestCase(GatewayEventType.MessageCreated)]
    [TestCase(GatewayEventType.PresenceUpdated)]
    [TestCase(GatewayEventType.TypingStarted)]
    public void Event_type_constants_match_wire_contract(string eventType)
    {
        Assert.That(eventType, Does.Match("^(message\\.created|presence\\.updated|typing\\.started)$"));
    }

    [Test]
    public void Resync_control_envelope_has_reason_and_oldest_cursor()
    {
        string json = GatewayJson.Serialize(
            GatewayEnvelope.Control(
                GatewayEventType.ResyncRequired,
                new GatewayResyncRequiredData("cursor_too_old", "hub:10")));
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("eventId").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(document.RootElement.GetProperty("d").GetProperty("reason").GetString(), Is.EqualTo("cursor_too_old"));
            Assert.That(document.RootElement.GetProperty("d").GetProperty("oldestEventId").GetString(), Is.EqualTo("hub:10"));
        });
    }
}
