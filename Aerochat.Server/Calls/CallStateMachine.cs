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
    ScreenStart,
    ScreenStop,
    Hangup
}

/// <summary>Pure signaling state machine. It has no transport or persistence concerns.</summary>
public sealed class CallStateMachine
{
    private readonly object _gate = new();

    public CallStateMachine(CallState initialState = CallState.Idle) => State = initialState;

    public CallState State { get; private set; }

    public bool IsScreenSharing { get; private set; }

    public bool TryApply(CallAction action)
    {
        lock (_gate)
        {
            if (action == CallAction.ScreenStart)
            {
                if (State != CallState.Connected || IsScreenSharing)
                {
                    return false;
                }

                IsScreenSharing = true;
                return true;
            }

            if (action == CallAction.ScreenStop)
            {
                if (State != CallState.Connected || !IsScreenSharing)
                {
                    return false;
                }

                IsScreenSharing = false;
                return true;
            }

            CallState? next = (State, action) switch
            {
                (CallState.Idle, CallAction.Ring) => CallState.Ringing,
                (CallState.Ringing, CallAction.Offer) => CallState.Offering,
                (CallState.Offering, CallAction.Answer) => CallState.Connected,
                (CallState.Ringing, CallAction.Ice) => CallState.Ringing,
                (CallState.Offering, CallAction.Ice) => CallState.Offering,
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
            if (State == CallState.Ended)
            {
                IsScreenSharing = false;
            }

            return true;
        }
    }
}

public enum CallApplyResult
{
    Applied,
    InvalidState,
    Forbidden,
    Mismatch
}

public sealed class CallSession(Guid conversationId, Guid callerId, Guid calleeId)
{
    public Guid ConversationId { get; } = conversationId;
    public Guid CallerId { get; } = callerId;
    public Guid CalleeId { get; } = calleeId;
    public Guid CallId { get; } = Guid.NewGuid();
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

    public bool TryStart(Guid conversationId, Guid callerId, Guid calleeId, out CallSession? session)
    {
        lock (_gate)
        {
            ReclaimExpired(conversationId);

            if (callerId == calleeId || _sessions.ContainsKey(conversationId))
            {
                session = null;
                return false;
            }

            session = new CallSession(conversationId, callerId, calleeId);
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

    /// <summary>
    /// Applies a signal for an authenticated actor, enforcing the identities captured at ring and
    /// the role rules for the current conversation member pair. The validated counterpart is
    /// returned as the only publish recipient, together with the generation actually applied.
    /// </summary>
    public CallApplyResult TryApply(
        Guid conversationId,
        Guid actorId,
        Guid firstMemberId,
        Guid secondMemberId,
        CallAction action,
        Guid? expectedCallId,
        out Guid recipientId,
        out Guid callId)
    {
        recipientId = Guid.Empty;
        callId = Guid.Empty;

        lock (_gate)
        {
            ReclaimExpired(conversationId);

            if (!_sessions.TryGetValue(conversationId, out CallSession? session))
            {
                return CallApplyResult.InvalidState;
            }

            if (!MatchesMemberPair(session, firstMemberId, secondMemberId))
            {
                return CallApplyResult.Forbidden;
            }

            bool isCaller = actorId == session.CallerId;
            bool isCallee = actorId == session.CalleeId;
            if (!isCaller && !isCallee)
            {
                return CallApplyResult.Forbidden;
            }

            if ((action == CallAction.Offer && !isCaller)
                || (action == CallAction.Answer && !isCallee))
            {
                return CallApplyResult.Forbidden;
            }

            if (expectedCallId is Guid expected && expected != session.CallId)
            {
                return CallApplyResult.Mismatch;
            }

            CallState previousState = session.StateMachine.State;
            if (!session.StateMachine.TryApply(action))
            {
                return CallApplyResult.InvalidState;
            }

            if (session.StateMachine.State == CallState.Ended)
            {
                _sessions.Remove(conversationId);
            }
            else if (session.StateMachine.State != previousState)
            {
                // Only a real pending-phase transition arms a fresh setup deadline; trickled ICE
                // is relayed without moving the existing deadline.
                ArmSetupDeadline(session);
            }

            recipientId = isCaller ? session.CalleeId : session.CallerId;
            callId = session.CallId;
            return CallApplyResult.Applied;
        }
    }

    private static bool MatchesMemberPair(CallSession session, Guid firstMemberId, Guid secondMemberId) =>
        (session.CallerId == firstMemberId && session.CalleeId == secondMemberId)
        || (session.CallerId == secondMemberId && session.CalleeId == firstMemberId);

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
