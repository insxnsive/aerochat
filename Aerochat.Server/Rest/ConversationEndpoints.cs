using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

public static class ConversationEndpoints
{
    private static readonly HashSet<string> MessageKinds =
    [
        "message",
        "sticker",
        "gif",
        "system"
    ];

    public static void MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/conversations", GetConversationsAsync);
        endpoints.MapGet("/conversations/{conversationId}/messages", GetMessagesAsync);
        endpoints.MapPost("/conversations/{conversationId}/messages", SendMessageAsync);
    }

    private static async Task<IResult> GetConversationsAsync(
        HttpContext httpContext,
        ChatDb db,
        IExternalUserStore externalUsers,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(
            httpContext,
            sessions,
            externalUsers,
            cancellationToken);
        if (user is null)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Participants.Any(participant => participant.UserId == user.Id))
            .OrderByDescending(conversation => conversation.CreatedAt)
            .ThenByDescending(conversation => conversation.Id)
            .Select(conversation => new ConversationDto(
                conversation.Id,
                conversation.Kind,
                conversation.Title,
                conversation.CreatedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(conversations);
    }
    private static async Task<IResult> GetMessagesAsync(
        string conversationId,
        HttpContext httpContext,
        ChatDb db,
        IExternalUserStore externalUsers,
        SessionService sessions,
        string? before,
        string? limit,
        CancellationToken cancellationToken)
    {
        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(
            httpContext,
            sessions,
            externalUsers,
            cancellationToken);
        if (user is null)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        if (!Guid.TryParse(conversationId, out Guid conversationGuid)
            || !TryParseLimit(limit, out int pageSize))
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
            .AnyAsync(
                participant => participant.ConversationId == conversationGuid && participant.UserId == user.Id,
                cancellationToken);
        if (!isMember)
        {
            return ConversationAuth.Forbidden();
        }

        MessageCursor? cursor = null;
        if (before is not null)
        {
            if (!MessageCursorCodec.TryDecode(before, out MessageCursor decoded)
                || decoded.ConversationId != conversationGuid)
            {
                return ConversationAuth.InvalidRequest();
            }

            cursor = decoded;
        }

        IQueryable<MessageEntity> query = db.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationGuid);
        if (cursor is not null)
        {
            query = query.Where(message =>
                message.CreatedAt < cursor.CreatedAt
                || (message.CreatedAt == cursor.CreatedAt && message.Id.CompareTo(cursor.Id) < 0));
        }

        bool canLookAhead = pageSize < int.MaxValue;
        int takeCount = canLookAhead ? pageSize + 1 : pageSize;
        List<MessageDto> rows = await query
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(takeCount)
            .Select(message => new MessageDto(
                message.Id,
                message.ConversationId,
                message.AuthorId,
                message.Body,
                message.Kind,
                message.RefPayloadJson,
                message.CreatedAt,
                message.EditedAt,
                message.DeletedAt))
            .ToListAsync(cancellationToken);

        bool hasMore = canLookAhead && rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        string? nextBefore = hasMore && rows.Count > 0
            ? MessageCursorCodec.Encode(conversationGuid, rows[^1].CreatedAt, rows[^1].Id)
            : null;
        return Results.Ok(new MessagePageDto(rows, nextBefore));
    }

    private static Task<IResult> SendMessageAsync(
        string conversationId,
        HttpContext httpContext,
        ConversationMessageService messages,
        SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        return messages.SendAsync(httpContext, conversationId, request, cancellationToken);
    }

    private static bool TryParseLimit(string? value, out int limit)
    {
        if (value is null)
        {
            limit = 50;
            return true;
        }

        return int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out limit)
            && limit > 0;
    }
}
