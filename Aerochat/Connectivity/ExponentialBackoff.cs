namespace Aerochat.Connectivity;

public static class ExponentialBackoff
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    public static TimeSpan GetDelay(
        int attempt,
        Func<int, TimeSpan>? jitter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        TimeSpan baseDelay = attempt >= 5
            ? MaximumDelay
            : TimeSpan.FromSeconds(1L << attempt);

        if (jitter is null)
            return baseDelay;

        long ticks = checked(baseDelay.Ticks + jitter(attempt).Ticks);
        return TimeSpan.FromTicks(Math.Max(0, ticks));
    }
}
