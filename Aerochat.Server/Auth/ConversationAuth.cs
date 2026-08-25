using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Rest;

namespace Aerochat.Server.Auth;

public static class ConversationAuth
{
    public static async Task<ExternalUser?> TryGetCurrentUserAsync(
        HttpContext httpContext,
        SessionService sessions,
        IExternalUserStore externalUsers,
        CancellationToken cancellationToken,
        string? tokenOverride = null)
    {
        string token;
        if (tokenOverride is not null)
        {
            token = tokenOverride;
        }
        else
        {
            string authorization = httpContext.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            token = authorization["Bearer ".Length..].Trim();
        }

        if (token.Length == 0)
        {
            return null;
        }

        SessionClaims? claims = sessions.Validate(token);
        if (claims is null)
        {
            return null;
        }

        return await externalUsers.FindAsync(claims.Provider, claims.ProviderUserId, cancellationToken);
    }

    public static IResult Unauthorized(HttpContext httpContext)
    {
        httpContext.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(new ErrorDto("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
    }

    public static IResult Forbidden() =>
        Results.Json(new ErrorDto("forbidden"), statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound() =>
        Results.Json(new ErrorDto("not_found"), statusCode: StatusCodes.Status404NotFound);

    public static IResult InvalidRequest() =>
        Results.Json(new ErrorDto("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
}
