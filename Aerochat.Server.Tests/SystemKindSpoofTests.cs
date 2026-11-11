using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class SystemKindSpoofTests
{
    private static readonly Guid Author = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Member = Guid.Parse("10000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Conversation = Guid.Parse("20000000-0000-0000-0000-0000000000a1");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static async Task SeedAsync(ApiWebApplicationFactory factory)
    {
        await factory.SeedAsync(db =>
        {
            foreach (Guid id in new[] { Author, Member })
            {
                db.Users.Add(new ExternalUserEntity
                {
                    Id = id,
                    Provider = "github",
                    ProviderUserId = id.ToString(),
                    DisplayName = "fixture",
                    CreatedAt = Created,
                    UpdatedAt = Created
                });
            }

            db.Conversations.Add(new ConversationEntity
            {
                Id = Conversation,
                Kind = "group",
                CreatedAt = Created,
                Participants =
                {
                    new ParticipantEntity { UserId = Author, JoinedAt = Created },
                    new ParticipantEntity { UserId = Member, JoinedAt = Created }
                }
            });
            return Task.CompletedTask;
        });
    }

    private static HttpClient AuthenticatedClient(ApiWebApplicationFactory factory, Guid userId)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.Services.GetRequiredService<SessionService>()
                .Issue(new Identity("github", userId.ToString(), "fixture")));
        return client;
    }

    private static StringContent Body(string kind) =>
        new(
            JsonSerializer.Serialize(new { body = "spoofed notice", kind }),
            Encoding.UTF8,
            "application/json");

    private static string MessagesPath => $"/conversations/{Conversation}/messages";

    [TestCase("author", "system")]
    [TestCase("member", "system")]
    [TestCase("member", "System")]
    [TestCase("member", "SYSTEM")]
    [TestCase("member", " system")]
    public async Task User_post_with_a_system_kind_is_rejected_for_every_participant(string who, string kind)
    {
        using var factory = new ApiWebApplicationFactory();
        await SeedAsync(factory);
        GatewayHub hub = factory.Services.GetRequiredService<GatewayHub>();
        var authorSink = new Sink(Author, "author-sink");
        var memberSink = new Sink(Member, "member-sink");
        hub.Register(authorSink);
        hub.Register(memberSink);
        authorSink.Events.Clear();
        memberSink.Events.Clear();
        using HttpClient client = AuthenticatedClient(factory, who == "author" ? Author : Member);

        using HttpResponseMessage response = await client.PostAsync(MessagesPath, Body(kind));
        string error = await response.Content.ReadAsStringAsync();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        int stored = await scope.ServiceProvider.GetRequiredService<ChatDb>().Messages.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error, Does.Contain("invalid_request"));
            Assert.That(stored, Is.Zero);
            Assert.That(authorSink.Events, Is.Empty);
            Assert.That(memberSink.Events, Is.Empty);
        });
    }

    [TestCase("message")]
    [TestCase("gif")]
    public async Task User_post_with_the_other_allowed_kinds_still_succeeds(string kind)
    {
        using var factory = new ApiWebApplicationFactory();
        await SeedAsync(factory);
        using HttpClient client = AuthenticatedClient(factory, Member);

        using HttpResponseMessage response = await client.PostAsync(MessagesPath, Body(kind));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        MessageEntity stored = await scope.ServiceProvider.GetRequiredService<ChatDb>().Messages.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(stored.Kind, Is.EqualTo(kind));
        });
    }

    [Test]
    public async Task A_system_message_written_by_server_code_is_still_returned_by_history()
    {
        using var factory = new ApiWebApplicationFactory();
        await SeedAsync(factory);
        await factory.SeedAsync(db =>
        {
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = Conversation,
                AuthorId = Author,
                Body = "server notice",
                Kind = "system",
                CreatedAt = Created.AddMinutes(1)
            });
            return Task.CompletedTask;
        });
        using HttpClient client = AuthenticatedClient(factory, Member);

        using HttpResponseMessage response = await client.GetAsync(MessagesPath);
        using JsonDocument page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(page.RootElement.GetProperty("items")[0].GetProperty("kind").GetString(), Is.EqualTo("system"));
        });
    }

    private sealed class Sink(Guid user, string connection) : IGatewaySink
    {
        internal List<GatewayEnvelope> Events = [];

        public Guid UserId => user;

        public string ConnectionId => connection;

        public CancellationToken Disconnected => CancellationToken.None;

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            Events.Add(envelope);
            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }
}
