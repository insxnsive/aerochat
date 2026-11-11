using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aerochat.Server.Hardening;

namespace Aerochat.Server.Turn;

public sealed class TurnSettings
{
    private readonly byte[] _secret;
    private TurnSettings(string[] urls, string secret, int ttl, int limit, int window)
    {
        Urls = Array.AsReadOnly(urls);
        _secret = Encoding.UTF8.GetBytes(secret);
        CredentialTtlSeconds = ttl;
        RateLimit = limit;
        WindowSeconds = window;
    }
    public IReadOnlyList<string> Urls { get; }
    public int CredentialTtlSeconds { get; }
    public int RateLimit { get; }
    public int WindowSeconds { get; }
    internal ReadOnlySpan<byte> Secret => _secret;

    public static TurnSettings FromConfiguration(IConfiguration configuration)
    {
        // Never use conversion exceptions or include configured values in an error.
        static InvalidOperationException Invalid() => new("Invalid TURN configuration.");
        static int Number(IConfiguration config, string key, int fallback, int minimum, int maximum)
        {
            string? raw = config[key];
            if (raw is null) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum) throw Invalid();
            return value;
        }
        int ttl = Number(configuration, "Turn:CredentialTtlSeconds", 3600, 60, 86400);
        int limit = Number(configuration, "Turn:RateLimit:Limit", 6, 1, 1000);
        int window = Number(configuration, "Turn:RateLimit:WindowSeconds", 60, 1, 86400);
        IConfigurationSection section = configuration.GetSection("Turn:Urls");
        if (!string.IsNullOrEmpty(section.Value)) throw Invalid();
        string[] urls = section.GetChildren().Select(child => child.Value ?? string.Empty).ToArray();
        string? secret = configuration["Turn:SharedSecret"];
        if (urls.Length == 0 && secret is null) return new([], string.Empty, ttl, limit, window);
        if (urls.Length == 0 || string.IsNullOrWhiteSpace(secret)
            || Encoding.UTF8.GetByteCount(secret) < 32 || secret.Any(char.IsControl)
            || secret.IndexOfAny(['<', '>']) >= 0
            || new[] { "placeholder", "example", "changeme", "change-me", "change_me" }
                .Any(marker => secret.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || string.Equals(secret, configuration["Auth:SessionSigningKey"], StringComparison.Ordinal)) throw Invalid();
        foreach (string url in urls)
        {
            if (!IsTurnUrl(url)) throw Invalid();
        }
        return new(urls, secret, ttl, limit, window);
    }

    private static bool IsTurnUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "turn" && uri.Scheme != "turns")) return false;
        Match match = Regex.Match(value,
            @"\Aturns?:(?<host>\[[0-9a-f:.]+\]|[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)(?::(?<port>[0-9]{1,5}))?(?:\?transport=(?:udp|tcp))?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return false;
        string host = match.Groups["host"].Value;
        if (host.StartsWith('['))
        {
            if (!IPAddress.TryParse(host[1..^1], out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        }
        else if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        return !match.Groups["port"].Success
            || (int.TryParse(match.Groups["port"].Value, out int port) && port is >= 1 and <= 65535);
    }
}

public sealed record TurnIceServer(string[] Urls, string Username, string Credential);
public sealed record TurnCredentialsResponse(TurnIceServer[] IceServers, int Ttl);

public sealed class TurnCredentialService
{
    private readonly TurnSettings _settings;
    private readonly TimeProvider _clock;
    private readonly FixedWindowRateLimiter _limiter;
    public TurnCredentialService(TurnSettings settings, TimeProvider clock)
    {
        _settings = settings;
        _clock = clock;
        _limiter = new(settings.RateLimit, TimeSpan.FromSeconds(settings.WindowSeconds), clock);
    }
    public RateLimitDecision TryAcquire(Guid userId) => _limiter.TryAcquire(userId.ToString("N"));
    public TurnCredentialsResponse Issue(Guid userId)
    {
        if (_settings.Urls.Count == 0) return new([], 0);
        long expiry = checked(_clock.GetUtcNow().ToUnixTimeSeconds() + _settings.CredentialTtlSeconds);
        string username = expiry.ToString(CultureInfo.InvariantCulture) + ":" + userId.ToString("N");
        string credential = Convert.ToBase64String(HMACSHA1.HashData(_settings.Secret, Encoding.UTF8.GetBytes(username)));
        return new([new(_settings.Urls.ToArray(), username, credential)], _settings.CredentialTtlSeconds);
    }
}
