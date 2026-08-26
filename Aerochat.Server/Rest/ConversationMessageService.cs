using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gateway;
using Aerochat.Server.Hardening;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

public sealed class ConversationMessageService(
    ChatDb db,
    IExternalUserStore externalUsers,
    SessionService sessions,
    TimeProvider clock,
    GatewayHub gateway)
{
    private static readonly HashSet<string> MessageKinds =
    ["message", "sticker", "gif", "system"];

    public async Task<IResult> SendAsync(
        HttpContext httpContext,
        string conversationId,
        SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(
            httpContext, sessions, externalUsers, cancellationToken);
        if (user is null)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        if (!Guid.TryParse(conversationId, out Guid conversationGuid) || request is null)
        {
            return ConversationAuth.InvalidRequest();
        }

        if (request.Body is not null && !MessageRequestValidator.IsBodyWithinLimit(request.Body))
        {
            return Results.Json(new ErrorDto("body_too_long"), statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Body)
            || request.Kind is null
            || !MessageKinds.Contains(request.Kind))
        {
            return ConversationAuth.InvalidRequest();
        }

        bool exists = await db.Conversations
            .AsNoTracking()
            .AnyAsync(conversation => conversation.Id == conversationGuid, cancellationToken);
        if (!exists)
        {
            return ConversationAuth.NotFound();
        }

        bool isMember = await db.Participants
            .AsNoTracking()
            .AnyAsync(participant =>
                participant.ConversationId == conversationGuid && participant.UserId == user.Id,
                cancellationToken);
        if (!isMember)
        {
            return ConversationAuth.Forbidden();
        }

        MessageEntity message = new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationGuid,
            AuthorId = user.Id,
            Body = request.Body,
            Kind = request.Kind,
            RefPayloadJson = null,
            CreatedAt = clock.GetUtcNow().ToUniversalTime()
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        List<Guid> audience = await db.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationGuid)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        object? refPayload = ParseRefPayload(message.RefPayloadJson);
        gateway.Publish(
            GatewayEventType.MessageCreated,
            new MessageCreatedData(
                message.ConversationId,
                new GatewayMessageData(
                    message.Id,
                    message.ConversationId,
                    message.AuthorId,
                    message.Body,
                    message.Kind,
                    refPayload,
                    message.CreatedAt,
                    message.EditedAt,
                    message.DeletedAt)),
            audience);

        return Results.Created(
            $"/conversations/{conversationGuid}/messages/{message.Id}",
            new MessageDto(
                message.Id,
                message.ConversationId,
                message.AuthorId,
                message.Body,
                message.Kind,
                message.RefPayloadJson,
                message.CreatedAt,
                message.EditedAt,
                message.DeletedAt));
    }

    private static object? ParseRefPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
