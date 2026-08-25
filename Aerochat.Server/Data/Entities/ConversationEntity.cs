namespace Aerochat.Server.Data.Entities;

public sealed class ConversationEntity
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ParticipantEntity> Participants { get; } = new List<ParticipantEntity>();
    public ICollection<MessageEntity> Messages { get; } = new List<MessageEntity>();
}
