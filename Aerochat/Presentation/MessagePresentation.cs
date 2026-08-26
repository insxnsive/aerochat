namespace Aerochat.Presentation;

public sealed class MessagePresentation : ObservableObject
{
    private string _body = "";
    public required Guid Id { get; init; }
    public required PersonPresentation Author { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required bool IsOutgoing { get; init; }
    public string Body { get => _body; set => SetProperty(ref _body, value); }
    public string Kind { get; init; } = "message";
    public string? RefPayloadJson { get; init; }
    public bool IsSticker => string.Equals(Kind, "sticker", StringComparison.Ordinal)
        && StickerCatalog.TryReadResourceName(RefPayloadJson, out _);
    public string? StickerUri => StickerCatalog.TryReadResourceName(RefPayloadJson, out string resourceName)
        && StickerCatalog.TryGet(resourceName, out StickerPresentation? sticker)
            ? sticker.ResourceUri
            : null;
    public string? AttachmentUri { get; init; }
    public MessagePresentation? ReplyTo { get; init; }
}
