using Aerochat.Server.Gateway;

namespace Aerochat.Server.Calls;

internal sealed class ConnectedCallCleanupService(
    CallRegistry registry,
    GatewayHub gateway,
    TimeProvider clock) : BackgroundService
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
