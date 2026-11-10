using System.Net;

namespace Aerochat.Server.Hardening;

public sealed class OAuthStartRateLimiter
{
    public const int PerIpLimit = 5;
    public const int GlobalLimit = 100;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private const string UnknownAddressKey = "unknown";

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perAddressCounts = new(StringComparer.Ordinal);
    private bool _windowInitialized;
    private DateTimeOffset _windowStart;
    private int _globalCount;

    public OAuthStartRateLimiter(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public RateLimitDecision TryAcquire(IPAddress? address)
    {
        string key = KeyFor(address);
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_windowInitialized || now >= _windowStart + Window)
            {
                _windowStart = now;
                _windowInitialized = true;
                _globalCount = 0;
                _perAddressCounts.Clear();
            }

            int addressCount = _perAddressCounts.TryGetValue(key, out int counted)
                ? counted
                : 0;
            if (addressCount >= PerIpLimit || _globalCount >= GlobalLimit)
            {
                return new RateLimitDecision(false, _windowStart + Window - now);
            }

            _perAddressCounts[key] = addressCount + 1;
            _globalCount++;
            return new RateLimitDecision(true, TimeSpan.Zero);
        }
    }

    private static string KeyFor(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownAddressKey;
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }
}
