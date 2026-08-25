using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aerochat.Server.Tests;

public sealed partial class ExternalUserStoreTests
{
    [Test]
    public async Task Ef_store_recovers_from_unique_identity_race()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"aerochat-race-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        var winnerId = Guid.NewGuid();
        var winnerCreatedAt = DateTimeOffset.Parse("2026-08-25T11:00:00Z");
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        try
        {
            await using (var setup = new ChatDb(new DbContextOptionsBuilder<ChatDb>().UseSqlite(connectionString).Options))
            {
                await setup.Database.MigrateAsync();
            }

            var interceptor = new UniqueWinnerInterceptor(
                connectionString,
                winnerId,
                winnerCreatedAt);
            await using var db = new ChatDb(
                new DbContextOptionsBuilder<ChatDb>()
                    .UseSqlite(connectionString)
                    .AddInterceptors(interceptor)
                    .Options);
            var store = new EfExternalUserStore(db);

            var result = await store.UpsertAsync(
                new ExternalIdentity("google", "raced-user", "Updated after race", "updated@example.test", null),
                now);

            Assert.Multiple(() =>
            {
                Assert.That(result.Id, Is.EqualTo(winnerId));
                Assert.That(result.CreatedAt, Is.EqualTo(winnerCreatedAt));
                Assert.That(result.UpdatedAt, Is.EqualTo(now));
                Assert.That(result.DisplayName, Is.EqualTo("Updated after race"));
                Assert.That(interceptor.InsertedWinner, Is.True);
            });
            await db.DisposeAsync();
        }
        finally
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }
    }

    private sealed class UniqueWinnerInterceptor : SaveChangesInterceptor
    {
        private readonly string _connectionString;
        private readonly Guid _winnerId;
        private readonly DateTimeOffset _winnerCreatedAt;

        public UniqueWinnerInterceptor(string connectionString, Guid winnerId, DateTimeOffset winnerCreatedAt)
        {
            _connectionString = connectionString;
            _winnerId = winnerId;
            _winnerCreatedAt = winnerCreatedAt;
        }

        public bool InsertedWinner { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!InsertedWinner)
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users
                        (id, provider, provider_user_id, display_name, email, avatar_url, created_at, updated_at)
                    VALUES
                        ($id, $provider, $provider_user_id, $display_name, $email, $avatar_url, $created_at, $updated_at);
                    """;
                command.Parameters.AddWithValue("$id", _winnerId.ToString("D"));
                command.Parameters.AddWithValue("$provider", "google");
                command.Parameters.AddWithValue("$provider_user_id", "raced-user");
                command.Parameters.AddWithValue("$display_name", "Race winner");
                command.Parameters.AddWithValue("$email", "winner@example.test");
                command.Parameters.AddWithValue("$avatar_url", DBNull.Value);
                command.Parameters.AddWithValue("$created_at", _winnerCreatedAt.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$updated_at", _winnerCreatedAt.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
                InsertedWinner = true;
            }

            return result;
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
