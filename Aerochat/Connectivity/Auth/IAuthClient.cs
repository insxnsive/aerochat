namespace Aerochat.Connectivity.Auth;

public interface IAuthClient
{
    bool IsAvailable { get; }

    Task<AuthSession> SignInAsync(
        string provider,
        bool rememberSession = true,
        CancellationToken cancellationToken = default);
}

public sealed record AuthSession(string AccessToken, int ExpiresIn);
