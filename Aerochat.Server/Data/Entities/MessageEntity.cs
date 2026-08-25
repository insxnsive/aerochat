namespace Aerochat.Server.Data.Entities;

public sealed class MessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? RefPayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ConversationEntity Conversation { get; set; } = null!;
    public ExternalUserEntity Author { get; set; } = null!;
}
