using Aerochat.Presentation;

namespace Aerochat.Connectivity.Rtc;

/// <summary>
/// Keeps the call surface available in offline demo mode without claiming live calling support.
/// </summary>
public sealed class OfflineCallCoordinator : ICallCoordinator
{
    private readonly PresentationState _state;
    private readonly string _conversationId;
    private int _disposed;

    public OfflineCallCoordinator(PresentationState state, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        _state = state;
        _conversationId = conversationId;
    }

    public CallSessionPresentation Session => _state.GetOrCreateCallSession(_conversationId);
    public bool IsMuted => false;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Session.BeginOutgoing();
        Session.Fail("Server not configured");
        return Task.CompletedTask;
    }

    public Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new InvalidOperationException("Live calling is unavailable in demo mode.");
    }

    public void ToggleMute() =>
        throw new InvalidOperationException("Live calling is unavailable in demo mode.");

    public Task HangupAsync(
        string reason = "local hangup",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Session.End(reason);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OfflineCallCoordinator));
    }
}
