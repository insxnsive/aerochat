using System.Net;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class GatewayHandshakeTests
{
    [TestCase("/ws")]
    [TestCase("/ws?token=")]
    [TestCase("/ws?token=not-a-session")]
    public async Task Missing_or_invalid_gateway_token_returns_401_with_bearer_challenge(string path)
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
        });
    }

    [Test]
    public async Task Valid_local_gateway_token_without_upgrade_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "gateway-handshake",
                DisplayName = "Gateway Handshake",
                CreatedAt = now,
                UpdatedAt = now
            });
            return Task.CompletedTask;
        });

        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "gateway-handshake", "Gateway Handshake"));
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync($"/ws?token={Uri.EscapeDataString(token)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
