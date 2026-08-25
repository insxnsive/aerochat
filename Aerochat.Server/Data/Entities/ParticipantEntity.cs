namespace Aerochat.Server.Data.Entities;

public sealed class ParticipantEntity
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public ConversationEntity Conversation { get; set; } = null!;
    public ExternalUserEntity User { get; set; } = null!;
}
