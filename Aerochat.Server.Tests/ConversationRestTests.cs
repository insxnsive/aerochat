using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Rest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class ConversationRestTests
{
    [Test]
    public async Task GetConversations_without_bearer_returns_401()
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/conversations");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
    }

    [Test]
    public async Task Valid_token_without_local_user_returns_401()
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "missing-local-user", "Missing Local User"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/conversations");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
    }

    [Test]
    public async Task Blank_message_body_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "blank-body",
                DisplayName = "Blank Body",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "blank-body");
        using HttpResponseMessage response = await client.PostAsync(
            $"/conversations/{conversationId}/messages",
            new StringContent("{\"body\":\"   \",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Unsupported_message_kind_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "unsupported-kind",
                DisplayName = "Unsupported Kind",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "unsupported-kind");
        using HttpResponseMessage response = await client.PostAsync(
            $"/conversations/{conversationId}/messages",
            new StringContent("{\"body\":\"hello\",\"kind\":\"unsupported\"}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Known_non_member_cannot_send_message()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid memberId = Guid.NewGuid();
        Guid nonMemberId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = memberId,
                    Provider = "github",
                    ProviderUserId = "send-member",
                    DisplayName = "Send Member",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = nonMemberId,
                    Provider = "github",
                    ProviderUserId = "send-non-member",
                    DisplayName = "Send Non-member",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = memberId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "send-non-member");
        using HttpResponseMessage response = await client.PostAsync(
            $"/conversations/{conversationId}/messages",
            new StringContent("{\"body\":\"hello\",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Missing_conversation_returns_404_for_history_and_send()
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
                ProviderUserId = "missing-conversation",
                DisplayName = "Missing Conversation",
                CreatedAt = now,
                UpdatedAt = now
            });
            return Task.CompletedTask;
        });

        Guid missingConversationId = Guid.NewGuid();
        using HttpClient client = CreateAuthorizedClient(factory, "missing-conversation");
        using HttpResponseMessage history = await client.GetAsync(
            $"/conversations/{missingConversationId}/messages");
        using HttpResponseMessage send = await client.PostAsync(
            $"/conversations/{missingConversationId}/messages",
            new StringContent("{\"body\":\"hello\",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));

        Assert.Multiple(() =>
        {
            Assert.That(history.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(send.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Malformed_conversation_id_returns_400_for_history_and_send()
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
                ProviderUserId = "malformed-conversation",
                DisplayName = "Malformed Conversation",
                CreatedAt = now,
                UpdatedAt = now
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "malformed-conversation");
        using HttpResponseMessage history = await client.GetAsync("/conversations/not-a-guid/messages");
        using HttpResponseMessage send = await client.PostAsync(
            "/conversations/not-a-guid/messages",
            new StringContent("{\"body\":\"hello\",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));

        Assert.Multiple(() =>
        {
            Assert.That(history.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(send.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Nonpositive_or_nonnumeric_limit_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "invalid-limit",
                DisplayName = "Invalid Limit",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "invalid-limit");
        foreach (string value in new[] { "0", "-1", "not-a-number" })
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/conversations/{conversationId}/messages?limit={value}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), value);
        }
    }

    [Test]
    public async Task Malformed_or_wrong_conversation_cursor_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid firstConversationId = Guid.NewGuid();
        Guid secondConversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "invalid-cursor",
                DisplayName = "Invalid Cursor",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.AddRange(
                new ConversationEntity
                {
                    Id = firstConversationId,
                    Kind = "dm",
                    CreatedAt = now,
                    Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
                },
                new ConversationEntity
                {
                    Id = secondConversationId,
                    Kind = "dm",
                    CreatedAt = now,
                    Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
                });
            db.Messages.AddRange(
                new MessageEntity
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000011"),
                    ConversationId = firstConversationId,
                    AuthorId = userId,
                    Body = "first",
                    Kind = "message",
                    CreatedAt = now
                },
                new MessageEntity
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000010"),
                    ConversationId = firstConversationId,
                    AuthorId = userId,
                    Body = "second",
                    Kind = "message",
                    CreatedAt = now.AddMinutes(-1)
                });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "invalid-cursor");
        using HttpResponseMessage firstPageResponse = await client.GetAsync(
            $"/conversations/{firstConversationId}/messages?limit=1");
        var firstPage = JsonSerializer.Deserialize<MessagePageDto>(
            await firstPageResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.That(firstPageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPage?.NextBefore, Is.Not.Null.And.Not.Empty);

        using HttpResponseMessage malformed = await client.GetAsync(
            $"/conversations/{firstConversationId}/messages?before={Uri.EscapeDataString("not-a-cursor")}");
        using HttpResponseMessage wrongConversation = await client.GetAsync(
            $"/conversations/{secondConversationId}/messages?before={Uri.EscapeDataString(firstPage!.NextBefore!)}");

        Assert.Multiple(() =>
        {
            Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(wrongConversation.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Empty_whitespace_padded_and_noncanonical_cursors_return_400()
    {
        using var factory = new ApiWebApplicationFactory();
        (Guid conversationId, DateTimeOffset now) =
            await SeedConversationWithMessagesAsync(factory, "strict-cursor", 2);
        using HttpClient client = CreateAuthorizedClient(factory, "strict-cursor");

        using HttpResponseMessage firstResponse = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=1");
        var firstPage = JsonSerializer.Deserialize<MessagePageDto>(
            await firstResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPage?.NextBefore, Is.Not.Null.And.Not.Empty);

        string validCursor = firstPage!.NextBefore!;
        string json = DecodeCursor(validCursor);
        string noncanonicalCursor = EncodeCursorJson(" {" + json[1..]);
        string[] invalidCursors =
        [
            string.Empty,
            " " + validCursor + " ",
            validCursor + "=",
            noncanonicalCursor
        ];

        foreach (string invalidCursor in invalidCursors)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/conversations/{conversationId}/messages?before={Uri.EscapeDataString(invalidCursor)}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidCursor);
        }
    }

    [Test]
    public async Task Noncanonical_cursor_timestamp_returns_400()
    {
        using var factory = new ApiWebApplicationFactory();
        (Guid conversationId, DateTimeOffset now) =
            await SeedConversationWithMessagesAsync(factory, "noncanonical-timestamp", 2);
        using HttpClient client = CreateAuthorizedClient(factory, "noncanonical-timestamp");

        using HttpResponseMessage firstResponse = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=1");
        var firstPage = JsonSerializer.Deserialize<MessagePageDto>(
            await firstResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPage?.NextBefore, Is.Not.Null.And.Not.Empty);

        string canonicalTimestamp = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string noncanonicalJson = DecodeCursor(firstPage!.NextBefore!)
            .Replace(canonicalTimestamp, now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        string noncanonicalTimestampCursor = EncodeCursorJson(noncanonicalJson);

        using HttpResponseMessage response = await client.GetAsync(
            $"/conversations/{conversationId}/messages?before={Uri.EscapeDataString(noncanonicalTimestampCursor)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Int_max_value_limit_is_accepted_without_overflow()
    {
        using var factory = new ApiWebApplicationFactory();
        (Guid conversationId, _) =
            await SeedConversationWithMessagesAsync(factory, "max-limit", 1);
        using HttpClient client = CreateAuthorizedClient(factory, "max-limit");

        using HttpResponseMessage response = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit={int.MaxValue.ToString(CultureInfo.InvariantCulture)}");
        var page = JsonSerializer.Deserialize<MessagePageDto>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(factory.MessageQueryLimits, Does.Contain(int.MaxValue));
        Assert.That(page, Is.Not.Null);
        Assert.That(page!.Items, Has.Count.EqualTo(1));
        Assert.That(page.NextBefore, Is.Null);
    }

    [Test]
    public async Task Known_non_member_cannot_read_history()
    {
        using var factory = new ApiWebApplicationFactory();
        var memberId = Guid.NewGuid();
        var nonMemberId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        await factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new ExternalUserEntity
                {
                    Id = memberId,
                    Provider = "github",
                    ProviderUserId = "member",
                    DisplayName = "Member",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ExternalUserEntity
                {
                    Id = nonMemberId,
                    Provider = "github",
                    ProviderUserId = "non-member",
                    DisplayName = "Non-member",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants =
                {
                    new ParticipantEntity { UserId = memberId, JoinedAt = now }
                }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "non-member", "Non-member"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync($"/conversations/{conversationId}/messages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task History_returns_newest_first_and_continues_with_opaque_cursor()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        Guid newestId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        Guid sameTimeHighId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid sameTimeLowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid deletedId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset sameTime = now.AddMinutes(-1);

        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "history-user",
                DisplayName = "History User",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "group",
                CreatedAt = now,
                Participants =
                {
                    new ParticipantEntity { UserId = userId, JoinedAt = now }
                }
            });
            db.Messages.AddRange(
                new MessageEntity
                {
                    Id = newestId,
                    ConversationId = conversationId,
                    AuthorId = userId,
                    Body = "newest",
                    Kind = "message",
                    CreatedAt = now
                },
                new MessageEntity
                {
                    Id = sameTimeHighId,
                    ConversationId = conversationId,
                    AuthorId = userId,
                    Body = "same high",
                    Kind = "sticker",
                    CreatedAt = sameTime
                },
                new MessageEntity
                {
                    Id = sameTimeLowId,
                    ConversationId = conversationId,
                    AuthorId = userId,
                    Body = "same low",
                    Kind = "gif",
                    CreatedAt = sameTime
                },
                new MessageEntity
                {
                    Id = deletedId,
                    ConversationId = conversationId,
                    AuthorId = userId,
                    Body = string.Empty,
                    Kind = "system",
                    CreatedAt = now.AddMinutes(-2),
                    DeletedAt = now.AddMinutes(-1)
                });
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "history-user", "History User"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage firstResponse = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=2");
        var firstPage = JsonSerializer.Deserialize<MessagePageDto>(
            await firstResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPage, Is.Not.Null);
        Assert.That(firstPage!.Items.Select(item => item.Id), Is.EqualTo(new[] { newestId, sameTimeHighId }));
        Assert.That(firstPage!.Items[1].CreatedAt, Is.EqualTo(sameTime));
        Assert.That(firstPage.NextBefore, Is.Not.Null.And.Not.Empty);

        using HttpResponseMessage secondResponse = await client.GetAsync(
            $"/conversations/{conversationId}/messages?limit=2&before={Uri.EscapeDataString(firstPage.NextBefore!)}");
        var secondPage = JsonSerializer.Deserialize<MessagePageDto>(
            await secondResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(secondPage, Is.Not.Null);
        Assert.That(secondPage!.Items.Select(item => item.Id), Is.EqualTo(new[] { sameTimeLowId, deletedId }));
        Assert.That(secondPage.Items[1].Body, Is.Empty);
        Assert.That(secondPage.Items[1].DeletedAt, Is.Not.Null);
        Assert.That(secondPage.NextBefore, Is.Null);
    }

    [Test]
    public async Task Authenticated_member_can_send_and_persist_canonical_message()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset seedTime = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "sender",
                DisplayName = "Sender",
                CreatedAt = seedTime,
                UpdatedAt = seedTime
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = seedTime,
                Participants =
                {
                    new ParticipantEntity { UserId = userId, JoinedAt = seedTime }
                }
            });
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "sender", "Token Name"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        DateTimeOffset requestStarted = DateTimeOffset.UtcNow;

        using HttpResponseMessage response = await client.PostAsync(
            $"/conversations/{conversationId}/messages",
            new StringContent("{\"body\":\"hello\",\"kind\":\"message\"}", Encoding.UTF8, "application/json"));
        var message = JsonSerializer.Deserialize<MessageDto>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(message, Is.Not.Null);
        Assert.That(message!.ConversationId, Is.EqualTo(conversationId));
        Assert.That(message.AuthorId, Is.EqualTo(userId));
        Assert.That(message.Body, Is.EqualTo("hello"));
        Assert.That(message.Kind, Is.EqualTo("message"));
        Assert.That(message.RefPayloadJson, Is.Null);
        Assert.That(message.EditedAt, Is.Null);
        Assert.That(message.DeletedAt, Is.Null);
        Assert.That(message.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(message.CreatedAt, Is.GreaterThanOrEqualTo(requestStarted));
        Assert.That(message.CreatedAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ChatDb db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        MessageEntity persisted = await db.Messages.SingleAsync(row => row.Id == message.Id);
        Assert.That(persisted.ConversationId, Is.EqualTo(conversationId));
        Assert.That(persisted.AuthorId, Is.EqualTo(userId));
        Assert.That(persisted.Body, Is.EqualTo("hello"));
        Assert.That(persisted.Kind, Is.EqualTo("message"));
        Assert.That(persisted.RefPayloadJson, Is.Null);
        Assert.That(persisted.EditedAt, Is.Null);
        Assert.That(persisted.DeletedAt, Is.Null);
        Assert.That(persisted.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task Authenticated_user_gets_only_member_conversations_in_deterministic_order()
    {
        using var factory = new ApiWebApplicationFactory();
        var userId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var oldestId = Guid.NewGuid();
        var notMineId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "user-1",
                DisplayName = "Database Name",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.AddRange(
                new ConversationEntity
                {
                    Id = oldestId,
                    Kind = "dm",
                    Title = null,
                    CreatedAt = now.AddMinutes(-10),
                    Participants =
                    {
                        new ParticipantEntity { UserId = userId, JoinedAt = now.AddMinutes(-10) }
                    }
                },
                new ConversationEntity
                {
                    Id = newestId,
                    Kind = "group",
                    Title = "Team",
                    CreatedAt = now,
                    Participants =
                    {
                        new ParticipantEntity { UserId = userId, JoinedAt = now }
                    }
                },
                new ConversationEntity
                {
                    Id = notMineId,
                    Kind = "server_channel",
                    Title = "Not mine",
                    CreatedAt = now.AddMinutes(1)
                });
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", "user-1", "Token Name"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/conversations");
        string json = await response.Content.ReadAsStringAsync();
        var conversations = JsonSerializer.Deserialize<List<ConversationDto>>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(conversations, Is.Not.Null);
        List<ConversationDto> actual = conversations!;
        Assert.That(actual.Select(conversation => conversation.Id), Is.EqualTo(new[] { newestId, oldestId }));
        Assert.That(actual[0].Kind, Is.EqualTo("group"));
        Assert.That(actual[0].Title, Is.EqualTo("Team"));
        Assert.That(actual[1].Title, Is.Null);
        Assert.That(json, Does.Contain("createdAt"));
        Assert.That(json, Does.Not.Contain("Participants"));
    }

    private static async Task<(Guid ConversationId, DateTimeOffset Now)> SeedConversationWithMessagesAsync(
        ApiWebApplicationFactory factory,
        string providerUserId,
        int messageCount)
    {
        Guid userId = Guid.NewGuid();
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = providerUserId,
                DisplayName = providerUserId,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Kind = "dm",
                CreatedAt = now,
                Participants = { new ParticipantEntity { UserId = userId, JoinedAt = now } }
            });
            for (int index = 0; index < messageCount; index++)
            {
                db.Messages.Add(new MessageEntity
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    AuthorId = userId,
                    Body = $"message-{index}",
                    Kind = "message",
                    CreatedAt = now.AddMinutes(-index)
                });
            }

            return Task.CompletedTask;
        });

        return (conversationId, now);
    }

    private static string DecodeCursor(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string EncodeCursorJson(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static HttpClient CreateAuthorizedClient(
        ApiWebApplicationFactory factory,
        string providerUserId)
    {
        HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", providerUserId, "Token Name"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
