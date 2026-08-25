using System.Globalization;
using Aerochat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aerochat.Server.Data;

public sealed class ChatDb : DbContext
{
    private static readonly ValueConverter<Guid, string> GuidConverter = new(
        value => value.ToString("D"),
        value => Guid.Parse(value));

    private static readonly ValueConverter<DateTimeOffset, string> DateTimeOffsetConverter = new(
        value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static readonly ValueConverter<DateTimeOffset?, string?> NullableDateTimeOffsetConverter = new(
        value => value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null,
        value => value != null ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null);

    public ChatDb(DbContextOptions<ChatDb> options)
        : base(options)
    {
    }

    public DbSet<ExternalUserEntity> Users => Set<ExternalUserEntity>();
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<ParticipantEntity> Participants => Set<ParticipantEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var users = modelBuilder.Entity<ExternalUserEntity>();
        users.ToTable("users");
        users.HasKey(user => user.Id).HasName("PK_users");
        users.Property(user => user.Id).HasColumnName("id").HasColumnType("TEXT").HasConversion(GuidConverter);
        users.Property(user => user.Provider).HasColumnName("provider").HasColumnType("TEXT").IsRequired();
        users.Property(user => user.ProviderUserId).HasColumnName("provider_user_id").HasColumnType("TEXT").IsRequired();
        users.Property(user => user.DisplayName).HasColumnName("display_name").HasColumnType("TEXT").IsRequired();
        users.Property(user => user.Email).HasColumnName("email").HasColumnType("TEXT");
        users.Property(user => user.AvatarUrl).HasColumnName("avatar_url").HasColumnType("TEXT");
        users.Property(user => user.CreatedAt).HasColumnName("created_at").HasColumnType("TEXT").IsRequired().HasConversion(DateTimeOffsetConverter);
        users.Property(user => user.UpdatedAt).HasColumnName("updated_at").HasColumnType("TEXT").IsRequired().HasConversion(DateTimeOffsetConverter);
        users.HasIndex(user => new { user.Provider, user.ProviderUserId }).IsUnique().HasDatabaseName("ux_users_provider_provider_user_id");

        var conversations = modelBuilder.Entity<ConversationEntity>();
        conversations.ToTable("conversations", table => table.HasCheckConstraint("CK_conversations_kind", "kind IN ('dm','group','server_channel')"));
        conversations.HasKey(conversation => conversation.Id).HasName("PK_conversations");
        conversations.Property(conversation => conversation.Id).HasColumnName("id").HasColumnType("TEXT").HasConversion(GuidConverter);
        conversations.Property(conversation => conversation.Kind).HasColumnName("kind").HasColumnType("TEXT").IsRequired();
        conversations.Property(conversation => conversation.Title).HasColumnName("title").HasColumnType("TEXT");
        conversations.Property(conversation => conversation.CreatedAt).HasColumnName("created_at").HasColumnType("TEXT").IsRequired().HasConversion(DateTimeOffsetConverter);

        var participants = modelBuilder.Entity<ParticipantEntity>();
        participants.ToTable("participants");
        participants.HasKey(participant => new { participant.ConversationId, participant.UserId }).HasName("PK_participants");
        participants.Property(participant => participant.ConversationId).HasColumnName("conversation_id").HasColumnType("TEXT").HasConversion(GuidConverter);
        participants.Property(participant => participant.UserId).HasColumnName("user_id").HasColumnType("TEXT").HasConversion(GuidConverter);
        participants.Property(participant => participant.JoinedAt).HasColumnName("joined_at").HasColumnType("TEXT").IsRequired().HasConversion(DateTimeOffsetConverter);
        participants.HasIndex(participant => participant.UserId)
            .HasDatabaseName("ix_participants_user");
        participants.HasOne(participant => participant.Conversation)
            .WithMany(conversation => conversation.Participants)
            .HasForeignKey(participant => participant.ConversationId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_participants_conversations");
        participants.HasOne(participant => participant.User)
            .WithMany(user => user.Participants)
            .HasForeignKey(participant => participant.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_participants_users");

        var messages = modelBuilder.Entity<MessageEntity>();
        messages.ToTable("messages", table => table.HasCheckConstraint("CK_messages_kind", "kind IN ('message','sticker','gif','system')"));
        messages.HasKey(message => message.Id).HasName("PK_messages");
        messages.Property(message => message.Id).HasColumnName("id").HasColumnType("TEXT").HasConversion(GuidConverter);
        messages.Property(message => message.ConversationId).HasColumnName("conversation_id").HasColumnType("TEXT").IsRequired().HasConversion(GuidConverter);
        messages.Property(message => message.AuthorId).HasColumnName("author_id").HasColumnType("TEXT").IsRequired().HasConversion(GuidConverter);
        messages.Property(message => message.Body).HasColumnName("body").HasColumnType("TEXT").IsRequired();
        messages.Property(message => message.Kind).HasColumnName("kind").HasColumnType("TEXT").IsRequired();
        messages.Property(message => message.RefPayloadJson).HasColumnName("ref_payload_json").HasColumnType("TEXT");
        messages.Property(message => message.CreatedAt).HasColumnName("created_at").HasColumnType("TEXT").IsRequired().HasConversion(DateTimeOffsetConverter);
        messages.Property(message => message.EditedAt).HasColumnName("edited_at").HasColumnType("TEXT").HasConversion(NullableDateTimeOffsetConverter);
        messages.Property(message => message.DeletedAt).HasColumnName("deleted_at").HasColumnType("TEXT").HasConversion(NullableDateTimeOffsetConverter);
        messages.HasIndex(message => new { message.ConversationId, message.CreatedAt })
            .HasDatabaseName("ix_messages_conversation_created")
            .IsDescending(false, true);
        messages.HasIndex(message => message.AuthorId)
            .HasDatabaseName("ix_messages_author");
        messages.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_messages_conversations");
        messages.HasOne(message => message.Author)
            .WithMany(user => user.Messages)
            .HasForeignKey(message => message.AuthorId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_messages_users");

    }
}
