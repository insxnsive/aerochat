using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aerochat.Server.Gateway;

public static class GatewayEventType
{
    public const string MessageCreated = "message.created";
    public const string PresenceUpdated = "presence.updated";
    public const string TypingStarted = "typing.started";
    public const string CallRing = "call.ring";
    public const string CallOffer = "call.offer";
    public const string CallAnswer = "call.answer";
    public const string CallIce = "call.ice";
    public const string CallHangup = "call.hangup";
    public const string Ready = "gateway.ready";
    public const string ResyncRequired = "gateway.resync_required";
}

public sealed record GatewayEnvelope(
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("eventId")] string? EventId,
    [property: JsonPropertyName("d")] object Data)
{
    internal string? SerializedFrame { get; init; }

    public static GatewayEnvelope Replayable(string type, string eventId, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentNullException.ThrowIfNull(data);
        return new GatewayEnvelope(type, eventId, data);
    }

    public static GatewayEnvelope Control(string type, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(data);
        return new GatewayEnvelope(type, null, data);
    }
}

public sealed class GatewaySerializationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public static class GatewayJson
{
    public const int DefaultMaxFrameBytes = 256 * 1024;

    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static string Serialize(GatewayEnvelope envelope) => Serialize(envelope, DefaultMaxFrameBytes);

    public static string Serialize(GatewayEnvelope envelope, int maxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Seal(envelope, maxFrameBytes).SerializedFrame!;
    }

    internal static GatewayEnvelope Seal(GatewayEnvelope envelope, int maxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        }

        string dataJson;
        try
        {
            ValidateValue(envelope.Data, new HashSet<object>(ReferenceEqualityComparer.Instance));
            dataJson = JsonSerializer.Serialize(envelope.Data, SerializerOptions);
        }
        catch (GatewaySerializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new GatewaySerializationException("Gateway payload could not be serialized safely.", exception);
        }

        string frame;
        JsonElement immutableData;
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            immutableData = document.RootElement.Clone();
            frame = JsonSerializer.Serialize(
                new GatewayEnvelope(envelope.Type, envelope.EventId, immutableData),
                SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new GatewaySerializationException("Gateway frame could not be serialized safely.", exception);
        }

        if (System.Text.Encoding.UTF8.GetByteCount(frame) > maxFrameBytes)
        {
            throw new GatewaySerializationException("Gateway frame exceeds the configured maximum size.");
        }

        return envelope with
        {
            Data = immutableData,
            SerializedFrame = frame
        };
    }

    private static void ValidateValue(object? value, HashSet<object> visited)
    {
        if (value is null or string or bool or char or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal or Guid or DateTime or DateTimeOffset)
        {
            return;
        }

        if (value is JsonElement)
        {
            return;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            throw new GatewaySerializationException("Cyclic gateway payloads are not supported.");
        }

        switch (value)
        {
            case MessageCreatedData messageCreated:
                ValidateValue(messageCreated.ConversationId, visited);
                ValidateValue(messageCreated.Message, visited);
                return;
            case GatewayMessageData message:
                ValidateValue(message.Id, visited);
                ValidateValue(message.ConversationId, visited);
                ValidateValue(message.AuthorId, visited);
                ValidateValue(message.Body, visited);
                ValidateValue(message.Kind, visited);
                ValidateValue(message.RefPayload, visited);
                ValidateValue(message.CreatedAt, visited);
                ValidateValue(message.EditedAt, visited);
                ValidateValue(message.DeletedAt, visited);
                return;
            case PresenceUpdatedData presence:
                ValidateValue(presence.UserId, visited);
                ValidateValue(presence.Status, visited);
                return;
            case TypingStartedData typing:
                ValidateValue(typing.ConversationId, visited);
                ValidateValue(typing.UserId, visited);
                return;
            case CallSignalData call:
                ValidateValue(call.ConversationId, visited);
                ValidateValue(call.Sdp, visited);
                ValidateValue(call.Candidate, visited);
                ValidateValue(call.Reason, visited);
                return;
            case GatewayReadyData ready:
                ValidateValue(ready.UserId, visited);
                ValidateValue(ready.InstanceId, visited);
                ValidateValue(ready.CurrentEventId, visited);
                ValidateValue(ready.ReplayedFrom, visited);
                return;
            case GatewayResyncRequiredData resync:
                ValidateValue(resync.Reason, visited);
                ValidateValue(resync.OldestEventId, visited);
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        throw new GatewaySerializationException("Gateway dictionaries must use string keys.");
                    }

                    ValidateValue(key, visited);
                    ValidateValue(entry.Value, visited);
                }

                return;
            case IEnumerable sequence:
                foreach (object? item in sequence)
                {
                    ValidateValue(item, visited);
                }

                return;
            default:
                throw new GatewaySerializationException($"Gateway payload type '{value.GetType().FullName}' is not allowed.");
        }
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record GatewayMessageData(
    Guid Id,
    Guid ConversationId,
    Guid AuthorId,
    string Body,
    string Kind,
    object? RefPayload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt);

public sealed record MessageCreatedData(Guid ConversationId, GatewayMessageData Message);

public sealed record PresenceUpdatedData(Guid UserId, string Status);

public sealed record TypingStartedData(Guid ConversationId, Guid UserId);

public sealed record CallSignalData(
    Guid ConversationId,
    string? Sdp,
    string? Candidate,
    string? Reason);

public sealed record GatewayReadyData(
    Guid UserId,
    string InstanceId,
    string? CurrentEventId,
    string? ReplayedFrom);

public sealed record GatewayResyncRequiredData(string Reason, string? OldestEventId);
