using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Rest;

namespace Aerochat.Server.Gifs;

public static class GifEndpoints
{
    private static readonly HashSet<string> AllowedContentFilters =
    [
        "off",
        "low",
        "medium",
        "high"
    ];

    public static void MapGifEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/gifs/search", SearchAsync);
    }

    private static async Task<IResult> SearchAsync(
        HttpContext httpContext,
        TenorProxyService tenor,
        IExternalUserStore externalUsers,
        SessionService sessions,
        string? q,
        string? contentfilter,
        CancellationToken cancellationToken)
    {
        if (await ConversationAuth.TryGetCurrentUserAsync(
                httpContext,
                sessions,
                externalUsers,
                cancellationToken) is null)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return ConversationAuth.InvalidRequest();
        }

        string filter = string.IsNullOrWhiteSpace(contentfilter)
            ? TenorProxyService.DefaultContentFilter
            : contentfilter.Trim().ToLowerInvariant();
        if (!AllowedContentFilters.Contains(filter))
        {
            return ConversationAuth.InvalidRequest();
        }

        TenorSearchResult result = await tenor.SearchAsync(q.Trim(), filter, cancellationToken);
        return result.ErrorCode switch
        {
            "gif_unavailable" => Results.Json(
                new ErrorDto(result.ErrorCode),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            "gif_upstream_failed" => Results.Json(
                new ErrorDto(result.ErrorCode),
                statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Ok(result.Items)
        };
    }
}
