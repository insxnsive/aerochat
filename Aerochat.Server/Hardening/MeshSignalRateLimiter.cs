namespace Aerochat.Server.Hardening;

// Separate from the general call/message budget. Three peer edges can gather ICE
// concurrently; the per-edge lifetime cap supplies the tighter candidate bound.
public sealed class MeshSignalRateLimiter(TimeProvider clock)
{
    private readonly FixedWindowRateLimiter _limiter=new(600,TimeSpan.FromSeconds(60),clock);
    public RateLimitDecision TryAcquire(string user)=>_limiter.TryAcquire(user);
}
