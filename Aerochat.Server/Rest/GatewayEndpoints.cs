using System.Net;
using System.Net.WebSockets;
using System.Text;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Gateway;
using Aerochat.Server.Hardening;

namespace Aerochat.Server.Rest;

public static class GatewayEndpoints
{
    public static void MapGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ws", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        SessionService sessions,
        IExternalUserStore externalUsers,
        GatewayHub gateway,
        GatewayOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        string? token = GetExactQueryValue(httpContext.Request.QueryString.Value, "token");
        SessionClaims? claims = string.IsNullOrEmpty(token) ? null : sessions.Validate(token);
        ExternalUser? user = claims is null
            ? null
            : await externalUsers.FindAsync(claims.Provider, claims.ProviderUserId, cancellationToken);

        if (claims is null || user is null || clock.GetUtcNow() >= claims.ExpiresAt)
        {
            return ConversationAuth.Unauthorized(httpContext);
        }

        if (!GatewayOriginPolicy.IsAllowed(httpContext.Request.Headers.Origin, options.AllowedOrigins))
        {
            return Results.Json(new ErrorDto("origin_not_allowed"), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            return ConversationAuth.InvalidRequest();
        }

        string? lastEventId = GetExactQueryValue(httpContext.Request.QueryString.Value, "lastEventId");
        using WebSocket socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        using var connection = new GatewayConnection(Guid.NewGuid().ToString("N"), user.Id, options);

        if (clock.GetUtcNow() >= claims.ExpiresAt)
        {
            connection.Abort(GatewayAbortReason.PolicyViolation);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "expired", CancellationToken.None);
            return Results.Empty;
        }

        GatewayRegistrationResult registration = gateway.Register(connection, lastEventId);

        await ServeSocketAsync(
            httpContext, socket, connection, gateway, registration, claims.ExpiresAt, clock, cancellationToken);
        return Results.Empty;
    }

    private static async Task ServeSocketAsync(
        HttpContext httpContext,
        WebSocket socket,
        GatewayConnection connection,
        GatewayHub gateway,
        GatewayRegistrationResult registration,
        DateTimeOffset expiresAt,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        WebSocketCloseStatus? forcedCloseStatus = registration.Registered
            ? null
            : registration.Status switch
            {
                GatewayReplayStatus.Invalid or GatewayReplayStatus.Future => WebSocketCloseStatus.PolicyViolation,
                GatewayReplayStatus.ServerRestarted => (WebSocketCloseStatus)1012,
                GatewayReplayStatus.Expired => WebSocketCloseStatus.NormalClosure,
                _ => MapCloseStatus(connection.TerminalAbortReason)
            };

        if (!registration.Registered && registration.Status is GatewayReplayStatus.Invalid or GatewayReplayStatus.Future)
        {
            connection.Abort(GatewayAbortReason.PolicyViolation);
        }

        TimeSpan remaining = expiresAt - clock.GetUtcNow();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        using var expiry = new CancellationTokenSource(remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, httpContext.RequestAborted, expiry.Token);
        using CancellationTokenRegistration expiryRegistration = expiry.Token.Register(() =>
        {
            connection.Abort(GatewayAbortReason.PolicyViolation);
            gateway.Remove(connection.ConnectionId, connection);
            try
            {
                socket.Abort();
            }
            catch
            {
                // The socket may already be gone; expiry shutdown stays best-effort.
            }
        });

        Task writer = WriteLoopAsync(socket, connection, forcedCloseStatus, expiresAt, clock, lifetime.Token);
        if (!registration.Registered)
        {
            await writer.ConfigureAwait(false);
            gateway.Remove(connection.ConnectionId, connection);
            return;
        }

        Task reader = ReadLoopAsync(socket, connection, lifetime.Token);
        try
        {
            Task completed = await Task.WhenAny(writer, reader).ConfigureAwait(false);
            if (completed == writer)
            {
                lifetime.Cancel();
            }

            await Task.WhenAll(writer, reader).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            connection.Abort(GatewayAbortReason.Disconnected);
        }
        finally
        {
            lifetime.Cancel();
            gateway.Remove(connection.ConnectionId, connection);
        }
    }

    private static async Task WriteLoopAsync(
        WebSocket socket,
        GatewayConnection connection,
        WebSocketCloseStatus? forcedCloseStatus,
        DateTimeOffset expiresAt,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? frame = await connection.WaitForFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (clock.GetUtcNow() >= expiresAt)
                {
                    connection.Abort(GatewayAbortReason.PolicyViolation);
                    break;
                }

                byte[] payload = Encoding.UTF8.GetBytes(frame);
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested
                && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                WebSocketCloseStatus status = forcedCloseStatus ?? MapCloseStatus(connection.TerminalAbortReason);
                await socket.CloseAsync(status, status.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            connection.Abort(GatewayAbortReason.Unexpected);
        }
        catch
        {
            connection.Abort(GatewayAbortReason.Unexpected);
        }
    }

    private static async Task ReadLoopAsync(
        WebSocket socket,
        GatewayConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    connection.Complete();
                    return;
                }

                connection.Abort(GatewayAbortReason.PolicyViolation);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            connection.Abort(GatewayAbortReason.Unexpected);
        }
    }

    private static WebSocketCloseStatus MapCloseStatus(GatewayAbortReason? reason) => reason switch
    {
        GatewayAbortReason.Overloaded => (WebSocketCloseStatus)1013,
        GatewayAbortReason.FrameTooLarge => WebSocketCloseStatus.MessageTooBig,
        GatewayAbortReason.PolicyViolation => WebSocketCloseStatus.PolicyViolation,
        GatewayAbortReason.ServerRestarted => WebSocketCloseStatus.EndpointUnavailable,
        GatewayAbortReason.Unexpected => WebSocketCloseStatus.InternalServerError,
        _ => WebSocketCloseStatus.NormalClosure
    };

    private static string? GetExactQueryValue(string? queryString, string expectedKey)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return null;
        }

        string? result = null;
        foreach (string part in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split('=', 2);
            string key = WebUtility.UrlDecode(pieces[0]);
            if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (result is not null)
            {
                return null;
            }

            result = pieces.Length == 1 ? string.Empty : WebUtility.UrlDecode(pieces[1]);
        }

        return result;
    }
}
