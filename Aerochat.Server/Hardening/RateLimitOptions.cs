namespace Aerochat.Server.Hardening;

public sealed class RateLimitOptions
{
    public int Limit { get; init; } = 30;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}
