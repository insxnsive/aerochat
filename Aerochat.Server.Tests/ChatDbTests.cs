using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aerochat.Server.Tests;

public sealed class ChatDbTests
{
    private const string InitialMigrationId = "20260825000000_InitialChatSchema";

    [Test]
    public void Initial_migration_creates_expected_tables_and_index()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        db.Database.Migrate();

        var tables = db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name IN ('users', 'conversations', 'participants', 'messages') ORDER BY name")
            .ToList();
        var indexes = db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = 'ix_messages_conversation_created'")
            .ToList();

        Assert.That(tables, Is.EqualTo(new[] { "conversations", "messages", "participants", "users" }));
        Assert.That(indexes, Is.EqualTo(new[] { "ix_messages_conversation_created" }));
        Assert.That(db.Database.GetAppliedMigrations(), Does.Contain(InitialMigrationId));
    }

    [Test]
    public void Model_snapshot_matches_runtime_model()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();

        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var modelDiffer = db.GetService<IMigrationsModelDiffer>();
        var modelInitializer = db.GetService<IModelRuntimeInitializer>();
        var snapshotModel = modelInitializer.Initialize(
            migrationsAssembly.ModelSnapshot!.Model,
            designTime: true);
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var operations = modelDiffer.GetDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel());

        Assert.That(
            operations,
            Is.Empty,
            string.Join(Environment.NewLine, operations.Select(operation => operation.ToString())));
    }

    [Test]
    public void Down_migration_removes_all_chat_tables()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();

        db.GetService<IMigrator>().Migrate(Migration.InitialDatabase);

        var tables = db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name IN ('users', 'conversations', 'participants', 'messages') ORDER BY name")
            .ToList();
        Assert.That(tables, Is.Empty);
    }

    [Test]
    public void External_user_provider_identity_is_unique()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        db.Users.AddRange(
            new ExternalUserEntity
            {
                Id = Guid.NewGuid(),
                Provider = "google",
                ProviderUserId = "same-user",
                DisplayName = "First",
                CreatedAt = now,
                UpdatedAt = now
            },
            new ExternalUserEntity
            {
                Id = Guid.NewGuid(),
                Provider = "google",
                ProviderUserId = "same-user",
                DisplayName = "Second",
                CreatedAt = now,
                UpdatedAt = now
            });

        Assert.That(() => db.SaveChanges(), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public void Conversation_participant_message_roundtrip()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        using (var db = CreateDb(connection))
        {
            db.Database.Migrate();
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = firstUserId,
                    Provider = "google",
                    ProviderUserId = "first-user",
                    DisplayName = "First User",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = secondUserId,
                    Provider = "github",
                    ProviderUserId = "second-user",
                    DisplayName = "Second User",
                    Email = "second@example.test",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "group",
                Title = "Aerochat",
                CreatedAt = now
            });
            db.Participants.AddRange(
                new ParticipantEntity { ConversationId = conversationId, UserId = firstUserId, JoinedAt = now },
                new ParticipantEntity { ConversationId = conversationId, UserId = secondUserId, JoinedAt = now });
            db.Messages.Add(new MessageEntity
            {
                Id = messageId,
                ConversationId = conversationId,
                AuthorId = firstUserId,
                Body = "Hello from SQLite",
                Kind = "message",
                RefPayloadJson = "{\"source\":\"test\"}",
                CreatedAt = now
            });
            db.SaveChanges();
        }

        using var restarted = CreateDb(connection);
        var conversation = restarted.Conversations
            .Include(value => value.Participants)
                .ThenInclude(value => value.User)
            .Include(value => value.Messages)
                .ThenInclude(value => value.Author)
            .Single(value => value.Id == conversationId);

        Assert.Multiple(() =>
        {
            Assert.That(conversation.Kind, Is.EqualTo("group"));
            Assert.That(conversation.Title, Is.EqualTo("Aerochat"));
            Assert.That(conversation.Participants.Select(value => value.UserId), Is.EquivalentTo(new[] { firstUserId, secondUserId }));
            Assert.That(conversation.Participants.Single(value => value.UserId == secondUserId).User.Email, Is.EqualTo("second@example.test"));
            Assert.That(conversation.Messages.Single().Id, Is.EqualTo(messageId));
            Assert.That(conversation.Messages.Single().Author.DisplayName, Is.EqualTo("First User"));
            Assert.That(conversation.Messages.Single().Body, Is.EqualTo("Hello from SQLite"));
            Assert.That(conversation.Messages.Single().RefPayloadJson, Is.EqualTo("{\"source\":\"test\"}"));
        });
    }

    [Test]
    public void Duplicate_participant_is_rejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        using (var db = CreateDb(connection))
        {
            db.Database.Migrate();
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "google",
                ProviderUserId = "participant-user",
                DisplayName = "Participant",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now
            });
            db.Participants.Add(new ParticipantEntity
            {
                ConversationId = conversationId,
                UserId = userId,
                JoinedAt = now
            });
            db.SaveChanges();
        }

        using var restarted = CreateDb(connection);
        restarted.Participants.Add(new ParticipantEntity
        {
            ConversationId = conversationId,
            UserId = userId,
            JoinedAt = now
        });

        Assert.That(() => restarted.SaveChanges(), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public void Deleting_conversation_cascades_participants_and_messages()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var userId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        using (var db = CreateDb(connection))
        {
            db.Database.Migrate();
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = userId,
                    Provider = "google",
                    ProviderUserId = "cascade-user",
                    DisplayName = "Cascade User",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = secondUserId,
                    Provider = "github",
                    ProviderUserId = "cascade-author",
                    DisplayName = "Cascade Author",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now
            });
            db.Participants.AddRange(
                new ParticipantEntity { ConversationId = conversationId, UserId = userId, JoinedAt = now },
                new ParticipantEntity { ConversationId = conversationId, UserId = secondUserId, JoinedAt = now });
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                AuthorId = secondUserId,
                Body = "Delete me with the conversation",
                Kind = "message",
                CreatedAt = now
            });
            db.SaveChanges();
        }

        using (var restarted = CreateDb(connection))
        {
            restarted.Conversations.Remove(restarted.Conversations.Single(value => value.Id == conversationId));
            restarted.SaveChanges();
        }

        using var verify = CreateDb(connection);
        Assert.Multiple(() =>
        {
            Assert.That(verify.Users.Count(), Is.EqualTo(2));
            Assert.That(verify.Conversations.Count(), Is.Zero);
            Assert.That(verify.Participants.Count(), Is.Zero);
            Assert.That(verify.Messages.Count(), Is.Zero);
        });
    }

    [Test]
    public void Invalid_conversation_kind_is_rejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();
        db.Conversations.Add(new ConversationEntity
        {
            Id = Guid.NewGuid(),
            Kind = "invalid",
            CreatedAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z")
        });

        Assert.That(() => db.SaveChanges(), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public void Invalid_message_kind_is_rejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var user = CreateUser("invalid-kind-author", now);
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            Kind = "dm",
            CreatedAt = now
        };
        db.AddRange(user, conversation);
        db.SaveChanges();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            AuthorId = user.Id,
            Body = "Invalid kind",
            Kind = "invalid",
            CreatedAt = now
        });

        Assert.That(() => db.SaveChanges(), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public void Deleting_user_with_authored_message_is_restricted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.Migrate();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var author = CreateUser("restricted-author", now);
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            Kind = "dm",
            CreatedAt = now
        };
        db.AddRange(author, conversation);
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            AuthorId = author.Id,
            Body = "Keeps author alive",
            Kind = "message",
            CreatedAt = now
        });
        db.SaveChanges();

        db.ChangeTracker.Clear();
        db.Users.Remove(db.Users.Single(user => user.Id == author.Id));
        Assert.That(() => db.SaveChanges(), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public void Deleting_non_author_user_cascades_participant_only()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var author = CreateUser("remaining-author", now);
        var participant = CreateUser("removed-participant", now);
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            Kind = "dm",
            CreatedAt = now
        };

        using (var db = CreateDb(connection))
        {
            db.Database.Migrate();
            db.AddRange(author, participant, conversation);
            db.Participants.AddRange(
                new ParticipantEntity { ConversationId = conversation.Id, UserId = author.Id, JoinedAt = now },
                new ParticipantEntity { ConversationId = conversation.Id, UserId = participant.Id, JoinedAt = now });
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                AuthorId = author.Id,
                Body = "Survives participant deletion",
                Kind = "message",
                CreatedAt = now
            });
            db.SaveChanges();
        }

        using (var delete = CreateDb(connection))
        {
            delete.Users.Remove(delete.Users.Single(user => user.Id == participant.Id));
            delete.SaveChanges();
        }

        using var verify = CreateDb(connection);
        Assert.Multiple(() =>
        {
            Assert.That(verify.Users.Select(user => user.Id), Is.EqualTo(new[] { author.Id }));
            Assert.That(verify.Participants.Select(value => value.UserId), Is.EqualTo(new[] { author.Id }));
            Assert.That(verify.Conversations.Count(), Is.EqualTo(1));
            Assert.That(verify.Messages.Count(), Is.EqualTo(1));
        });
    }

    private static ExternalUserEntity CreateUser(string providerUserId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = "google",
            ProviderUserId = providerUserId,
            DisplayName = providerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ChatDb CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ChatDb>()
            .UseSqlite(connection)
            .Options;
        return new ChatDb(options);
    }
}
