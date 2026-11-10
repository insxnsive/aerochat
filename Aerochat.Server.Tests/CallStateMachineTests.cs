using Aerochat.Server.Calls;

namespace Aerochat.Server.Tests;

public sealed class CallStateMachineTests
{
    [TestCase(CallAction.Ring, CallState.Idle, CallState.Ringing)]
    [TestCase(CallAction.Offer, CallState.Ringing, CallState.Offering)]
    [TestCase(CallAction.Answer, CallState.Offering, CallState.Connected)]
    [TestCase(CallAction.Ice, CallState.Connected, CallState.Connected)]
    public void Legal_transition_changes_state(
        CallAction action,
        CallState initial,
        CallState expected)
    {
        var machine = new CallStateMachine(initial);

        Assert.That(machine.TryApply(action), Is.True);
        Assert.That(machine.State, Is.EqualTo(expected));
    }

    [TestCase(CallAction.Offer, CallState.Idle)]
    [TestCase(CallAction.Answer, CallState.Ringing)]
    [TestCase(CallAction.Offer, CallState.Offering)]
    [TestCase(CallAction.Answer, CallState.Connected)]
    [TestCase(CallAction.Ring, CallState.Ended)]
    public void Illegal_transition_is_rejected_without_state_change(CallAction action, CallState initial)
    {
        var machine = new CallStateMachine(initial);

        Assert.That(machine.TryApply(action), Is.False);
        Assert.That(machine.State, Is.EqualTo(initial));
    }

    [TestCase(CallState.Ringing)]
    [TestCase(CallState.Offering)]
    [TestCase(CallState.Connected)]
    public void Ice_is_relayed_without_changing_state(CallState initial)
    {
        var machine = new CallStateMachine(initial);

        Assert.That(machine.TryApply(CallAction.Ice), Is.True);
        Assert.That(machine.State, Is.EqualTo(initial));
    }

    [Test]
    public void Hangup_ends_any_live_session_and_ended_session_is_terminal()
    {
        foreach (CallState state in Enum.GetValues<CallState>().Where(state => state != CallState.Ended))
        {
            var machine = new CallStateMachine(state);

            Assert.That(machine.TryApply(CallAction.Hangup), Is.True);
            Assert.That(machine.State, Is.EqualTo(CallState.Ended));
            Assert.That(machine.TryApply(CallAction.Hangup), Is.False);
        }
    }

    [Test]
    public void Screen_share_start_stop_requires_a_connected_call_and_rejects_duplicates()
    {
        foreach (CallState state in Enum.GetValues<CallState>().Where(candidate => candidate != CallState.Connected))
        {
            var machine = new CallStateMachine(state);

            Assert.That(machine.TryApply(CallAction.ScreenStart), Is.False, state.ToString());
            Assert.That(machine.TryApply(CallAction.ScreenStop), Is.False, state.ToString());
            Assert.That(machine.IsScreenSharing, Is.False, state.ToString());
            Assert.That(machine.State, Is.EqualTo(state), state.ToString());
        }

        var connected = new CallStateMachine(CallState.Connected);

        Assert.That(connected.TryApply(CallAction.ScreenStart), Is.True);
        Assert.That(connected.IsScreenSharing, Is.True);
        Assert.That(connected.TryApply(CallAction.ScreenStart), Is.False);
        Assert.That(connected.IsScreenSharing, Is.True);
        Assert.That(connected.TryApply(CallAction.ScreenStop), Is.True);
        Assert.That(connected.IsScreenSharing, Is.False);
        Assert.That(connected.TryApply(CallAction.ScreenStop), Is.False);
        Assert.That(connected.State, Is.EqualTo(CallState.Connected));

        Assert.That(connected.TryApply(CallAction.ScreenStart), Is.True);
        Assert.That(connected.TryApply(CallAction.Hangup), Is.True);
        Assert.That(connected.IsScreenSharing, Is.False);
        Assert.That(connected.State, Is.EqualTo(CallState.Ended));
    }

    [Test]
    public void Registry_allows_one_active_call_per_conversation()
    {
        var registry = new CallRegistry();
        Guid conversationId = Guid.NewGuid();
        Guid callerId = Guid.NewGuid();
        Guid calleeId = Guid.NewGuid();

        Assert.That(registry.TryStart(conversationId, callerId, calleeId, out CallSession? first), Is.True);
        Assert.That(registry.TryStart(conversationId, callerId, calleeId, out CallSession? second), Is.False);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);

        Assert.That(
            registry.TryApply(
                conversationId, callerId, callerId, calleeId, CallAction.Hangup, out _),
            Is.EqualTo(CallApplyResult.Applied));
        Assert.That(registry.TryStart(conversationId, callerId, calleeId, out CallSession? replacement), Is.True);
        Assert.That(replacement, Is.Not.Null);
    }

    [Test]
    public void Registry_enforces_bound_identities_and_recipient()
    {
        var registry = new CallRegistry();
        Guid conversationId = Guid.NewGuid();
        Guid callerId = Guid.NewGuid();
        Guid calleeId = Guid.NewGuid();
        Guid strangerId = Guid.NewGuid();

        Assert.That(registry.TryStart(conversationId, callerId, calleeId, out _), Is.True);

        Assert.That(
            registry.TryApply(conversationId, calleeId, callerId, calleeId, CallAction.Offer, out _),
            Is.EqualTo(CallApplyResult.Forbidden));
        Assert.That(
            registry.TryApply(conversationId, callerId, callerId, calleeId, CallAction.Offer, out Guid recipient),
            Is.EqualTo(CallApplyResult.Applied));
        Assert.That(recipient, Is.EqualTo(calleeId));
        Assert.That(
            registry.TryApply(conversationId, callerId, callerId, calleeId, CallAction.Answer, out _),
            Is.EqualTo(CallApplyResult.Forbidden));
        Assert.That(
            registry.TryApply(conversationId, strangerId, callerId, calleeId, CallAction.Ice, out _),
            Is.EqualTo(CallApplyResult.Forbidden));
        Assert.That(
            registry.TryApply(conversationId, calleeId, callerId, calleeId, CallAction.Answer, out Guid callerRecipient),
            Is.EqualTo(CallApplyResult.Applied));
        Assert.That(callerRecipient, Is.EqualTo(callerId));
    }
}
