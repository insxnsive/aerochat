namespace Aerochat.Presentation;

public enum CallSessionState
{
    Idle,
    Starting,
    Ringing,
    Incoming,
    Offering,
    Connecting,
    Connected,
    Failed,
    Ended
}

public sealed class CallSessionPresentation : ObservableObject
{
    private CallSessionState _state;
    private string? _sdp;
    private string? _candidate;
    private string? _reason;
    private string? _callId;

    public required string ConversationId { get; init; }
    public string? CallId { get => _callId; private set => SetProperty(ref _callId, value); }
    public CallSessionState State { get => _state; private set => SetProperty(ref _state, value); }
    public string? Sdp { get => _sdp; private set => SetProperty(ref _sdp, value); }
    public string? Candidate { get => _candidate; private set => SetProperty(ref _candidate, value); }
    public string? Reason { get => _reason; private set => SetProperty(ref _reason, value); }

    public void Apply(string eventType, string? sdp, string? candidate, string? reason)
    {
        State = eventType switch
        {
            "call.ring" => CallSessionState.Incoming,
            "call.offer" => CallSessionState.Incoming,
            "call.answer" when State == CallSessionState.Connected => CallSessionState.Connected,
            "call.answer" => CallSessionState.Connecting,
            "call.hangup" => CallSessionState.Ended,
            "call.ice" => State,
            _ => State
        };
        Sdp = sdp ?? Sdp;
        Candidate = candidate ?? Candidate;
        Reason = reason ?? Reason;
    }

    /// <summary>
    /// Applies a signal that arrived from the server for a specific call. Unlike
    /// <see cref="Apply"/>, this checks the call id so a stale or replayed signal cannot change
    /// the session. Returns true when the signal was applied.
    /// </summary>
    public bool ApplyRemote(string eventType, string? callId, string? sdp, string? candidate, string? reason)
    {
        if (string.IsNullOrEmpty(callId))
            return false;

        if (eventType is "call.ring" or "call.offer")
        {
            // A call that is already being placed or running is never replaced or restarted.
            if (IsCallInProgress)
                return false;

            if (CallId != callId)
            {
                CallId = callId;
                Sdp = null;
                Candidate = null;
                Reason = null;
            }

            Apply(eventType, sdp, candidate, reason);
            return true;
        }

        if (CallId is null || CallId != callId)
            return false;

        Apply(eventType, sdp, candidate, reason);
        if (eventType == "call.hangup")
            CallId = null;
        return true;
    }

    public void SetLocalState(CallSessionState state)
    {
        State = state;
        if (state is CallSessionState.Idle or CallSessionState.Failed or CallSessionState.Ended)
            CallId = null;
    }

    public void BeginOutgoing()
    {
        CallId = null;
        Sdp = null;
        Candidate = null;
        Reason = null;
        State = CallSessionState.Starting;
    }

    public void End(string reason)
    {
        Reason = reason;
        State = CallSessionState.Ended;
        CallId = null;
    }

    public void Fail(string reason)
    {
        Reason = reason;
        State = CallSessionState.Failed;
        CallId = null;
    }

    private bool IsCallInProgress =>
        State is CallSessionState.Starting or CallSessionState.Ringing or CallSessionState.Offering
            or CallSessionState.Connecting or CallSessionState.Connected;
}
