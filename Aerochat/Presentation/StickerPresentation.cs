using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aerochat.Presentation;

public sealed record StickerPresentation(string ResourceName, string ResourceUri, string Label)
{
    public const string PackPathPrefix = "/sticker-packs/wlm/";

    public string ServerPackPath => $"{PackPathPrefix}{Uri.EscapeDataString(ResourceName)}";

    public string ContentType => ResourceName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
        ? "image/gif"
        : "image/png";

    public string RefPayloadJson => JsonSerializer.Serialize(
        new StickerAttachmentPayload(ResourceName, ServerPackPath, ContentType));
}

public sealed record StickerAttachmentPayload(
    [property: JsonPropertyName("sticker")] string Sticker,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("contentType")] string ContentType);

public static class StickerCatalog
{
    public static IReadOnlyList<StickerPresentation> Items { get; } =
    [
        Create("Smile.png", "Smile"),
        Create("Heart.png", "Heart"),
        Create("Grin.png", "Grin"),
        Create("Wink.png", "Wink"),
        Create("ThumbsUp.png", "Thumbs up"),
        Create("Dog.png", "Dog"),
        Create("Football.png", "Football"),
        Create("HighFive.png", "High five")
    ];

    public static bool TryGet(string resourceName, out StickerPresentation sticker)
    {
        sticker = Items.FirstOrDefault(item =>
            string.Equals(item.ResourceName, resourceName, StringComparison.Ordinal))!;
        return sticker is not null;
    }

    public static bool TryReadResourceName(string? refPayloadJson, out string resourceName)
    {
        resourceName = string.Empty;
        if (string.IsNullOrWhiteSpace(refPayloadJson))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(refPayloadJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sticker", out JsonElement stickerValue)
                || stickerValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("url", out JsonElement urlValue)
                || urlValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("contentType", out JsonElement contentTypeValue)
                || contentTypeValue.ValueKind != JsonValueKind.String)
                return false;

            string? candidate = stickerValue.GetString();
            if (candidate is null || !TryGet(candidate, out StickerPresentation sticker))
                return false;

            if (!string.Equals(urlValue.GetString(), sticker.ServerPackPath, StringComparison.Ordinal)
                || !string.Equals(contentTypeValue.GetString(), sticker.ContentType, StringComparison.OrdinalIgnoreCase))
                return false;

            resourceName = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static StickerPresentation Create(string resourceName, string label) =>
        new(resourceName,
            $"pack://application:,,,/Aerochat;component/Resources/Emoji/{resourceName}",
            label);
}
