using Aerochat.Server.Gateway;

namespace Aerochat.Server.Calls;

internal sealed class ConnectedCallCleanupService(
    CallRegistry registry,
    GatewayHub gateway,
    TimeProvider clock,
    ILogger<ConnectedCallCleanupService> logger) : BackgroundService
{
    internal void Sweep()
    {
        IReadOnlyList<CallSession> ended = gateway.WithPresenceLock(registry.CollectDisconnectedCalls);
        foreach (CallSession session in ended)
        {
            gateway.Publish(
                GatewayEventType.CallHangup,
                new CallSignalData(session.ConversationId, null, null, "peer_disconnected", session.CallId),
                new[] { session.CallerId, session.CalleeId });
        }
    }

    internal async Task RunLoopAsync(
        Func<CancellationToken, ValueTask<bool>> waitForTick,
        Action sweep,
        CancellationToken stoppingToken)
    {
        try
        {
            while (await waitForTick(stoppingToken))
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    sweep();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    logger.LogWarning(
                        new EventId(4101, "ConnectedCallCleanupSweepFailed"),
                        "Connected call cleanup sweep failed; continuing on the next scheduled tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        await RunLoopAsync(timer.WaitForNextTickAsync, Sweep, stoppingToken);
    }
}
