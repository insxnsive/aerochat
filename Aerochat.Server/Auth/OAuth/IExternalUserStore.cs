namespace Aerochat.Server.Auth.OAuth;

public interface IExternalUserStore
{
    Task<ExternalUser> UpsertAsync(
        ExternalIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<ExternalUser?> FindAsync(
        string provider,
        string providerUserId,
        CancellationToken cancellationToken = default);
}
