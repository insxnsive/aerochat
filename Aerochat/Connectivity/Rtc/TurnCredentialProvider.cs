using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Aerochat.Connectivity.Auth;

namespace Aerochat.Connectivity.Rtc;

internal sealed record TurnIceServer(string[] Urls, string Username, string Credential)
{
    public override string ToString() => "TurnIceServer { [redacted] }";
}
internal sealed record TurnResponse(TurnIceServer[] IceServers, int Ttl);

// One instance per app authentication lifetime. No credential is persisted or logged.
internal sealed class TurnCredentialProvider : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _fetch = new(1);
    private readonly object _gate = new();
    private readonly ITimer _timer;
    private TurnResponse? _cached;
    private long _started, _retryStarted;
    private string? _identity;
    private int _leases, _generation;
    private bool _disposed;
    private TimeSpan _retry;
    private int _refreshFailures;
    internal Task RefreshTask { get; private set; } = Task.CompletedTask;

    internal TurnCredentialProvider(HttpClient http, Uri server,
        Func<CancellationToken, Task<string?>> token, TimeProvider? clock = null)
    {
        if (!server.IsAbsoluteUri || (server.Scheme != "https" &&
            !(server.Scheme == "http" && OAuthAuthClient.HasExactNumericLoopbackHost(server))))
            throw new ArgumentException("The TURN server must use HTTPS or numeric loopback HTTP.");
        _http = http; _server = server; _token = token; _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(_ => { lock (_gate) RefreshTask = RefreshAsync(); },
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal async Task<Lease> AcquireAsync(CancellationToken ct = default)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _leases++; }
        try { return new Lease(this, await GetAsync(false, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Release(); throw; }
        catch { Release(); throw Failure(); }
    }

    private async Task<TurnResponse> GetAsync(bool refresh, CancellationToken ct)
    {
        await _fetch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? token = await _token(ct).ConfigureAwait(false);
            int generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_identity != token)
                {
                    _cached = null; _identity = token; _generation++;
                    _retry = TimeSpan.Zero; _refreshFailures = 0;
                }
                if (string.IsNullOrWhiteSpace(token)) { _cached = null; throw Failure(); }
                if (!refresh && _cached is not null &&
                    (_cached.Ttl == 0 || _clock.GetElapsedTime(_started).TotalSeconds < _cached.Ttl))
                    return Copy(_cached);
                if (_clock.GetElapsedTime(_retryStarted) < _retry) throw Failure();
                generation = _generation;
            }
            long start = _clock.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, "turn/credentials"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    TimeSpan retry = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()) ?? TimeSpan.FromSeconds(5);
                    lock (_gate) { _retryStarted = _clock.GetTimestamp(); _retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 300)); }
                }
                throw Failure();
            }
            TurnResponse value;
            try
            {
                value = await BoundedJsonResponse.ReadAsync<TurnResponse>(response, 32 * 1024,
                    deadline.Token).ConfigureAwait(false) ?? throw Failure();
                Validate(value);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or TimeoutException)
            { throw Failure(); }
            // Recheck identity after I/O so an old account's response cannot populate a new cache.
            if (await _token(ct).ConfigureAwait(false) != token) throw Failure();
            lock (_gate)
            {
                if (_disposed || _leases == 0 || generation != _generation) throw Failure();
                if (value.Ttl > 0 && _clock.GetElapsedTime(start).TotalSeconds >= value.Ttl) throw Failure();
                _cached = Copy(value); _started = start; _retry = TimeSpan.Zero; _refreshFailures = 0;
                Schedule(value.Ttl == 0 ? Timeout.InfiniteTimeSpan :
                    TimeSpan.FromSeconds(Math.Max(1, value.Ttl * .8 - _clock.GetElapsedTime(start).TotalSeconds)));
                return Copy(value);
            }
        }
        finally { _fetch.Release(); }
    }

    private async Task RefreshAsync()
    {
        try { await GetAsync(true, CancellationToken.None).ConfigureAwait(false); }
        catch
        {
            lock (_gate)
                if (!_disposed && _leases > 0)
                {
                    _refreshFailures = Math.Min(_refreshFailures + 1, 7);
                    double backoff = Math.Min(300, 5 * Math.Pow(2, _refreshFailures - 1));
                    double remaining = Math.Max(0, (_retry - _clock.GetElapsedTime(_retryStarted)).TotalSeconds);
                    _retry = TimeSpan.FromSeconds(Math.Clamp(Math.Max(backoff, remaining), 1, 300));
                    _retryStarted = _clock.GetTimestamp();
                    Schedule(_retry);
                }
        }
    }
    private void Schedule(TimeSpan delay) => _timer.Change(
        delay == Timeout.InfiniteTimeSpan ? delay : delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay,
        Timeout.InfiniteTimeSpan);
    private static InvalidOperationException Failure() => new("TURN credentials are unavailable.");
    private static TurnResponse Copy(TurnResponse value) => new(
        value.IceServers.Select(s => new TurnIceServer(s.Urls.ToArray(), s.Username, s.Credential)).ToArray(), value.Ttl);
    private static void Validate(TurnResponse value)
    {
        if (value.IceServers is null || value.IceServers.Length > 16 ||
            (value.IceServers.Length == 0 ? value.Ttl != 0 : value.Ttl is < 60 or > 86400)) throw Failure();
        foreach (var s in value.IceServers)
            if (s is null || s.Urls is null || s.Urls.Length is < 1 or > 16 ||
                string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrWhiteSpace(s.Credential) ||
                s.Urls.Any(u => string.IsNullOrWhiteSpace(u) || u.Any(char.IsControl) || !IsTurnUrl(u))) throw Failure();
    }
    private static bool IsTurnUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "turn" && uri.Scheme != "turns")) return false;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(value,
            @"\Aturns?:(?<host>\[[0-9a-f:.]+\]|[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)(?::(?<port>[0-9]{1,5}))?(?:\?transport=(?:udp|tcp))?\z",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return false;
        string host = match.Groups["host"].Value;
        if (host.StartsWith('['))
        {
            if (!System.Net.IPAddress.TryParse(host[1..^1], out System.Net.IPAddress? address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        return !match.Groups["port"].Success
            || (int.TryParse(match.Groups["port"].Value, out int port) && port is >= 1 and <= 65535);
    }
    private void Release()
    {
        lock (_gate)
            if (--_leases == 0) { _cached = null; _identity = null; _generation++; _retry = TimeSpan.Zero; _refreshFailures = 0; if (!_disposed) Schedule(Timeout.InfiniteTimeSpan); }
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _cached = null; _identity = null; _generation++; _timer.Dispose(); }
        _http.Dispose();
    }
    internal sealed class Lease(TurnCredentialProvider owner, TurnResponse value) : IDisposable
    {
        private TurnCredentialProvider? _owner = owner;
        internal TurnResponse Value { get; } = value;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
