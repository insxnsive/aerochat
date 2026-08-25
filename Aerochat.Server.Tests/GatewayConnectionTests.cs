using Aerochat.Server.Gateway;

namespace Aerochat.Server.Tests;

public sealed class GatewayConnectionTests
{
    [Test]
    public void Connection_enforces_configured_queue_capacity()
    {
        Guid userId = Guid.NewGuid();
        var options = new GatewayOptions { QueueCapacity = 1, MaxFrameBytes = 4096 };
        using var connection = new GatewayConnection("connection", userId, options);
        GatewayEnvelope first = GatewayEnvelope.Control(GatewayEventType.Ready, new GatewayReadyData(userId, "hub", null, null));
        GatewayEnvelope second = GatewayEnvelope.Control(GatewayEventType.ResyncRequired, new GatewayResyncRequiredData("reason", null));

        Assert.Multiple(() =>
        {
            Assert.That(connection.TryEnqueue(first), Is.True);
            Assert.That(connection.TryEnqueue(second), Is.False);
            Assert.That(connection.QueueCount, Is.EqualTo(1));
            Assert.That(connection.EnqueueFailureReason, Is.EqualTo(GatewayAbortReason.Overloaded));
        });
    }

    [Test]
    public void Connection_rejects_frames_over_configured_maximum()
    {
        Guid userId = Guid.NewGuid();
        var options = new GatewayOptions { QueueCapacity = 2, MaxFrameBytes = 32 };
        using var connection = new GatewayConnection("connection", userId, options);
        GatewayEnvelope oversized = GatewayEnvelope.Control(
            GatewayEventType.Ready,
            new GatewayReadyData(userId, "hub", null, new string('x', 100)));

        Assert.That(connection.TryEnqueue(oversized), Is.False);
        Assert.That(connection.EnqueueFailureReason, Is.EqualTo(GatewayAbortReason.FrameTooLarge));
        Assert.That(connection.QueueCount, Is.Zero);
    }

    [Test]
    public void Connection_retains_immutable_serialized_frames_until_dequeue()
    {
        Guid userId = Guid.NewGuid();
        using var connection = new GatewayConnection("connection", userId, new GatewayOptions());
        GatewayEnvelope envelope = GatewayEnvelope.Control(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"));

        Assert.That(connection.TryEnqueue(envelope), Is.True);
        Assert.That(connection.TryDequeue(out string? frame), Is.True);
        Assert.That(frame, Does.Contain("presence.updated"));
        Assert.That(connection.QueueCount, Is.Zero);
    }

    [Test]
    public void Connection_revalidates_a_presealed_frame_against_its_own_smaller_limit()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub", MaxFrameBytes = 4096 });
        GatewayEventRecord record = hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, new string('x', 100)),
            [userId]);
        using var connection = new GatewayConnection(
            "connection",
            userId,
            new GatewayOptions { QueueCapacity = 1, MaxFrameBytes = 64 });

        Assert.Multiple(() =>
        {
            Assert.That(connection.TryEnqueue(record.Envelope), Is.False);
            Assert.That(connection.EnqueueFailureReason, Is.EqualTo(GatewayAbortReason.FrameTooLarge));
            Assert.That(connection.QueueCount, Is.Zero);
        });
    }

    [Test]
    public void Serializing_a_with_copy_revalidates_current_payload_instead_of_stale_cached_frame()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub", MaxFrameBytes = 4096 });
        GatewayEventRecord record = hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "original"),
            [userId]);
        GatewayEnvelope changed = record.Envelope with
        {
            Data = new PresenceUpdatedData(userId, "changed")
        };

        string json = GatewayJson.Serialize(changed);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("changed"));
            Assert.That(json, Does.Not.Contain("original"));
        });
    }
}
