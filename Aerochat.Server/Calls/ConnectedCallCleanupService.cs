using Aerochat.Server.Gateway;

namespace Aerochat.Server.Calls;

internal sealed class ConnectedCallCleanupService(
    CallRegistry registry,
    GatewayHub gateway,
    TimeProvider clock,
    ILogger<ConnectedCallCleanupService> logger) : BackgroundService
{
    private const int FailureLogInterval = 60;

    internal void Sweep()
    {
        IReadOnlyList<CallSession> ended = gateway.WithPresenceLock(registry.CollectDisconnectedCalls);
        NotifyEnded(ended, PublishHangup);
    }

    internal void NotifyEnded(IReadOnlyList<CallSession> ended, Action<CallSession> publish)
    {
        foreach (CallSession session in ended)
        {
            try
            {
                publish(session);
            }
            catch (Exception)
            {
                logger.LogWarning(
                    new EventId(4102, "ConnectedCallHangupPublishFailed"),
                    "Connected call hangup notification failed; continuing with the remaining calls.");
            }
        }
    }

    private void PublishHangup(CallSession session) =>
        gateway.Publish(
            GatewayEventType.CallHangup,
            new CallSignalData(session.ConversationId, null, null, "peer_disconnected", session.CallId),
            new[] { session.CallerId, session.CalleeId });

    internal async Task RunLoopAsync(
        Func<CancellationToken, ValueTask<bool>> waitForTick,
        Action sweep,
        CancellationToken stoppingToken)
    {
        long consecutiveFailures = 0;
        try
        {
            while (await waitForTick(stoppingToken))
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    sweep();
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    consecutiveFailures++;
                    if (consecutiveFailures == 1 || consecutiveFailures % FailureLogInterval == 0)
                    {
                        logger.LogWarning(
                            new EventId(4101, "ConnectedCallCleanupSweepFailed"),
                            "Connected call cleanup sweep failed ({ExceptionType}, consecutive failures: {ConsecutiveFailures}); continuing on the next scheduled tick.",
                            exception.GetType().Name,
                            consecutiveFailures);
                    }
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
