using Aerochat.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace Aerochat.Server.Data.Migrations;

[DbContext(typeof(ChatDb))]
partial class ChatDbModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.30")
            .HasAnnotation("Relational:MaxIdentifierLength", 64);

        modelBuilder.Entity("Aerochat.Server.Data.Entities.ConversationEntity", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT").HasColumnName("id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT").HasColumnName("created_at");
            b.Property<string>("Kind").IsRequired().HasColumnType("TEXT").HasColumnName("kind");
            b.Property<string>("Title").HasColumnType("TEXT").HasColumnName("title");
            b.HasKey("Id").HasName("PK_conversations");
            b.ToTable("conversations", t => t.HasCheckConstraint("CK_conversations_kind", "kind IN ('dm','group','server_channel')"));
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.ExternalUserEntity", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT").HasColumnName("id");
            b.Property<string>("AvatarUrl").HasColumnType("TEXT").HasColumnName("avatar_url");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT").HasColumnName("created_at");
            b.Property<string>("DisplayName").IsRequired().HasColumnType("TEXT").HasColumnName("display_name");
            b.Property<string>("Email").HasColumnType("TEXT").HasColumnName("email");
            b.Property<string>("Provider").IsRequired().HasColumnType("TEXT").HasColumnName("provider");
            b.Property<string>("ProviderUserId").IsRequired().HasColumnType("TEXT").HasColumnName("provider_user_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("TEXT").HasColumnName("updated_at");
            b.HasKey("Id").HasName("PK_users");
            b.HasIndex("Provider", "ProviderUserId").IsUnique().HasDatabaseName("ux_users_provider_provider_user_id");
            b.ToTable("users");
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.MessageEntity", b =>
        {
            b.Property<Guid>("Id").HasColumnType("TEXT").HasColumnName("id");
            b.Property<Guid>("AuthorId").HasColumnType("TEXT").HasColumnName("author_id");
            b.Property<Guid>("ConversationId").HasColumnType("TEXT").HasColumnName("conversation_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT").HasColumnName("created_at");
            b.Property<DateTimeOffset?>("DeletedAt").HasColumnType("TEXT").HasColumnName("deleted_at");
            b.Property<DateTimeOffset?>("EditedAt").HasColumnType("TEXT").HasColumnName("edited_at");
            b.Property<string>("Body").IsRequired().HasColumnType("TEXT").HasColumnName("body");
            b.Property<string>("Kind").IsRequired().HasColumnType("TEXT").HasColumnName("kind");
            b.Property<string>("RefPayloadJson").HasColumnType("TEXT").HasColumnName("ref_payload_json");
            b.HasKey("Id").HasName("PK_messages");
            b.HasIndex("AuthorId").HasDatabaseName("ix_messages_author");
            b.HasIndex("ConversationId", "CreatedAt").IsDescending(false, true).HasDatabaseName("ix_messages_conversation_created");
            b.ToTable("messages", t => t.HasCheckConstraint("CK_messages_kind", "kind IN ('message','sticker','gif','system')"));
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.ParticipantEntity", b =>
        {
            b.Property<Guid>("ConversationId").HasColumnType("TEXT").HasColumnName("conversation_id");
            b.Property<Guid>("UserId").HasColumnType("TEXT").HasColumnName("user_id");
            b.Property<DateTimeOffset>("JoinedAt").HasColumnType("TEXT").HasColumnName("joined_at");
            b.HasKey("ConversationId", "UserId").HasName("PK_participants");
            b.HasIndex("UserId").HasDatabaseName("ix_participants_user");
            b.ToTable("participants");
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.MessageEntity", b =>
        {
            b.HasOne("Aerochat.Server.Data.Entities.ExternalUserEntity", "Author")
                .WithMany("Messages")
                .HasForeignKey("AuthorId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired()
                .HasConstraintName("FK_messages_users");
            b.HasOne("Aerochat.Server.Data.Entities.ConversationEntity", "Conversation")
                .WithMany("Messages")
                .HasForeignKey("ConversationId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired()
                .HasConstraintName("FK_messages_conversations");
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.ParticipantEntity", b =>
        {
            b.HasOne("Aerochat.Server.Data.Entities.ConversationEntity", "Conversation")
                .WithMany("Participants")
                .HasForeignKey("ConversationId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired()
                .HasConstraintName("FK_participants_conversations");
            b.HasOne("Aerochat.Server.Data.Entities.ExternalUserEntity", "User")
                .WithMany("Participants")
                .HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired()
                .HasConstraintName("FK_participants_users");
        });

        modelBuilder.Entity("Aerochat.Server.Data.Entities.ConversationEntity", b =>
        {
            b.Navigation("Messages");
            b.Navigation("Participants");
        });
        modelBuilder.Entity("Aerochat.Server.Data.Entities.ExternalUserEntity", b =>
        {
            b.Navigation("Messages");
            b.Navigation("Participants");
        });
        modelBuilder.Entity("Aerochat.Server.Data.Entities.MessageEntity", b =>
        {
            b.Navigation("Author");
            b.Navigation("Conversation");
        });
        modelBuilder.Entity("Aerochat.Server.Data.Entities.ParticipantEntity", b =>
        {
            b.Navigation("Conversation");
            b.Navigation("User");
        });
#pragma warning restore 612, 618
    }
}
