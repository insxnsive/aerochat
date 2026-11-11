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
            // A slow request holds only a small pooled read buffer. The replay buffer
            // grows with decoded bytes actually received, never with the configured cap.
            using var bounded = new MemoryStream();
            byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(8 * 1024);
            try
            {
                int total = 0;
                while (total <= limit)
                {
                    int count = await original.ReadAsync(chunk.AsMemory(0,
                        Math.Min(8 * 1024, limit + 1 - total)), context.RequestAborted);
                    if (count == 0) break;
                    total += count;
                    if (total > limit)
                    {
                        await RejectAsync(context);
                        return;
                    }
                    bounded.Write(chunk, 0, count);
                }
                bounded.Position = 0;
                context.Request.Body = bounded;
                await next(context);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunk, clearArray: true); }
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
