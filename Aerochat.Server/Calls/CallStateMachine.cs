namespace Aerochat.Server.Calls;

public enum CallState
{
    Idle,
    Ringing,
    Offering,
    Answering,
    Connected,
    Ended
}

public enum CallAction
{
    Ring,
    Offer,
    Answer,
    Ice,
    Hangup
}

/// <summary>Pure signaling state machine. It has no transport or persistence concerns.</summary>
public sealed class CallStateMachine
{
    private readonly object _gate = new();

    public CallStateMachine(CallState initialState = CallState.Idle) => State = initialState;

    public CallState State { get; private set; }

    public bool TryApply(CallAction action)
    {
        lock (_gate)
        {
            CallState? next = (State, action) switch
            {
                (CallState.Idle, CallAction.Ring) => CallState.Ringing,
                (CallState.Ringing, CallAction.Offer) => CallState.Offering,
                (CallState.Offering, CallAction.Answer) => CallState.Connected,
                (CallState.Connected, CallAction.Ice) => CallState.Connected,
                (CallState.Idle or CallState.Ringing or CallState.Offering
                    or CallState.Answering or CallState.Connected, CallAction.Hangup) => CallState.Ended,
                _ => null
            };

            if (next is null)
            {
                return false;
            }

            State = next.Value;
            return true;
        }
    }
}

public sealed class CallSession(Guid conversationId)
{
    public Guid ConversationId { get; } = conversationId;
    public CallStateMachine StateMachine { get; } = new();

    /// <summary>Setup deadline for pending states; null once the call is connected or ended.</summary>
    internal DateTimeOffset? SetupDeadline { get; set; }
}

/// <summary>Single-server v1 active-call registry. State is intentionally process-local.</summary>
public sealed class CallRegistry
{
    /// <summary>Pending setup states stop blocking replacement calls after this long.</summary>
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CallSession> _sessions = [];
    private readonly TimeProvider _clock;

    public CallRegistry()
        : this(TimeProvider.System)
    {
    }

    public CallRegistry(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public bool TryStart(Guid conversationId, out CallSession? session)
    {
        lock (_gate)
        {
            ReclaimExpired(conversationId);

            if (_sessions.ContainsKey(conversationId))
            {
                session = null;
                return false;
            }

            session = new CallSession(conversationId);
            if (!session.StateMachine.TryApply(CallAction.Ring))
            {
                session = null;
                return false;
            }

            ArmSetupDeadline(session);
            _sessions.Add(conversationId, session);
            return true;
        }
    }

    public bool TryApply(Guid conversationId, CallAction action)
    {
        lock (_gate)
        {
            ReclaimExpired(conversationId);

            if (!_sessions.TryGetValue(conversationId, out CallSession? session)
                || !session.StateMachine.TryApply(action))
            {
                return false;
            }

            if (session.StateMachine.State == CallState.Ended)
            {
                _sessions.Remove(conversationId);
            }
            else
            {
                ArmSetupDeadline(session);
            }

            return true;
        }
    }

    private void ArmSetupDeadline(CallSession session)
    {
        session.SetupDeadline = session.StateMachine.State is CallState.Ringing
            or CallState.Offering
            or CallState.Answering
            ? _clock.GetUtcNow().Add(SetupTimeout)
            : null;
    }

    private void ReclaimExpired(Guid conversationId)
    {
        if (_sessions.TryGetValue(conversationId, out CallSession? session)
            && session.SetupDeadline is DateTimeOffset deadline
            && _clock.GetUtcNow() >= deadline)
        {
            _sessions.Remove(conversationId);
        }
    }
}
