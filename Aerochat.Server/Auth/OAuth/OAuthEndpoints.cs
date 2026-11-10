using Aerochat.Server.Hardening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aerochat.Server.Auth.OAuth;

public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/auth/{provider}/start",
            (
                string provider,
                string? returnUri,
                OAuthFlowService flow,
                HttpContext context,
                OAuthStartRateLimiter admission) =>
            {
                RateLimitDecision decision = admission.TryAcquire(
                    context.Connection.RemoteIpAddress);
                if (!decision.Allowed)
                {
                    int retryAfter = Math.Max(
                        1,
                        (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
                    context.Response.Headers["Retry-After"] =
                        retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(
                        new { error = "rate_limited" },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                try
                {
                    OAuthStartResult result = flow.Start(provider, returnUri ?? string.Empty);
                    return Results.Redirect(result.AuthorizationUri.AbsoluteUri);
                }
                catch (OAuthFlowException exception)
                {
                    return Error(exception);
                }
            });

        endpoints.MapGet(
            "/auth/{provider}/callback",
            async (
                string provider,
                string? code,
                string? state,
                OAuthFlowService flow,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    OAuthCompletionResult result = await flow.CompleteAsync(
                        provider,
                        code ?? string.Empty,
                        state ?? string.Empty,
                        cancellationToken);
                    return Results.Redirect(result.RedirectUri.AbsoluteUri);
                }
                catch (OAuthFlowException exception)
                {
                    return Error(exception);
                }
            });

        endpoints.MapPost(
            "/auth/session/exchange",
            (OAuthSessionExchangeRequest? request, OAuthFlowService flow) =>
            {
                try
                {
                    OAuthSessionExchangeResult result = flow.ExchangeHandoff(request?.Code ?? string.Empty);
                    return Results.Json(new
                    {
                        accessToken = result.AccessToken,
                        expiresIn = result.ExpiresIn
                    });
                }
                catch (OAuthFlowException exception)
                {
                    return Error(exception);
                }
            });

        return endpoints;
    }

    private static IResult Error(OAuthFlowException exception) =>
        Results.Json(new { error = exception.ErrorCode }, statusCode: exception.StatusCode);
}

public sealed record OAuthSessionExchangeRequest(string? Code);
