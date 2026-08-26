namespace Aerochat.Server.Hardening;

public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);

public sealed class FixedWindowRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public FixedWindowRateLimiter(int limit, TimeSpan window, TimeProvider clock)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _limit = limit;
        _window = window;
        _clock = clock;
    }

    public RateLimitDecision TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out Window? window)
                || now >= window!.Start + _window)
            {
                _windows[key] = window = new Window(now);
            }

            if (window.Count < _limit)
            {
                window.Count++;
                return new RateLimitDecision(true, TimeSpan.Zero);
            }

            return new RateLimitDecision(false, window.Start + _window - now);
        }
    }

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;
        public int Count { get; set; }
    }
}
