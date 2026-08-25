namespace Aerochat.Connectivity;

public interface ITokenCache
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string token, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
