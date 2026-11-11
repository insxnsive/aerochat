using System.Globalization;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Turn;

namespace Aerochat.Server.Rest;

public static class TurnEndpoints
{
    public static void MapTurnEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/turn/credentials", HandleAsync);

    private static async Task<IResult> HandleAsync(HttpContext context, SessionService sessions,
        IExternalUserStore externalUsers, TurnCredentialService turn, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(context, sessions, externalUsers, cancellationToken);
        if (user is null) return ConversationAuth.Unauthorized(context);
        var decision = turn.TryAcquire(user.Id);
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return Results.Json(new ErrorDto("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests);
        }
        return Results.Ok(turn.Issue(user.Id));
    }
}
