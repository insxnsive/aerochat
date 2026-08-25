namespace Aerochat.Connectivity.Auth;

public interface ILoopbackCallbackListener : IAsyncDisposable
{
    Uri CallbackUri { get; }

    void Start();

    Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default);
}
