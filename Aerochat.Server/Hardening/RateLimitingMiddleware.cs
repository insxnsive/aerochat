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
        string[] parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return (HttpMethods.IsGet(context.Request.Method)
                && parts.Length == 2
                && string.Equals(parts[0], "gifs", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1], "search", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsPost(context.Request.Method)
                && parts.Length == 3
                && string.Equals(parts[0], "conversations", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[2], "messages", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsPost(context.Request.Method)
                && parts.Length == 4
                && string.Equals(parts[0], "conversations", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[2], "call", StringComparison.OrdinalIgnoreCase));
    }
}
