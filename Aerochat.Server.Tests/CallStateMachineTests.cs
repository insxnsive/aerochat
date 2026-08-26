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
    [TestCase(CallAction.Ice, CallState.Offering)]
    [TestCase(CallAction.Offer, CallState.Offering)]
    [TestCase(CallAction.Answer, CallState.Connected)]
    [TestCase(CallAction.Ring, CallState.Ended)]
    public void Illegal_transition_is_rejected_without_state_change(CallAction action, CallState initial)
    {
        var machine = new CallStateMachine(initial);

        Assert.That(machine.TryApply(action), Is.False);
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
    public void Registry_allows_one_active_call_per_conversation()
    {
        var registry = new CallRegistry();
        Guid conversationId = Guid.NewGuid();

        Assert.That(registry.TryStart(conversationId, out CallSession? first), Is.True);
        Assert.That(registry.TryStart(conversationId, out CallSession? second), Is.False);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);

        Assert.That(registry.TryApply(conversationId, CallAction.Hangup), Is.True);
        Assert.That(registry.TryStart(conversationId, out CallSession? replacement), Is.True);
        Assert.That(replacement, Is.Not.Null);
    }
}
