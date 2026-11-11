using Aerochat.Connectivity;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class CallSessionIdTests
{
    private const string Current = "11111111-1111-1111-1111-111111111111";
    private const string Other = "22222222-2222-2222-2222-222222222222";

    [Test]
    public void A_replayed_ring_of_an_ended_call_is_not_adopted_by_the_session()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "replayed-offer", callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.CallId, Is.Null);
            Assert.That(session.Sdp, Is.Null);
        });
    }

    [Test]
    public void A_call_that_a_newer_ring_replaced_is_remembered_as_ended()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Other));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: Other));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.CallId, Is.Null);
        });
    }

    [Test]
    public void Accepting_takes_the_offer_and_blocks_a_newer_ring()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        bool taken = session.TryBeginAccept(out string? offer, out string? callId);
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Other));
        bool takenAgain = session.TryBeginAccept(out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.True);
            Assert.That(offer, Is.EqualTo("offer-sdp"));
            Assert.That(callId, Is.EqualTo(Current));
            Assert.That(takenAgain, Is.False);
            Assert.That(session.State, Is.EqualTo(CallSessionState.Connecting));
            Assert.That(session.CallId, Is.EqualTo(Current));
            Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));
        });
    }

    [Test]
    public void A_matching_answer_while_incoming_is_ignored()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp", callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Incoming));
            Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));
        });
    }

    [Test]
    public void A_coordinator_style_hangup_also_clears_the_call_id()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        state.ApplyCallSignal("call.hangup", conversationId, null, null, "peer_disconnected");

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.CallId, Is.Null);
        });
    }

    [Test]
    public void Stale_or_id_less_hangup_leaves_a_connected_call_connected()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        session.SetLocalState(CallSessionState.Connected);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "stale", callId: Other));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "no id"));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Connected));
            Assert.That(session.Reason, Is.Null);
            Assert.That(session.CallId, Is.EqualTo(Current));
        });

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.Reason, Is.EqualTo("peer_disconnected"));
            Assert.That(session.CallId, Is.Null);
        });
    }

    [Test]
    public void Replayed_signals_with_no_active_call_leave_the_session_idle_or_ended()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp", callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Idle));
            Assert.That(session.Reason, Is.Null);
            Assert.That(session.Sdp, Is.Null);
        });

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.answer", conversationId, sdp: "late-answer", callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "replayed", callId: Current));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.Reason, Is.EqualTo("peer_disconnected"));
            Assert.That(session.Sdp, Is.Null);
            Assert.That(session.CallId, Is.Null);
        });
    }

    [Test]
    public void A_new_ring_replaces_an_unanswered_call_but_not_a_call_in_progress()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: Current));
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Other));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Incoming));
            Assert.That(session.CallId, Is.EqualTo(Other));
            Assert.That(session.Sdp, Is.Null, "the first call's offer must not survive into the second call");
        });

        session.SetLocalState(CallSessionState.Connecting);
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "replayed-offer", callId: Other));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Connecting));
            Assert.That(session.CallId, Is.EqualTo(Other));
            Assert.That(session.Sdp, Is.Null);
        });
    }

    [Test]
    public void An_outgoing_call_ignores_adapter_signals_so_the_coordinator_stays_the_only_owner()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        CallSessionPresentation session = state.BeginOutgoingCall(conversationId);

        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: Other));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "stale", callId: Other));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp", callId: Other));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Starting));
            Assert.That(session.CallId, Is.Null);
            Assert.That(session.Reason, Is.Null);
            Assert.That(session.Sdp, Is.Null);
        });
    }
}
