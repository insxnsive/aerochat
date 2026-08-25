namespace Aerochat.Server.Auth.OAuth;

public interface IExternalUserStore
{
    ExternalUser Upsert(ExternalIdentity identity, DateTimeOffset now);

    bool TryGet(string provider, string providerUserId, out ExternalUser? user);
}
