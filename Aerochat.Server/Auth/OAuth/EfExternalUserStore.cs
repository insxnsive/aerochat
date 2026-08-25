using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Auth.OAuth;

public sealed class EfExternalUserStore : IExternalUserStore
{
    private readonly ChatDb _db;

    public EfExternalUserStore(ChatDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<ExternalUser> UpsertAsync(
        ExternalIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        ExternalUserEntity? existing = await _db.Users
            .SingleOrDefaultAsync(
                user => user.Provider == identity.Provider
                    && user.ProviderUserId == identity.ProviderUserId,
                cancellationToken);

        if (existing is not null)
        {
            ApplyIdentity(existing, identity, now);
            await _db.SaveChangesAsync(cancellationToken);
            return ToModel(existing);
        }

        var created = new ExternalUserEntity
        {
            Id = Guid.NewGuid(),
            Provider = identity.Provider,
            ProviderUserId = identity.ProviderUserId,
            DisplayName = identity.DisplayName,
            Email = identity.Email,
            AvatarUrl = identity.AvatarUrl,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Users.Add(created);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return ToModel(created);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            _db.Entry(created).State = EntityState.Detached;
            ExternalUserEntity? raced = await _db.Users
                .SingleOrDefaultAsync(
                    user => user.Provider == identity.Provider
                        && user.ProviderUserId == identity.ProviderUserId,
                    cancellationToken);
            if (raced is null)
            {
                throw;
            }

            ApplyIdentity(raced, identity, now);
            await _db.SaveChangesAsync(cancellationToken);
            return ToModel(raced);
        }
    }

    public async Task<ExternalUser?> FindAsync(
        string provider,
        string providerUserId,
        CancellationToken cancellationToken = default)
    {
        ExternalUserEntity? entity = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.Provider == provider && user.ProviderUserId == providerUserId,
                cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    private static void ValidateIdentity(ExternalIdentity identity)
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
    }

    private static void ApplyIdentity(ExternalUserEntity entity, ExternalIdentity identity, DateTimeOffset now)
    {
        entity.Provider = identity.Provider;
        entity.ProviderUserId = identity.ProviderUserId;
        entity.DisplayName = identity.DisplayName;
        entity.Email = identity.Email;
        entity.AvatarUrl = identity.AvatarUrl;
        entity.UpdatedAt = now;
    }

    private static ExternalUser ToModel(ExternalUserEntity entity) =>
        new(
            entity.Id,
            entity.Provider,
            entity.ProviderUserId,
            entity.DisplayName,
            entity.Email,
            entity.AvatarUrl,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && sqlite.SqliteExtendedErrorCode == 2067;
}
