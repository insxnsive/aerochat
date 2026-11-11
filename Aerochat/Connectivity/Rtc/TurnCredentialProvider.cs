using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Aerochat.Connectivity.Auth;

namespace Aerochat.Connectivity.Rtc;

internal sealed record TurnIceServer(string[] Urls, string Username, string Credential);
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
                if (string.IsNullOrWhiteSpace(token)) { _cached = null; throw Failure(); }
                if (_identity != token) { _cached = null; _identity = token; _generation++; }
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
                    lock (_gate) { _retryStarted = start; _retry = retry < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retry; }
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
                _cached = Copy(value); _started = start; _retry = TimeSpan.Zero;
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
                    Schedule(_retry > TimeSpan.Zero ? _retry - _clock.GetElapsedTime(_retryStarted) : TimeSpan.FromSeconds(5));
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
                s.Urls.Any(u => string.IsNullOrWhiteSpace(u) || u.Any(char.IsControl) ||
                    !(u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                      u.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)))) throw Failure();
    }
    private void Release()
    {
        lock (_gate)
            if (--_leases == 0) { _cached = null; _identity = null; _generation++; if (!_disposed) Schedule(Timeout.InfiniteTimeSpan); }
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
