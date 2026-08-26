namespace Aerochat.Connectivity.Auth;

public sealed class NullAuthClient : IAuthClient
{
    public bool IsAvailable => false;

    public Task<string?> LoadCachedTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<AuthSession> SignInAsync(
        string provider,
        bool rememberSession = true,
        CancellationToken cancellationToken = default) =>
        throw new AuthUnavailableException();
}
