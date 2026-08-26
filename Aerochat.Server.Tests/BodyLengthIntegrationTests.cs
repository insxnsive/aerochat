using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class BodyLengthIntegrationTests
{
    [Test]
    public async Task Overlong_message_returns_body_too_long_without_persisting()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId, Provider = "github", ProviderUserId = "long-user",
                DisplayName = "Long User", CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "long-user", "Long User"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.PostAsync(
            $"/conversations/{conversationId}/messages",
            new StringContent(JsonSerializer.Serialize(new { body = new string('x', 2001), kind = "message" }), Encoding.UTF8, "application/json"));

        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"error\":\"body_too_long\"}"));
    }
}
