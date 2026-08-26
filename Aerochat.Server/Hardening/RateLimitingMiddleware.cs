using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;

namespace Aerochat.Server.Hardening;

public sealed class RateLimitingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        FixedWindowRateLimiter limiter,
        SessionService sessions,
        IExternalUserStore externalUsers)
    {
        if (!IsProtectedRequest(context))
        {
            await next(context);
            return;
        }

        ExternalUser? user = await ConversationAuth.TryGetCurrentUserAsync(
            context, sessions, externalUsers, context.RequestAborted);
        if (user is null)
        {
            await next(context);
            return;
        }

        RateLimitDecision decision = limiter.TryAcquire(user.Id.ToString("N"));
        if (!decision.Allowed)
        {
            int retryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] =
                retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Results.Json(new Aerochat.Server.Rest.ErrorDto("rate_limited"))
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsProtectedRequest(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            && !(HttpMethods.IsGet(context.Request.Method)
                && context.Request.Path.Equals("/gifs/search")))
        {
            return false;
        }

        string[] parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return (HttpMethods.IsGet(context.Request.Method)
                && parts is ["gifs", "search"])
            || (HttpMethods.IsPost(context.Request.Method)
                && parts.Length == 3
                && parts[0] == "conversations"
                && parts[2] == "messages")
            || (HttpMethods.IsPost(context.Request.Method)
                && parts.Length == 4
                && parts[0] == "conversations"
                && parts[2] == "call");
    }
}
