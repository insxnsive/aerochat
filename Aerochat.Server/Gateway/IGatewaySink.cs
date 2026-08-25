namespace Aerochat.Server.Gateway;

public enum GatewayAbortReason
{
    Disconnected,
    Replaced,
    Overloaded,
    FrameTooLarge,
    PolicyViolation,
    ServerRestarted,
    Unexpected,
    Closed
}

public interface IGatewaySink
{
    string ConnectionId { get; }
    Guid UserId { get; }
    CancellationToken Disconnected { get; }
    bool TryEnqueue(GatewayEnvelope envelope);
    GatewayAbortReason? EnqueueFailureReason => null;
    void Abort(GatewayAbortReason reason);
    void Complete();
}
