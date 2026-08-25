using System.Collections.Immutable;

namespace Aerochat.Server.Gateway;

public sealed record GatewayEventRecord
{
    private readonly GatewayEnvelope _envelope;

    public GatewayEventRecord(
        long sequence,
        string eventId,
        string type,
        object data,
        IEnumerable<Guid> audience)
        : this(sequence, eventId, type, GatewayJson.Seal(GatewayEnvelope.Replayable(type, eventId, data), GatewayJson.DefaultMaxFrameBytes), audience)
    {
    }

    internal GatewayEventRecord(
        long sequence,
        string eventId,
        string type,
        GatewayEnvelope envelope,
        IEnumerable<Guid> audience)
    {
        Sequence = sequence;
        EventId = eventId;
        Type = type;
        _envelope = envelope;
        Data = envelope.Data;
        Audience = audience.ToImmutableHashSet();
    }

    public long Sequence { get; }
    public string EventId { get; }
    public string Type { get; }
    public object Data { get; }
    public IReadOnlySet<Guid> Audience { get; }
    public GatewayEnvelope Envelope => _envelope;

    public bool IsFor(Guid userId) => Audience.Contains(userId);
}
