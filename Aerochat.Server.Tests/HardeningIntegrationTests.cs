using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class HardeningIntegrationTests
{
    [Test]
    public async Task Message_rate_limit_returns_retry_after_and_resets()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        using var factory = new ApiWebApplicationFactory
        {
            Clock = clock,
            RateLimit = 1,
            RateLimitWindowSeconds = 60
        };
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId, Provider = "github", ProviderUserId = "rate-user",
                DisplayName = "Rate User", CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId, Kind = "dm", CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "rate-user");
        static StringContent Message(string body) =>
            new(JsonSerializer.Serialize(new { body, kind = "message" }), Encoding.UTF8, "application/json");

        using HttpResponseMessage first = await client.PostAsync($"/conversations/{conversationId}/messages", Message("one"));
        using HttpResponseMessage limited = await client.PostAsync($"/conversations/{conversationId}/messages", Message("two"));
        string limitedBody = await limited.Content.ReadAsStringAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        using HttpResponseMessage reset = await client.PostAsync($"/conversations/{conversationId}/messages", Message("three"));

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(limitedBody, Is.EqualTo("{\"error\":\"rate_limited\"}"));
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        });
    }

    private static HttpClient CreateAuthorizedClient(ApiWebApplicationFactory factory, string providerUserId)
    {
        HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", providerUserId, providerUserId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
