using System.Text;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Calls;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

public static class CallEndpoints
{
    private const int MaxPayloadBytes = 64 * 1024;

    public static void MapCallEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/conversations/{conversationId}/call/ring", RingAsync);
        endpoints.MapPost("/conversations/{conversationId}/call/offer", OfferAsync);
        endpoints.MapPost("/conversations/{conversationId}/call/answer", AnswerAsync);
        endpoints.MapPost("/conversations/{conversationId}/call/ice", IceAsync);
        endpoints.MapPost("/conversations/{conversationId}/call/hangup", HangupAsync);
    }

    private static Task<IResult> RingAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.Ring, GatewayEventType.CallRing, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static Task<IResult> OfferAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.Offer, GatewayEventType.CallOffer, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static Task<IResult> AnswerAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.Answer, GatewayEventType.CallAnswer, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static Task<IResult> IceAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.Ice, GatewayEventType.CallIce, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static Task<IResult> HangupAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.Hangup, GatewayEventType.CallHangup, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static async Task<IResult> ApplyAsync(
        CallAction action,
        string eventType,
        string conversationId,
        HttpContext httpContext,
        CallSignalRequest? request,
        ChatDb db,
        IExternalUserStore externalUsers,
        SessionService sessions,
        CallRegistry registry,
        GatewayHub gateway,
        CancellationToken cancellationToken)
    {
        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(
            httpContext, sessions, externalUsers, cancellationToken);
        if (user is null)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        if (!Guid.TryParse(conversationId, out Guid conversationGuid)
            || request is null
            || HasOversizedPayload(request)
            || !HasRequiredPayload(action, request))
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

        bool applied = action == CallAction.Ring
            ? registry.TryStart(conversationGuid, out _)
            : registry.TryApply(conversationGuid, action);
        if (!applied)
        {
            return Results.Json(new ErrorDto("call_invalid_state"), statusCode: StatusCodes.Status409Conflict);
        }

        List<Guid> audience = await db.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationGuid)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        gateway.Publish(
            eventType,
            new CallSignalData(conversationGuid, request.Sdp, request.Candidate, request.Reason),
            audience);

        return Results.Ok(new { conversationId = conversationGuid });
    }

    private static bool HasRequiredPayload(CallAction action, CallSignalRequest request) =>
        action is not (CallAction.Offer or CallAction.Answer or CallAction.Ice)
        || (action == CallAction.Ice
            ? !string.IsNullOrWhiteSpace(request.Candidate)
            : !string.IsNullOrWhiteSpace(request.Sdp));

    private static bool HasOversizedPayload(CallSignalRequest request) =>
        IsOversized(request.Sdp) || IsOversized(request.Candidate) || IsOversized(request.Reason);

    private static bool IsOversized(string? value) =>
        value is not null && Encoding.UTF8.GetByteCount(value) > MaxPayloadBytes;
}
