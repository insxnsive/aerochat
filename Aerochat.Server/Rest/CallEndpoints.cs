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
        endpoints.MapPost("/conversations/{conversationId}/call/screen/start", ScreenStartAsync);
        endpoints.MapPost("/conversations/{conversationId}/call/screen/stop", ScreenStopAsync);
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

    private static Task<IResult> ScreenStartAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.ScreenStart, GatewayEventType.CallScreenStarted, conversationId, httpContext, request,
            db, externalUsers, sessions, registry, gateway, cancellationToken);

    private static Task<IResult> ScreenStopAsync(
        string conversationId, HttpContext httpContext, CallSignalRequest? request,
        ChatDb db, IExternalUserStore externalUsers, SessionService sessions,
        CallRegistry registry, GatewayHub gateway, CancellationToken cancellationToken) =>
        ApplyAsync(CallAction.ScreenStop, GatewayEventType.CallScreenStopped, conversationId, httpContext, request,
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

        List<Guid> members = await db.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationGuid)
            .Select(participant => participant.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (!members.Contains(user.Id))
        {
            return ConversationAuth.Forbidden();
        }

        string? kind = await db.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationGuid)
            .Select(conversation => conversation.Kind)
            .SingleOrDefaultAsync(cancellationToken);
        if (kind != "dm" || members.Count != 2)
        {
            return Results.Json(
                new ErrorDto("call_invalid_conversation"),
                statusCode: StatusCodes.Status409Conflict);
        }

        Guid otherMemberId = members[0] == user.Id ? members[1] : members[0];
        Guid recipientId;
        if (action == CallAction.Ring)
        {
            if (!registry.TryStart(conversationGuid, user.Id, otherMemberId, out _))
            {
                return Results.Json(
                    new ErrorDto("call_invalid_state"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            recipientId = otherMemberId;
        }
        else
        {
            CallApplyResult result = registry.TryApply(
                conversationGuid, user.Id, members[0], members[1], action, out recipientId);
            if (result == CallApplyResult.Forbidden)
            {
                return ConversationAuth.Forbidden();
            }

            if (result != CallApplyResult.Applied)
            {
                return Results.Json(
                    new ErrorDto("call_invalid_state"),
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        gateway.Publish(
            eventType,
            new CallSignalData(conversationGuid, request.Sdp, request.Candidate, request.Reason),
            [recipientId]);

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
