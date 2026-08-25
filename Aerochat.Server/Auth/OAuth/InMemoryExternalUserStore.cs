using System.Collections.Concurrent;

namespace Aerochat.Server.Auth.OAuth;

public sealed class InMemoryExternalUserStore : IExternalUserStore
{
    private readonly ConcurrentDictionary<ExternalUserKey, ExternalUser> _users = new(ExternalUserKeyComparer.Instance);

    public Task<ExternalUser> UpsertAsync(
        ExternalIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Upsert(identity, now));
    }

    public Task<ExternalUser?> FindAsync(
        string provider,
        string providerUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGet(provider, providerUserId, out ExternalUser? user);
        return Task.FromResult(user);
    }

    public ExternalUser Upsert(ExternalIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Provider))
        {
            throw new ArgumentException("Provider is required.", nameof(identity));
        }

        if (string.IsNullOrWhiteSpace(identity.ProviderUserId))
        {
            throw new ArgumentException("Provider user id is required.", nameof(identity));
        }

        var key = new ExternalUserKey(identity.Provider, identity.ProviderUserId);
        return _users.AddOrUpdate(
            key,
            _ => new ExternalUser(
                Guid.NewGuid(),
                identity.Provider,
                identity.ProviderUserId,
                identity.DisplayName,
                identity.Email,
                identity.AvatarUrl,
                now,
                now),
            (_, existing) => existing with
            {
                Provider = identity.Provider,
                ProviderUserId = identity.ProviderUserId,
                DisplayName = identity.DisplayName,
                Email = identity.Email,
                AvatarUrl = identity.AvatarUrl,
                UpdatedAt = now
            });
    }

    public bool TryGet(string provider, string providerUserId, out ExternalUser? user)
    {
        return _users.TryGetValue(new ExternalUserKey(provider, providerUserId), out user);
    }

    private readonly record struct ExternalUserKey(string Provider, string ProviderUserId);

    private sealed class ExternalUserKeyComparer : IEqualityComparer<ExternalUserKey>
    {
        public static ExternalUserKeyComparer Instance { get; } = new();

        public bool Equals(ExternalUserKey x, ExternalUserKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider)
            && StringComparer.Ordinal.Equals(x.ProviderUserId, y.ProviderUserId);

        public int GetHashCode(ExternalUserKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Provider),
                StringComparer.Ordinal.GetHashCode(obj.ProviderUserId));
    }
}
