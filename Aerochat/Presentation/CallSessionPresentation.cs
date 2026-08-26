namespace Aerochat.Presentation;

public enum CallSessionState
{
    Idle,
    Ringing,
    Incoming,
    Offering,
    Connected,
    Ended
}

public sealed class CallSessionPresentation : ObservableObject
{
    private CallSessionState _state;
    private string? _sdp;
    private string? _candidate;
    private string? _reason;

    public required string ConversationId { get; init; }
    public CallSessionState State { get => _state; private set => SetProperty(ref _state, value); }
    public string? Sdp { get => _sdp; private set => SetProperty(ref _sdp, value); }
    public string? Candidate { get => _candidate; private set => SetProperty(ref _candidate, value); }
    public string? Reason { get => _reason; private set => SetProperty(ref _reason, value); }

    public void Apply(string eventType, string? sdp, string? candidate, string? reason)
    {
        State = eventType switch
        {
            "call.ring" => CallSessionState.Incoming,
            "call.offer" => CallSessionState.Offering,
            "call.answer" => CallSessionState.Connected,
            "call.hangup" => CallSessionState.Ended,
            "call.ice" => State,
            _ => State
        };
        Sdp = sdp ?? Sdp;
        Candidate = candidate ?? Candidate;
        Reason = reason ?? Reason;
    }

    public void SetLocalState(CallSessionState state) => State = state;
}
