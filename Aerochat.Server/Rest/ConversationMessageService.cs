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

    private static readonly HashSet<string> StickerPayloadProperties =
    ["sticker", "url", "contentType"];

    private static readonly HashSet<string> InstalledStickerResourceNames = new(StringComparer.Ordinal)
    {
        "Smile.png",
        "Heart.png",
        "Grin.png",
        "Wink.png",
        "ThumbsUp.png",
        "Dog.png",
        "Football.png",
        "HighFive.png"
    };

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

        if (request.RefPayloadJson is not null
            && request.RefPayloadJson.Length > MessageRequestValidator.MaxRefPayloadJsonCharacters)
        {
            return Results.Json(
                new ErrorDto("ref_payload_too_long"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Body)
            || request.Kind is null
            || !MessageKinds.Contains(request.Kind))
        {
            return ConversationAuth.InvalidRequest();
        }

        if (request.Kind == "sticker" && !IsValidStickerPayload(request.Body, request.RefPayloadJson))
        {
            return ConversationAuth.InvalidRequest();
        }

        if (!TryParseRefPayload(request.RefPayloadJson, out object? refPayload))
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
            RefPayloadJson = request.RefPayloadJson,
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

    private static bool IsValidStickerPayload(string body, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonProperty[] properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (properties.Length != StickerPayloadProperties.Count
                || properties.Any(property => !StickerPayloadProperties.Contains(property.Name))
                || !TryGetString(root, "sticker", out string? sticker)
                || !TryGetString(root, "url", out string? url)
                || !TryGetString(root, "contentType", out string? contentType))
            {
                return false;
            }

            return InstalledStickerResourceNames.Contains(sticker!)
                && string.Equals(body, sticker, StringComparison.Ordinal)
                && string.Equals(url, $"/sticker-packs/wlm/{sticker}", StringComparison.Ordinal)
                && string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseRefPayload(string? json, out object? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        return root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }
}
