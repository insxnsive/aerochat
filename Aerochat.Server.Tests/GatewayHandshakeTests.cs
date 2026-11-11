using System.Net;
using Aerochat.Server.Auth;
using Aerochat.Server.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class GatewayHandshakeTests
{
    [TestCase(null)]
    [TestCase("Bearer not-a-session")]
    [TestCase("Bearer ")]
    [TestCase("Basic dXNlcjpwYXNz")]
    public async Task Missing_or_invalid_gateway_bearer_token_returns_401_with_bearer_challenge(string? authorization)
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ws");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
        });
    }

    [Test]
    public async Task Query_string_token_without_header_is_rejected_as_invalid_request()
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync("/ws?token=not-a-session");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Headers.WwwAuthenticate, Is.Empty);
            Assert.That(body, Does.Contain("invalid_request"));
        });
    }

    [TestCase("/ws?token={0}")]
    [TestCase("/ws?token=")]
    [TestCase("/ws?token={0}&token={0}")]
    [TestCase("/ws?TOKEN={0}")]
    [TestCase("/ws?lastEventId=1&token={0}")]
    public async Task Query_string_token_is_rejected_even_when_the_header_is_valid(string pathTemplate)
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
                ProviderUserId = "gateway-header-only",
                DisplayName = "Gateway Header Only",
                CreatedAt = now,
                UpdatedAt = now
            });
            return Task.CompletedTask;
        });

        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "gateway-header-only", "Gateway Header Only"));
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        string path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            pathTemplate,
            Uri.EscapeDataString(token));

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
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
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/ws");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
