using Aerochat.Presentation;
using System.Text;

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
    private const int MaxPendingCandidates = 64;
    private const int MaxPendingCandidateBytes = 32 * 1024;
    private Task? _disposeTask;
    private int _disposed;
    private bool _muted;
    private bool _remoteDescriptionApplied;
    private bool _hangupCompleted;
    private CallRole _role;
    private string? _callId;
    private int _callGeneration;
    private const int EndedCallMemory = 8;
    private readonly Queue<string> _endedCallIds = new();

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

        // The ring may have reached the adapter before this coordinator existed; follow that call.
        _callId = Session.CallId;

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
            _pendingCandidates.Clear();
            ClearCallId();
            _muted = false;
            _role = CallRole.Outgoing;
            ApplyPresentation(() => _state.BeginOutgoingCall(_conversationId));
            _callId = await _signaling.RingAsync(_conversationId, cancellationToken).ConfigureAwait(false);
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Ringing));
            string offer = await _engine.StartCall(cancellationToken).ConfigureAwait(false);
            DrainPendingCandidates();
            await _signaling.OfferAsync(_conversationId, offer, cancellationToken, callId: _callId).ConfigureAwait(false);
            ApplyPresentation(() => Session.SetLocalState(CallSessionState.Offering));
        }
        catch (Exception exception)
        {
            await FailCallUnderLockAsync(
                "Call setup failed",
                notifyRemote: exception is not CallStaleGenerationException).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    // Forgets the current call id and remembers it as ended, so a replayed ring or offer of the
    // same call is not adopted as a new call. Callers hold the operations lock. Every call that ends or
    // starts also moves the call generation on, which late failures use to tell which call they belong to.
    private string? ClearCallId()
    {
        Interlocked.Increment(ref _callGeneration);
        string? callId = _callId;
        _callId = null;
        if (callId is not null)
        {
            _endedCallIds.Enqueue(callId);
            while (_endedCallIds.Count > EndedCallMemory)
                _endedCallIds.Dequeue();
        }

        return callId;
    }

    // Thrown when an accept finds that the call it took has ended or changed while it waited for the
    // operations lock. It is not a failure of the call, so it does not end the call or hang up.
    private sealed class AcceptSupersededException : InvalidOperationException
    {
        public AcceptSupersededException()
            : base("The call ended or changed before it could be accepted.")
        {
        }
    }

    // Returns false when the session refused the signal.
    private bool ApplyRingOrOfferToSession(CallSignalEventArgs signal)
    {
        // A signal with a call id goes through the checked path, which also clears the previous
        // call's offer when the id changes; an id-less signal keeps the legacy behavior.
        if (signal.CallId is null)
        {
            // An id-less ring or offer must not restart a call that is being accepted or is running.
            if (Session.State is CallSessionState.Connecting or CallSessionState.Connected)
                return false;

            _state.ApplyCallSignal(signal.EventType, _conversationId, signal.Sdp, signal.Candidate, signal.Reason);
            return true;
        }

        return _state.ApplyRemoteCallSignal(
            signal.EventType,
            _conversationId,
            signal.CallId,
            signal.Sdp,
            signal.Candidate,
            signal.Reason);
    }

    private async Task FailCallUnderLockAsync(string reason, bool notifyRemote = true)
    {
        _hangupCompleted = true;
        _muted = false;
        string? callId = ClearCallId();
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

        if (!notifyRemote)
            return;

        try
        {
            await _signaling.HangupAsync(_conversationId, reason, callId: callId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Setup failure is already terminal; remote cleanup is best effort.
        }
    }

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // A second click while the call is already being accepted, or running, does nothing.
        if (Session.State is CallSessionState.Connecting or CallSessionState.Connected)
            return;

        // Take the offer and the call id and mark the call as accepted right away, before waiting for
        // the operations lock. The presentation write runs on the UI thread (ApplyPresentation is
        // synchronous), so a newer ring from the adapter cannot replace the call while this accept waits.
        string? acceptedOffer = null;
        string? acceptedCallId = null;
        ApplyPresentation(() => Session.TryBeginAccept(out acceptedOffer, out acceptedCallId));
        if (acceptedOffer is null)
            throw new InvalidOperationException("No incoming call offer is available.");

        try
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never got to accept: put the call back so it can be accepted again, unless it ended meanwhile.
            ApplyPresentation(() => Session.TryRestoreIncoming(acceptedCallId));
            throw;
        }

        try
        {
            // This accept waited for the lock: make sure the call it took is still the call we are in.
            if (Session.State != CallSessionState.Connecting
                || Session.CallId != acceptedCallId
                || (acceptedCallId is not null && _endedCallIds.Contains(acceptedCallId))
                || (acceptedCallId is not null && _callId is not null && _callId != acceptedCallId))
            {
                throw new AcceptSupersededException();
            }

            _hangupCompleted = false;
            _muted = false;
            if (_role == CallRole.Outgoing)
                throw new InvalidOperationException("An outgoing call cannot accept its own offer.");
            _role = CallRole.Incoming;
            if (acceptedCallId is not null)
                _callId = acceptedCallId;

            string answer = await _engine.AcceptOffer(acceptedOffer, cancellationToken).ConfigureAwait(false);
            _remoteDescriptionApplied = true;
            DrainPendingCandidates();
            await _signaling.AnswerAsync(_conversationId, answer, cancellationToken, callId: _callId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not AcceptSupersededException)
        {
            await FailCallUnderLockAsync(
                "Call acceptance failed",
                notifyRemote: exception is not CallStaleGenerationException).ConfigureAwait(false);
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
            string? callId = ClearCallId();
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
                await _signaling.HangupAsync(_conversationId, reason, cancellationToken, callId).ConfigureAwait(false);
            }
            catch (CallStaleGenerationException)
            {
                // The server had already ended that call.
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

        // A failure while handling this signal belongs to the call that was running when it arrived.
        int generation = Volatile.Read(ref _callGeneration);
        StartBackgroundTask(() => HandleSignalSafelyAsync(signal, generation));
    }

    private async Task HandleSignalSafelyAsync(CallSignalEventArgs signal, int generation)
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
            await EndCallAfterFailureAsync("Call signal handling failed", notifyRemote: true, onlyIfGeneration: generation)
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

            // Once this call has an id, only signals for that call count; another call's signals are dropped.
            bool startsCall = signal.EventType is "call.ring" or "call.offer";
            if (!startsCall && _callId is not null && signal.CallId != _callId)
                return;

            switch (signal.EventType)
            {
                case "call.ring":
                case "call.offer":
                    if (_role == CallRole.Outgoing)
                        break;
                    // Never follow a call that already ended.
                    if (signal.CallId is not null && _endedCallIds.Contains(signal.CallId))
                        break;
                    // An id-less signal must not replace a call that has an id.
                    if (signal.CallId is null && _callId is not null)
                        break;
                    // The same call offering again while it is answered must not reset it.
                    if (signal.CallId is not null && signal.CallId == _callId && _remoteDescriptionApplied)
                        break;
                    // A different call: ignore it while this one is answered.
                    if (signal.CallId != _callId && _callId is not null && _remoteDescriptionApplied)
                        break;

                    // The session decides first (ApplyPresentation is synchronous). A ring it refuses, for
                    // example while the user is accepting another call or for a call that already ended,
                    // is not followed by the coordinator either.
                    bool accepted = false;
                    ApplyPresentation(() => accepted = ApplyRingOrOfferToSession(signal));
                    if (!accepted)
                        break;

                    if (signal.CallId != _callId)
                    {
                        ClearCallId();
                        _callId = signal.CallId;
                        _remoteDescriptionApplied = false;
                        _pendingCandidates.Clear();
                    }

                    if (signal.EventType == "call.ring")
                        _pendingCandidates.Clear();
                    _role = CallRole.Incoming;
                    _hangupCompleted = false;
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
                        QueueCandidate(candidate);
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
                    ClearCallId();
                    _remoteDescriptionApplied = false;
                    _pendingCandidates.Clear();
                    try
                    {
                        await _engine.Hangup(signal.Reason ?? "remote hangup").ConfigureAwait(false);
                    }
                    finally
                    {
                        // The call is over for the user even when releasing the media fails.
                        _role = CallRole.None;
                        ApplyPresentation(() => _state.ApplyCallSignal(
                            signal.EventType,
                            _conversationId,
                            signal.Sdp,
                            signal.Candidate,
                            signal.Reason));
                    }

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
    {
        // A candidate raised after the call ended is dropped, and one that is sent keeps the id of the
        // call it was raised for, even if another call has started by the time the send runs.
        if (Volatile.Read(ref _hangupCompleted))
            return;

        string? callId = Volatile.Read(ref _callId);
        int generation = Volatile.Read(ref _callGeneration);
        StartBackgroundTask(() => SendIceAsync(candidate, callId, generation));
    }

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

        int generation = Volatile.Read(ref _callGeneration);
        StartBackgroundTask(() => EndCallAfterFailureAsync(
            $"RTC peer {state.ToString().ToLowerInvariant()}",
            notifyRemote: true,
            onlyIfGeneration: generation));
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

    private async Task SendIceAsync(RtcIceCandidate candidate, string? callId, int generation)
    {
        try
        {
            await _signaling.IceAsync(_conversationId, candidate.Candidate, callId: callId).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (CallStaleGenerationException)
        {
            // The server no longer knows the call this candidate belongs to. If that is still our
            // current call, the server ended or replaced it, so end it here without telling the server
            // again; a candidate of an older call is simply dropped.
            await EndCallAfterFailureAsync("Call ended by the server", onlyIfGeneration: generation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await EndCallAfterFailureAsync("ICE signaling failed", onlyIfGeneration: generation).ConfigureAwait(false);
        }
    }

    private async Task EndCallAfterFailureAsync(string reason, bool notifyRemote = false, int? onlyIfGeneration = null)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hangupCompleted)
                return;

            // A failure that belongs to an older call must not end the call that is running now.
            if (onlyIfGeneration is not null && Volatile.Read(ref _callGeneration) != onlyIfGeneration)
                return;

            _hangupCompleted = true;
            string? callId = ClearCallId();
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
                    await _signaling.HangupAsync(_conversationId, reason, callId: callId).ConfigureAwait(false);
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

    private void QueueCandidate(RtcIceCandidate candidate)
    {
        if (_pendingCandidates.Count >= MaxPendingCandidates)
            return;

        int queuedBytes = _pendingCandidates.Sum(item => Encoding.UTF8.GetByteCount(item.Candidate));
        if (queuedBytes + Encoding.UTF8.GetByteCount(candidate.Candidate) <= MaxPendingCandidateBytes)
            _pendingCandidates.Add(candidate);
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
        string? disposeCallId = null;
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
                    disposeCallId = ClearCallId();
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
                        await _signaling.HangupAsync(_conversationId, "coordinator disposed", callId: disposeCallId).ConfigureAwait(false);
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
