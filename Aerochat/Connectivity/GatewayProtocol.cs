using System.Text.Json;

namespace Aerochat.Connectivity;

public sealed record GatewayFrame(
    string Type,
    string? EventId,
    JsonElement Data);

public static class GatewayProtocol
{
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
}
