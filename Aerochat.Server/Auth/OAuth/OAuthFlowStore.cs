using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Aerochat.Server.Auth.OAuth;

public sealed record OAuthAuthorizationState(
    string Provider,
    string CodeVerifier,
    Uri ReturnUri,
    DateTimeOffset ExpiresAt);

public sealed record OAuthHandoff(
    string AccessToken,
    int ExpiresIn,
    DateTimeOffset ExpiresAt);

public sealed class OAuthFlowStore
{
    public static readonly TimeSpan AuthorizationStateTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HandoffTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, OAuthAuthorizationState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthHandoff> _handoffs = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly object _handoffGate = new();
    private readonly TimeProvider _clock;
    private readonly int _maxPendingAuthorizationStates;
    private readonly int _maxPendingHandoffs;

    public OAuthFlowStore(
        TimeProvider clock,
        int maxPendingAuthorizationStates = 4096,
        int maxPendingHandoffs = 4096)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (maxPendingAuthorizationStates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingAuthorizationStates));
        }

        if (maxPendingHandoffs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingHandoffs));
        }

        _maxPendingAuthorizationStates = maxPendingAuthorizationStates;
        _maxPendingHandoffs = maxPendingHandoffs;
    }

    public string CreateAuthorizationState(
        string provider,
        string codeVerifier,
        Uri returnUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentNullException.ThrowIfNull(returnUri);

        DateTimeOffset now = _clock.GetUtcNow();
        lock (_stateGate)
        {
            RemoveExpired(_states, now, entry => entry.ExpiresAt);
            if (_states.Count >= _maxPendingAuthorizationStates)
            {
                throw new OAuthFlowCapacityException("The authorization state store is at capacity.");
            }

            while (true)
            {
                string state = OAuthPkce.Base64Url(RandomNumberGenerator.GetBytes(32));
                var authorizationState = new OAuthAuthorizationState(
                    provider,
                    codeVerifier,
                    returnUri,
                    now.Add(AuthorizationStateTtl));
                if (_states.TryAdd(state, authorizationState))
                {
                    return state;
                }
            }
        }
    }

    public bool TryConsumeAuthorizationState(
        string state,
        out OAuthAuthorizationState? authorizationState)
    {
        authorizationState = null;
        if (string.IsNullOrWhiteSpace(state)
            || !_states.TryRemove(state, out OAuthAuthorizationState? candidate))
        {
            return false;
        }

        if (_clock.GetUtcNow() >= candidate.ExpiresAt)
        {
            return false;
        }

        authorizationState = candidate;
        return true;
    }

    public string CreateHandoff(string accessToken, int expiresIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (expiresIn <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresIn));
        }

        DateTimeOffset now = _clock.GetUtcNow();
        lock (_handoffGate)
        {
            RemoveExpired(_handoffs, now, entry => entry.ExpiresAt);
            if (_handoffs.Count >= _maxPendingHandoffs)
            {
                throw new OAuthFlowCapacityException("The handoff store is at capacity.");
            }

            while (true)
            {
                string code = OAuthPkce.Base64Url(RandomNumberGenerator.GetBytes(32));
                var handoff = new OAuthHandoff(
                    accessToken,
                    expiresIn,
                    now.Add(HandoffTtl));
                if (_handoffs.TryAdd(code, handoff))
                {
                    return code;
                }
            }
        }
    }

    public bool TryConsumeHandoff(string code, out OAuthHandoff? handoff)
    {
        handoff = null;
        if (string.IsNullOrWhiteSpace(code)
            || !_handoffs.TryRemove(code, out OAuthHandoff? candidate))
        {
            return false;
        }

        if (_clock.GetUtcNow() >= candidate.ExpiresAt)
        {
            return false;
        }

        handoff = candidate;
        return true;
    }

    private static void RemoveExpired<T>(
        ConcurrentDictionary<string, T> entries,
        DateTimeOffset now,
        Func<T, DateTimeOffset> expiresAt)
    {
        foreach ((string key, T value) in entries)
        {
            if (now >= expiresAt(value))
            {
                entries.TryRemove(key, out _);
            }
        }
    }
}
