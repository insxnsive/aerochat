using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aerochat.Server.Rest;

public sealed record ConversationDto(
    Guid Id,
    string Kind,
    string? Title,
    DateTimeOffset CreatedAt);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid AuthorId,
    string Body,
    string Kind,
    string? RefPayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt);

public sealed record MessagePageDto(
    IReadOnlyList<MessageDto> Items,
    string? NextBefore);

public sealed record SendMessageRequest(string? Body, string? Kind, string? RefPayloadJson = null);

public sealed record CallSignalRequest(string? Sdp, string? Candidate, string? Reason);

public sealed record ErrorDto(string Error);

internal sealed record MessageCursor(Guid ConversationId, DateTimeOffset CreatedAt, Guid Id);

internal static class MessageCursorCodec
{
    private static readonly HashSet<string> RequiredProperties =
    [
        "conversationId",
        "createdAt",
        "id"
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(Guid conversationId, DateTimeOffset createdAt, Guid id)
    {
        string json = JsonSerializer.Serialize(new
        {
            conversationId,
            createdAt = createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            id
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string value, out MessageCursor cursor)
    {
        cursor = null!;
        try
        {
            if (value.Length == 0
                || value.Length % 4 == 1
                || value.Any(character =>
                    !((character is >= 'A' and <= 'Z')
                        || (character is >= 'a' and <= 'z')
                        || (character is >= '0' and <= '9')
                        || character is '-' or '_')))
            {
                return false;
            }

            string padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            using JsonDocument document = JsonDocument.Parse(
                StrictUtf8.GetString(Convert.FromBase64String(padded)));
            JsonElement root = document.RootElement;
            JsonProperty[] properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (properties.Length != RequiredProperties.Count
                || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != RequiredProperties.Count
                || properties.Any(property => !RequiredProperties.Contains(property.Name))
                || !root.TryGetProperty("conversationId", out JsonElement conversationElement)
                || !root.TryGetProperty("createdAt", out JsonElement createdAtElement)
                || !root.TryGetProperty("id", out JsonElement idElement)
                || conversationElement.ValueKind != JsonValueKind.String
                || createdAtElement.ValueKind != JsonValueKind.String
                || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(conversationElement.GetString(), out Guid conversationId)
                || !Guid.TryParse(idElement.GetString(), out Guid id)
                || !DateTimeOffset.TryParse(
                    createdAtElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset createdAt)
                || createdAt.Offset != TimeSpan.Zero
                || !string.Equals(Encode(conversationId, createdAt, id), value, StringComparison.Ordinal))
            {
                return false;
            }

            cursor = new MessageCursor(conversationId, createdAt, id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
