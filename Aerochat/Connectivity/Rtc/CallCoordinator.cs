using Aerochat.Presentation;

namespace Aerochat.Connectivity.Rtc;

public interface ICallCoordinator : IAsyncDisposable
{
    CallSessionPresentation Session { get; }
    bool IsMuted { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task AcceptAsync(CancellationToken cancellationToken = default);
    void ToggleMute();
    Task HangupAsync(string reason = "local hangup", CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns one conversation's signaling and RTC lifetime. The gateway transport is shared
/// by the application and is never disposed here.
/// </summary>
public sealed class CallCoordinator : ICallCoordinator
{
    private enum CallRole
    {
        None,
        Outgoing,
        Incoming
    }

    private readonly PresentationState _state;
    private readonly string _conversationId;
    private readonly ICallSignalingClient _signaling;
    private readonly IRtcPeerEngine _engine;
    private readonly IChatTransport _transport;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly List<RtcIceCandidate> _pendingCandidates = [];
    private readonly object _disposeGate = new();
    private readonly object _backgroundGate = new();
    private readonly List<Task> _backgroundTasks = [];
    private Task? _disposeTask;
    private int _disposed;
    private bool _muted;
    private bool _remoteDescriptionApplied;
    private bool _hangupCompleted;
    private CallRole _role;

    public CallCoordinator(
        PresentationState state,
        string conversationId,
        ICallSignalingClient signaling,
        IRtcPeerEngine engine,
        IChatTransport transport,
        Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transport);

        _state = state;
        _conversationId = conversationId;
        _signaling = signaling;
        _engine = engine;
        _transport = transport;
        _dispatch = dispatch ?? (action => action());

        _transport.CallSignalReceived += OnCallSignalReceived;
        _engine.IceCandidateReady += OnIceCandidateReady;
        _engine.StateChanged += OnRtcStateChanged;
    }

    public CallSessionPresentation Session => _state.GetOrCreateCallSession(_conversationId);
    public bool IsMuted => _muted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hangupCompleted = false;
            _remoteDescriptionApplied = false;
            _muted = false;
            _role = CallRole.Outgoing;
            ApplyPresentation(() => _state.BeginOutgoingCall(_conversationId));
            await _signaling.RingAsync(_conversationId, cancellationToken).ConfigureAwait(false);
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Ringing));
            string offer = await _engine.StartCall(cancellationToken).ConfigureAwait(false);
            DrainPendingCandidates();
            await _signaling.OfferAsync(_conversationId, offer, cancellationToken).ConfigureAwait(false);
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Offering));
        }
        catch
        {
            await FailCallUnderLockAsync("Call setup failed").ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task FailCallUnderLockAsync(string reason)
    {
        _hangupCompleted = true;
        _muted = false;
        try
        {
            await _engine.Hangup(reason).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the original setup failure while still terminating presentation state.
        }

        _remoteDescriptionApplied = false;
        _pendingCandidates.Clear();
        _role = CallRole.None;
        ApplyPresentation(() => Session.Fail(reason));

        try
        {
            await _signaling.HangupAsync(_conversationId, reason).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Setup failure is already terminal; remote cleanup is best effort.
        }
    }

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hangupCompleted = false;
            _muted = false;
            if (_role == CallRole.Outgoing)
                throw new InvalidOperationException("An outgoing call cannot accept its own offer.");
            _role = CallRole.Incoming;
            string offer = Session.Sdp
                ?? throw new InvalidOperationException("No incoming call offer is available.");
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Connecting));
            string answer = await _engine.AcceptOffer(offer, cancellationToken).ConfigureAwait(false);
            _remoteDescriptionApplied = true;
            DrainPendingCandidates();
            await _signaling.AnswerAsync(_conversationId, answer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailCallUnderLockAsync("Call acceptance failed").ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    public void ToggleMute()
    {
        ThrowIfDisposed();
        if (_muted)
            _engine.Unmute();
        else
            _engine.Mute();

        _muted = !_muted;
    }

    public async Task HangupAsync(
        string reason = "local hangup",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hangupCompleted)
                return;

            _hangupCompleted = true;
            Exception? mediaFailure = null;
            try
            {
                await _engine.Hangup(reason).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                mediaFailure = exception;
            }
            finally
            {
                _remoteDescriptionApplied = false;
                _pendingCandidates.Clear();
                _role = CallRole.None;
                ApplyPresentation(() => Session.SetLocalState(CallSessionState.Ended));
            }

            try
            {
                await _signaling.HangupAsync(_conversationId, reason, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (mediaFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(mediaFailure).Throw();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void OnCallSignalReceived(object? sender, CallSignalEventArgs signal)
    {
        if (signal.ConversationId != _conversationId || Volatile.Read(ref _disposed) != 0)
            return;

        StartBackgroundTask(() => HandleSignalSafelyAsync(signal));
    }

    private async Task HandleSignalSafelyAsync(CallSignalEventArgs signal)
    {
        try
        {
            await HandleSignalAsync(signal).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (Exception)
        {
            await EndCallAfterFailureAsync("Call signal handling failed", notifyRemote: true)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleSignalAsync(CallSignalEventArgs signal)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            switch (signal.EventType)
            {
                case "call.ring":
                case "call.offer":
                    if (_role == CallRole.Outgoing)
                        break;
                    _role = CallRole.Incoming;
                    _hangupCompleted = false;
                    ApplyPresentation(() => _state.ApplyCallSignal(
                        signal.EventType,
                        _conversationId,
                        signal.Sdp,
                        signal.Candidate,
                        signal.Reason));
                    break;

                case "call.answer" when signal.Sdp is not null && _role == CallRole.Outgoing:
                    await _engine.ApplyAnswer(signal.Sdp).ConfigureAwait(false);
                    _remoteDescriptionApplied = true;
                    DrainPendingCandidates();
                    ApplyPresentation(() => _state.ApplyCallSignal(
                        signal.EventType,
                        _conversationId,
                        signal.Sdp,
                        signal.Candidate,
                        signal.Reason));
                    break;

                case "call.ice" when signal.Candidate is not null:
                    var candidate = new RtcIceCandidate(signal.Candidate);
                    if (!_remoteDescriptionApplied)
                        _pendingCandidates.Add(candidate);
                    else
                        _engine.AddIceCandidate(candidate);
                    ApplyPresentation(() => _state.ApplyCallSignal(
                        signal.EventType,
                        _conversationId,
                        signal.Sdp,
                        signal.Candidate,
                        signal.Reason));
                    break;

                case "call.hangup":
                    _hangupCompleted = true;
                    await _engine.Hangup(signal.Reason ?? "remote hangup").ConfigureAwait(false);
                    _role = CallRole.None;
                    ApplyPresentation(() => _state.ApplyCallSignal(
                        signal.EventType,
                        _conversationId,
                        signal.Sdp,
                        signal.Candidate,
                        signal.Reason));
                    break;
            }
        }
        catch when (Volatile.Read(ref _disposed) != 0)
        {
        }
        finally
        {
            _operations.Release();
        }
    }

    private void OnIceCandidateReady(object? sender, RtcIceCandidate candidate)
        => StartBackgroundTask(() => SendIceAsync(candidate));

    private void OnRtcStateChanged(object? sender, RtcPeerState state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (state == RtcPeerState.Connected)
        {
            if (_hangupCompleted)
                return;

            ApplyPresentation(() =>
            {
                if (Session.State is not (CallSessionState.Idle or CallSessionState.Failed or CallSessionState.Ended))
                    Session.SetLocalState(CallSessionState.Connected);
            });
            return;
        }

        if (state == RtcPeerState.Closed)
        {
            _role = CallRole.None;
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Ended));
            return;
        }

        if (state is not (RtcPeerState.Disconnected or RtcPeerState.Failed))
            return;

        StartBackgroundTask(() => EndCallAfterFailureAsync(
            $"RTC peer {state.ToString().ToLowerInvariant()}",
            notifyRemote: true));
    }

    private void StartBackgroundTask(Func<Task> taskFactory)
    {
        Task task;
        lock (_backgroundGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            task = taskFactory();
            _backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                    _ = completed.Exception;
                lock (_backgroundGate)
                    _backgroundTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task SendIceAsync(RtcIceCandidate candidate)
    {
        try
        {
            await _signaling.IceAsync(_conversationId, candidate.Candidate).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (Exception)
        {
            await EndCallAfterFailureAsync("ICE signaling failed").ConfigureAwait(false);
        }
    }

    private async Task EndCallAfterFailureAsync(string reason, bool notifyRemote = false)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hangupCompleted)
                return;

            _hangupCompleted = true;
            try
            {
                await _engine.Hangup(reason).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The initiating failure remains terminal even when media cleanup also fails.
            }
            _remoteDescriptionApplied = false;
            _pendingCandidates.Clear();
            _role = CallRole.None;
            ApplyPresentation(() => Session.Fail(reason));
            if (notifyRemote)
            {
                try
                {
                    await _signaling.HangupAsync(_conversationId, reason).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Peer cleanup must complete even if failure signaling is unavailable.
                }
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void DrainPendingCandidates()
    {
        foreach (RtcIceCandidate candidate in _pendingCandidates)
            _engine.AddIceCandidate(candidate);
        _pendingCandidates.Clear();
    }

    private void ApplyPresentation(Action action) => _dispatch(action);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            Volatile.Write(ref _disposed, 1);
            _ = CompleteDisposeAsync(completion);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _transport.CallSignalReceived -= OnCallSignalReceived;
        _engine.IceCandidateReady -= OnIceCandidateReady;
        _engine.StateChanged -= OnRtcStateChanged;

        Task[] backgroundTasks;
        lock (_backgroundGate)
            backgroundTasks = _backgroundTasks.ToArray();

        bool shouldNotifyRemote = false;
        try
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                _pendingCandidates.Clear();
                shouldNotifyRemote =
                    !_hangupCompleted && Session.State is not (CallSessionState.Idle or CallSessionState.Ended);
                if (shouldNotifyRemote)
                {
                    _hangupCompleted = true;
                    _role = CallRole.None;
                    ApplyPresentation(() => Session.SetLocalState(CallSessionState.Ended));
                }

                await _engine.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _operations.Release();
            }
        }
        finally
        {
            try
            {
                try
                {
                    await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Signaling is optional. Cleanup still owns both clients after failure.
                }

                if (shouldNotifyRemote)
                {
                    try
                    {
                        await _signaling.HangupAsync(_conversationId, "coordinator disposed").ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Local disposal still owns media cleanup when signaling is unavailable.
                    }
                }
            }
            finally
            {
                await _signaling.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
