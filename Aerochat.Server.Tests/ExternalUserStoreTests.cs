using Aerochat.Server.Auth.OAuth;

namespace Aerochat.Server.Tests;

public sealed partial class ExternalUserStoreTests
{
    [Test]
    public async Task In_memory_async_upsert_preserves_identity_and_created_at()
    {
        var store = new InMemoryExternalUserStore();
        var createdAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var updatedAt = createdAt.AddMinutes(5);

        var first = await store.UpsertAsync(
            new ExternalIdentity("google", "provider-user", "First", "first@example.test", null),
            createdAt);
        var second = await store.UpsertAsync(
            new ExternalIdentity("google", "provider-user", "Updated", "updated@example.test", "https://avatar.example/u"),
            updatedAt);
        var found = await store.FindAsync("google", "provider-user");

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.Not.Null);
            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(second.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(second.UpdatedAt, Is.EqualTo(updatedAt));
            Assert.That(second.DisplayName, Is.EqualTo("Updated"));
            Assert.That(second.Email, Is.EqualTo("updated@example.test"));
            Assert.That(found!.Id, Is.EqualTo(first.Id));
        });
    }
}
