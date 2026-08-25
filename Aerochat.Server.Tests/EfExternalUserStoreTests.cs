using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Tests;

public sealed partial class ExternalUserStoreTests
{
    [Test]
    public async Task Ef_store_preserves_id_and_created_at_across_context_restart()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var createdAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var updatedAt = createdAt.AddMinutes(5);
        ExternalUser first;

        using (var firstDb = CreateDb(connection))
        {
            await firstDb.Database.MigrateAsync();
            var store = new EfExternalUserStore(firstDb);
            first = await store.UpsertAsync(
                new ExternalIdentity("google", "durable-user", "First", "first@example.test", null),
                createdAt);
        }

        using (var restartedDb = CreateDb(connection))
        {
            var store = new EfExternalUserStore(restartedDb);
            var second = await store.UpsertAsync(
                new ExternalIdentity("google", "durable-user", "Updated", "updated@example.test", "https://avatar.example/u"),
                updatedAt);
            var found = await store.FindAsync("google", "durable-user");

            Assert.Multiple(() =>
            {
                Assert.That(second.Id, Is.EqualTo(first.Id));
                Assert.That(second.CreatedAt, Is.EqualTo(createdAt));
                Assert.That(second.UpdatedAt, Is.EqualTo(updatedAt));
                Assert.That(second.DisplayName, Is.EqualTo("Updated"));
                Assert.That(found!.Id, Is.EqualTo(first.Id));
            });
        }
    }

    private static ChatDb CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ChatDb>()
            .UseSqlite(connection)
            .Options;
        return new ChatDb(options);
    }
}
