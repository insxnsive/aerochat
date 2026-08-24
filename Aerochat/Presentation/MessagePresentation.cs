namespace Aerochat.Presentation;

public sealed class MessagePresentation : ObservableObject
{
    private string _body = "";
    public required Guid Id { get; init; }
    public required PersonPresentation Author { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required bool IsOutgoing { get; init; }
    public string Body { get => _body; set => SetProperty(ref _body, value); }
    public string? AttachmentUri { get; init; }
    public MessagePresentation? ReplyTo { get; init; }
}
