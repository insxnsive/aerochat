using Microsoft.AspNetCore.Http.Features;

namespace Aerochat.Server.Hardening;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public const int GlobalLimit = 2 * 1024 * 1024;
    public const int AuthLimit = 16 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        int limit = context.Request.Path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            ? AuthLimit : GlobalLimit;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit + 64 * 1024;
        if (context.Request.ContentLength > limit)
        {
            await RejectAsync(context);
            return;
        }
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            await next(context);
            return;
        }

        Stream original = context.Request.Body;
        try
        {
            byte[] bytes = new byte[limit + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int count = await original.ReadAsync(bytes.AsMemory(total), context.RequestAborted);
                if (count == 0) break;
                total += count;
            }
            if (total > limit)
            {
                await RejectAsync(context);
                return;
            }
            using var bounded = new MemoryStream(bytes, 0, total, writable: false);
            context.Request.Body = bounded;
            await next(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge
            && !context.Response.HasStarted)
        {
            await RejectAsync(context);
        }
        finally { context.Request.Body = original; }
    }

    private static Task RejectAsync(HttpContext context) =>
        Results.Json(new { error = "request_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge)
            .ExecuteAsync(context);
}
