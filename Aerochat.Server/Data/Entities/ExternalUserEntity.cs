namespace Aerochat.Server.Data.Entities;

public sealed class ExternalUserEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ParticipantEntity> Participants { get; } = new List<ParticipantEntity>();
    public ICollection<MessageEntity> Messages { get; } = new List<MessageEntity>();
}
