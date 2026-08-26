using System.Text.Json;
using System.Globalization;

namespace Aerochat.Connectivity;

public sealed record GatewayFrame(
    string Type,
    string? EventId,
    JsonElement Data);

public static class GatewayProtocol
{
    private static readonly string[] CallTypes =
    ["call.ring", "call.offer", "call.answer", "call.ice", "call.hangup"];

    public static bool TryParseFrame(string json, out GatewayFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(type.GetString())
                || !root.TryGetProperty("eventId", out JsonElement eventId)
                || (eventId.ValueKind != JsonValueKind.String
                    && eventId.ValueKind != JsonValueKind.Null)
                || !root.TryGetProperty("d", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            frame = new GatewayFrame(
                type.GetString()!,
                eventId.ValueKind == JsonValueKind.Null ? null : eventId.GetString(),
                data.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseCallSignal(GatewayFrame frame, out CallSignalEventArgs? call)
    {
        call = null;
        if (!CallTypes.Contains(frame.Type, StringComparer.Ordinal)
            || !TryGetString(frame.Data, "conversationId", out string? conversationId))
            return false;

        call = new CallSignalEventArgs(
            frame.Type,
            conversationId!,
            TryGetString(frame.Data, "sdp", out string? sdp) ? sdp : null,
            TryGetString(frame.Data, "candidate", out string? candidate) ? candidate : null,
            TryGetString(frame.Data, "reason", out string? reason) ? reason : null);
        return true;
    }

    public static bool TryParseMessage(
        JsonElement data,
        out MessageCreatedEventArgs? message)
    {
        message = null;
        if (!TryGetString(data, "conversationId", out string? conversationId)
            || !data.TryGetProperty("message", out JsonElement payload)
            || payload.ValueKind != JsonValueKind.Object
            || !TryGetString(payload, "id", out string? messageId)
            || !TryGetString(payload, "authorId", out string? authorId)
            || !TryGetString(payload, "body", out string? body)
            || !TryGetString(payload, "createdAt", out string? createdAtText)
            || !DateTimeOffset.TryParse(
                createdAtText!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAt))
        {
            return false;
        }

        string kind = TryGetString(payload, "kind", out string? parsedKind)
            ? parsedKind!
            : "message";
        string? refPayloadJson = payload.TryGetProperty("refPayload", out JsonElement refPayload)
            && refPayload.ValueKind != JsonValueKind.Null
            ? refPayload.GetRawText()
            : null;
        message = new MessageCreatedEventArgs(
            conversationId!, messageId!, authorId!, body!, createdAt, kind, refPayloadJson);
        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }
}
