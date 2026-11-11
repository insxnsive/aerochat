using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;
using SIPSorceryMedia.Abstractions;

namespace Aerochat.VisualShell.Tests;

public sealed class CallClientTests
{
    [TestCase("call.ring", CallSessionState.Incoming)]
    [TestCase("call.offer", CallSessionState.Incoming)]
    [TestCase("call.answer", CallSessionState.Connecting)]
    [TestCase("call.hangup", CallSessionState.Ended)]
    public void Call_signal_maps_to_presentation_state(string type, CallSessionState expected)
    {
        PresentationState state = DemoData.Create();

        state.ApplyCallSignal(type, "conversation-1", "sdp", "candidate", "reason");

        CallSessionPresentation session = state.CallSessions.Single();
        Assert.That(session.State, Is.EqualTo(expected));
        Assert.That(session.Sdp, Is.EqualTo("sdp"));
    }

    [Test]
    public void Answer_signal_does_not_downgrade_a_connected_session()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        session.SetLocalState(CallSessionState.Connected);

        state.ApplyCallSignal("call.answer", conversationId, "answer-sdp", null, null);

        Assert.That(session.State, Is.EqualTo(CallSessionState.Connected));
    }

    [Test]
    public async Task Offline_call_coordinator_keeps_demo_call_control_available_and_fails_safely()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var calls = new OfflineCallCoordinator(state, conversationId);

        Assert.That(calls.Session.State, Is.EqualTo(CallSessionState.Idle));

        await calls.StartAsync();

        Assert.Multiple(() =>
        {
            Assert.That(calls.Session.State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(calls.Session.Reason, Is.EqualTo("Server not configured"));
            Assert.That(calls.IsMuted, Is.False);
        });

        await calls.DisposeAsync();
    }

    [TestCase("call.ring")]
    [TestCase("call.offer")]
    [TestCase("call.answer")]
    [TestCase("call.ice")]
    [TestCase("call.hangup")]
    [TestCase("call.screen.started")]
    [TestCase("call.screen.stopped")]
    public void Gateway_protocol_parses_call_event(string type)
    {
        string json = JsonSerializer.Serialize(new
        {
            t = type,
            eventId = "instance:1",
            d = new { conversationId = "conversation-1", sdp = "sdp", candidate = "candidate", reason = "busy" }
        });

        Assert.That(GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame), Is.True);
        Assert.That(GatewayProtocol.TryParseCallSignal(frame!, out CallSignalEventArgs? call), Is.True);
        Assert.That(call!.EventType, Is.EqualTo(type));
    }

    [Test]
    public async Task Signaling_client_uses_bearer_and_call_path()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var signaling = new CallSignalingClient(client, new Uri("https://server.test/"), "token");

        await signaling.OfferAsync("conversation/1", "offer-sdp");

        Assert.That(handler.Request!.RequestUri!.AbsolutePath, Is.EqualTo("/conversations/conversation%2F1/call/offer"));
        Assert.That(handler.Request.Headers.Authorization!.Parameter, Is.EqualTo("token"));
        Assert.That(handler.Body, Does.Contain("offer-sdp"));
    }

    [Test]
    public async Task Screen_share_signaling_uses_authenticated_start_and_stop_paths()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var signaling = new CallSignalingClient(client, new Uri("https://server.test/"), "token");
        var screen = (IScreenShareSignalingClient)signaling;

        await screen.ScreenStartAsync("conversation/1");
        Assert.That(handler.Request!.RequestUri!.AbsolutePath,
            Is.EqualTo("/conversations/conversation%2F1/call/screen/start"));
        Assert.That(handler.Request.Headers.Authorization!.Parameter, Is.EqualTo("token"));

        await screen.ScreenStopAsync("conversation/1");
        Assert.That(handler.Request!.RequestUri!.AbsolutePath,
            Is.EqualTo("/conversations/conversation%2F1/call/screen/stop"));
        Assert.That(handler.Request.Headers.Authorization!.Parameter, Is.EqualTo("token"));
    }

    [Test]
    public void Gateway_protocol_reads_the_call_id_from_call_events()
    {
        static CallSignalEventArgs Parse(object data)
        {
            string json = JsonSerializer.Serialize(new { t = "call.offer", eventId = "instance:1", d = data });
            Assert.That(GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame), Is.True);
            Assert.That(GatewayProtocol.TryParseCallSignal(frame!, out CallSignalEventArgs? call), Is.True);
            return call!;
        }

        Assert.Multiple(() =>
        {
            Assert.That(Parse(new { conversationId = "conversation-1", sdp = "sdp", callId = "6F9619FF-8B86-D011-B42D-00C04FC964FF" }).CallId,
                Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            Assert.That(Parse(new { conversationId = "conversation-1", sdp = "sdp" }).CallId, Is.Null);
            Assert.That(Parse(new { conversationId = "conversation-1", sdp = "sdp", callId = "not-a-call" }).CallId, Is.Null);
        });
    }

    [Test]
    public async Task Signaling_client_returns_the_issued_call_id_and_echoes_it()
    {
        var handler = new RecordingHandler(
            responseBody: "{\"conversationId\":\"00000000-0000-0000-0000-000000000001\",\"callId\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\"}");
        using var client = new HttpClient(handler);
        var signaling = new CallSignalingClient(client, new Uri("https://server.test/"), "token");

        string? issued = await signaling.RingAsync("conversation-1");
        await signaling.IceAsync("conversation-1", "candidate-1", callId: issued);

        Assert.Multiple(() =>
        {
            Assert.That(issued, Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            Assert.That(handler.Body, Does.Contain("\"callId\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\""));
        });
    }

    [Test]
    public void Signaling_client_reports_a_stale_call_generation_as_its_own_failure()
    {
        using var staleClient = new HttpClient(new RecordingHandler(
            HttpStatusCode.Conflict, "{\"error\":\"call_stale_generation\"}"));
        using var busyClient = new HttpClient(new RecordingHandler(
            HttpStatusCode.Conflict, "{\"error\":\"call_invalid_state\"}"));
        var stale = new CallSignalingClient(staleClient, new Uri("https://server.test/"), "token");
        var busy = new CallSignalingClient(busyClient, new Uri("https://server.test/"), "token");

        Assert.ThrowsAsync<CallStaleGenerationException>(
            () => stale.HangupAsync("conversation-1", "bye", callId: "6f9619ff-8b86-d011-b42d-00c04fc964ff"));
        Assert.ThrowsAsync<HttpRequestException>(
            () => busy.HangupAsync("conversation-1", "bye", callId: "6f9619ff-8b86-d011-b42d-00c04fc964ff"));
    }

    [Test]
    public async Task Signaling_client_never_reads_a_reply_past_the_size_cap()
    {
        string padding = new('x', 1024 * 1024);
        var ringBody = new CountingStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"callId\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\",\"pad\":\"" + padding + "\"}"));
        var iceBody = new CountingStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"callId\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\"}"));
        var conflictBody = new CountingStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"error\":\"call_stale_generation\",\"pad\":\"" + padding + "\"}"));
        using var ringClient = new HttpClient(new StreamingHandler(HttpStatusCode.OK, ringBody));
        using var iceClient = new HttpClient(new StreamingHandler(HttpStatusCode.OK, iceBody));
        using var conflictClient = new HttpClient(new StreamingHandler(HttpStatusCode.Conflict, conflictBody));
        var server = new Uri("https://server.test/");

        string? issued = await new CallSignalingClient(ringClient, server, "token").RingAsync("conversation-1");
        await new CallSignalingClient(iceClient, server, "token")
            .IceAsync("conversation-1", "candidate-1", callId: "6f9619ff-8b86-d011-b42d-00c04fc964ff");
        Exception? conflict = null;
        try
        {
            await new CallSignalingClient(conflictClient, server, "token")
                .HangupAsync("conversation-1", "bye", callId: "6f9619ff-8b86-d011-b42d-00c04fc964ff");
        }
        catch (Exception exception)
        {
            conflict = exception;
        }

        Assert.Multiple(() =>
        {
            Assert.That(issued, Is.Null, "oversized ring reply was parsed");
            Assert.That(ringBody.BytesRead, Is.LessThanOrEqualTo(4 * 1024 + 1), "ring reply read past the cap");
            Assert.That(iceBody.BytesRead, Is.Zero, "a reply that is not needed was read");
            Assert.That(conflict, Is.TypeOf<HttpRequestException>(), "oversized 409 reply was parsed");
            Assert.That(conflictBody.BytesRead, Is.LessThanOrEqualTo(4 * 1024 + 1), "409 reply read past the cap");
        });
    }

    [Test]
    public async Task Signaling_client_disposes_its_http_client_exactly_once()
    {
        var httpClient = new RecordingHttpClient(new RecordingHandler());
        var signaling = new CallSignalingClient(httpClient, new Uri("https://server.test/"), "token");

        await signaling.DisposeAsync();
        await signaling.DisposeAsync();

        Assert.That(httpClient.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Engine_can_initialize_without_audio_devices()
    {
        await using var engine = new RtcPeerEngine(useAudioDevices: false);

        await engine.InitializeAsync();

        Assert.That(engine.State, Is.EqualTo(RtcPeerState.New));
    }

    [Test]
    public async Task Caller_creates_offer_and_callee_returns_answer_without_audio_devices()
    {
        await using var caller = new RtcPeerEngine(useAudioDevices: false);
        await using var callee = new RtcPeerEngine(useAudioDevices: false);

        string offer = await caller.StartCall();
        string answer = await callee.AcceptOffer(offer);
        await caller.ApplyAnswer(answer);

        Assert.Multiple(() =>
        {
            Assert.That(offer, Does.StartWith("v=0"));
            Assert.That(answer, Does.StartWith("v=0"));
            Assert.That(caller.State, Is.EqualTo(RtcPeerState.Connecting));
            Assert.That(callee.State, Is.EqualTo(RtcPeerState.Connecting));
        });
    }

    [Test]
    public async Task Negotiated_call_starts_audio_and_hangup_stops_it_exactly_once()
    {
        var callerAudio = new RecordingRtcAudioEndpoint();
        var calleeAudio = new RecordingRtcAudioEndpoint();
        await using var caller = new RtcPeerEngine(() => callerAudio);
        await using var callee = new RtcPeerEngine(() => calleeAudio);

        string offer = await caller.StartCall();
        string answer = await callee.AcceptOffer(offer);
        await caller.ApplyAnswer(answer);

        caller.Mute();
        caller.Mute();
        caller.Unmute();
        caller.Unmute();
        await caller.Hangup();
        await caller.Hangup();

        Assert.Multiple(() =>
        {
            Assert.That(callerAudio.StartCount, Is.EqualTo(1));
            Assert.That(calleeAudio.StartCount, Is.EqualTo(1));
            Assert.That(callerAudio.SetFormatCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(calleeAudio.SetFormatCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(callerAudio.MuteCount, Is.EqualTo(1));
            Assert.That(callerAudio.UnmuteCount, Is.EqualTo(1));
            Assert.That(callerAudio.StopCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Concurrent_rtc_engine_disposals_join_actual_audio_cleanup()
    {
        var audio = new RecordingRtcAudioEndpoint(blockStop: true);
        var engine = new RtcPeerEngine(() => audio);
        await engine.StartCall();

        Task first = engine.DisposeAsync().AsTask();
        await audio.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = engine.DisposeAsync().AsTask();

        try
        {
            Assert.That(second.IsCompleted, Is.False);
        }
        finally
        {
            audio.ReleaseStop.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(audio.StopCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Call_coordinator_drives_outbound_answer_ice_and_hangup()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        await coordinator.StartAsync();
        transport.Raise(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp"));
        engine.RaiseIce(new RtcIceCandidate("candidate-1"));
        await coordinator.HangupAsync();

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions, Is.EqualTo(new[]
            {
                "ring", "offer:offer-sdp", "ice:candidate-1", "hangup:local hangup"
            }));
            Assert.That(engine.StartCount, Is.EqualTo(1));
            Assert.That(engine.AppliedAnswers, Is.EqualTo(new[] { "answer-sdp" }));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
        });
    }

    [Test]
    public async Task Call_coordinator_queues_remote_ice_until_the_caller_applies_the_answer()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        await coordinator.StartAsync();
        transport.Raise(new CallSignalEventArgs("call.ice", conversationId, candidate: "remote-ice"));

        Assert.That(engine.AddedCandidates, Is.Empty);

        transport.Raise(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp"));

        Assert.That(engine.AddedCandidates.Select(candidate => candidate.Candidate),
            Is.EqualTo(new[] { "remote-ice" }));
    }

    [Test]
    public async Task Remote_hangup_resets_candidate_queue_for_the_next_incoming_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-1"));
        await coordinator.AcceptAsync();
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId));

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-2"));
        transport.Raise(new CallSignalEventArgs("call.ice", conversationId, candidate: "unanswered-call-ice"));
        Assert.That(engine.AddedCandidates, Is.Empty);
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId));

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-3"));
        transport.Raise(new CallSignalEventArgs("call.ice", conversationId, candidate: "current-call-ice"));
        Assert.That(engine.AddedCandidates, Is.Empty);
        await coordinator.AcceptAsync();

        Assert.That(engine.AddedCandidates.Select(candidate => candidate.Candidate),
            Is.EqualTo(new[] { "current-call-ice" }));
    }

    [Test]
    public async Task Pending_candidates_are_bounded_and_reset_for_a_replacement_attempt()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        void Signal(string type, string? sdp = null, string? candidate = null) =>
            transport.Raise(new CallSignalEventArgs(type, conversationId, sdp: sdp, candidate: candidate));
        string[] Applied() => engine.AddedCandidates.Select(item => item.Candidate).ToArray();

        // Count bound
        Signal("call.ring");
        Signal("call.offer", sdp: "offer-1");
        for (int index = 0; index < 70; index++)
            Signal("call.ice", candidate: $"ice-{index}");
        await coordinator.AcceptAsync();
        Assert.That(Applied(), Is.EqualTo(Enumerable.Range(0, 64).Select(index => $"ice-{index}")));
        Signal("call.hangup");
        engine.AddedCandidates.Clear();

        // Byte bound
        string large = new('x', 20_000);
        Signal("call.ring");
        Signal("call.offer", sdp: "offer-2");
        Signal("call.ice", candidate: large);
        Signal("call.ice", candidate: new string('y', 20_000));
        Signal("call.ice", candidate: "small-ice");
        await coordinator.AcceptAsync();
        Assert.That(Applied(), Is.EqualTo(new[] { large, "small-ice" }));
        Signal("call.hangup");
        engine.AddedCandidates.Clear();

        // Replacement attempt with no hangup
        Signal("call.ring");
        Signal("call.offer", sdp: "offer-3");
        Signal("call.ice", candidate: "stale-ice");
        Signal("call.ring");
        Signal("call.offer", sdp: "offer-4");
        Signal("call.ice", candidate: "current-ice");
        await coordinator.AcceptAsync();
        Assert.That(Applied(), Is.EqualTo(new[] { "current-ice" }));
        Signal("call.hangup");
        engine.AddedCandidates.Clear();

        // Outgoing call does not inherit queued candidates
        Signal("call.ice", candidate: "idle-ice");
        await coordinator.StartAsync();
        Signal("call.answer", sdp: "answer-sdp");
        Assert.That(Applied(), Is.Empty);
    }

    [Test]
    public async Task Outgoing_call_echoes_the_issued_call_id_and_ignores_other_calls()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        const string other = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(issuedCallId: current);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        await coordinator.StartAsync();
        transport.Raise(new CallSignalEventArgs("call.answer", conversationId, sdp: "other-answer", callId: other));
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: other));
        transport.Raise(new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-sdp", callId: current));
        await coordinator.HangupAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.AppliedAnswers, Is.EqualTo(new[] { "answer-sdp" }));
            Assert.That(signaling.CallIds, Is.EqualTo(new[] { current, current }));
        });
    }

    [Test]
    public async Task Incoming_call_follows_its_ring_id_and_ends_only_on_its_own_hangup()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        const string other = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        await coordinator.AcceptAsync();
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "other-offer", callId: other));
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: other));
        int hangupsAfterOtherCall = engine.HangupCount;
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: current));

        Assert.Multiple(() =>
        {
            Assert.That(hangupsAfterOtherCall, Is.Zero, "a signal from another call ended this one");
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
            Assert.That(signaling.CallIds, Is.EqualTo(new[] { current }));
        });
    }

    [Test]
    public async Task Stale_call_generation_ends_the_call_without_a_remote_hangup()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(staleAnswer: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        Assert.ThrowsAsync<CallStaleGenerationException>(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions.Where(action => action.StartsWith("hangup:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Failed));
        });
    }

    [Test]
    public async Task A_new_ring_for_another_call_clears_the_old_offer_so_accept_needs_the_new_one()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        const string second = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer", callId: first));
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: second));

        Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.Empty, "the first call's offer was answered under the second call");
            Assert.That(signaling.Actions.Where(action => action.StartsWith("answer:", StringComparison.Ordinal)), Is.Empty);
        });
    }

    [Test]
    public async Task An_id_less_offer_does_not_replace_a_call_that_has_an_id()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "id-less-offer"));
        await coordinator.AcceptAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
            Assert.That(signaling.CallIds, Is.EqualTo(new[] { current }));
        });
    }

    [Test]
    public async Task A_candidate_raised_after_the_call_ended_is_not_sent()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(issuedCallId: current);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();
        await coordinator.HangupAsync();

        engine.RaiseIce(new RtcIceCandidate("late-candidate"));

        Assert.That(
            signaling.Actions.Where(action => action.StartsWith("ice:", StringComparison.Ordinal)),
            Is.Empty,
            "a late candidate must not be sent without its call id");
    }

    [Test]
    public async Task A_stale_candidate_for_the_current_call_ends_it_locally_without_a_remote_hangup()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(issuedCallId: current, staleIce: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        engine.RaiseIce(new RtcIceCandidate("candidate-for-the-current-call"));

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions, Does.Contain("ice:candidate-for-the-current-call"));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(signaling.Actions.Where(action => action.StartsWith("hangup:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Failed));
        });
    }

    [Test]
    public async Task A_stale_candidate_of_an_older_call_does_not_end_the_new_call()
    {
        await AssertOlderCandidateFailureLeavesTheNewCallAlive(new RecordingCallSignalingClient(
            issuedCallId: "11111111-1111-1111-1111-111111111111", blockIce: true, staleIce: true));
    }

    [Test]
    public async Task A_failed_candidate_of_an_older_call_does_not_end_the_new_call()
    {
        await AssertOlderCandidateFailureLeavesTheNewCallAlive(new RecordingCallSignalingClient(
            issuedCallId: "11111111-1111-1111-1111-111111111111", blockIce: true, failIce: true));
    }

    private static async Task AssertOlderCandidateFailureLeavesTheNewCallAlive(RecordingCallSignalingClient signaling)
    {
        const string newer = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        var states = new List<CallSessionState>();
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CallSessionPresentation.State))
                states.Add(session.State);
        };
        await coordinator.StartAsync();
        engine.RaiseIce(new RtcIceCandidate("candidate-of-the-first-call"));
        await coordinator.HangupAsync();
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: newer));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "newer-offer", callId: newer));
        await coordinator.AcceptAsync();

        signaling.ReleaseIce.TrySetResult();
        // Bounded wait for the background candidate task: a wrong implementation ends the newer call
        // within milliseconds, a correct one never changes the state, so this waits the full second.
        SpinWait.SpinUntil(() => states.Contains(CallSessionState.Failed), TimeSpan.FromSeconds(1));
        await coordinator.DisposeAsync();

        Assert.That(states, Does.Not.Contain(CallSessionState.Failed), "a candidate of the first call ended the newer call");
    }

    [Test]
    public async Task A_call_that_a_newer_ring_replaced_is_not_adopted_again_by_a_replay()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        const string second = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: second));

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: second));

        Assert.Multiple(() =>
        {
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_replayed_ring_of_an_ended_call_is_not_adopted_as_a_new_call()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        await coordinator.AcceptAsync();
        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: current));

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "replayed-offer", callId: current));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Ended));
            Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));
        });
    }

    [Test]
    public async Task The_same_offer_again_while_answered_does_not_reset_the_call()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        await coordinator.AcceptAsync();

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-again", callId: current));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Connecting));
            Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));
        });
    }

    [Test]
    public async Task A_coordinator_created_after_the_ring_ignores_other_calls_and_keeps_the_call_id()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        const string other = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "stale", callId: other));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Incoming));
            Assert.That(engine.HangupCount, Is.Zero);
        });

        await coordinator.AcceptAsync();

        Assert.That(signaling.CallIds, Is.EqualTo(new[] { current }));
    }

    [Test]
    public async Task A_newer_ring_cannot_make_accept_answer_an_offer_the_session_no_longer_holds()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        const string second = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        bool armed = false;
        bool injected = false;
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport,
            action =>
            {
                // Stand in for the adapter applying a newer ring on the UI thread just before
                // accept's first presentation write.
                if (armed && !injected)
                {
                    injected = true;
                    adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: second));
                }

                action();
            });
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer", callId: first));
        armed = true;

        try
        {
            await coordinator.AcceptAsync();
        }
        catch (InvalidOperationException)
        {
        }

        Assert.That(
            signaling.Actions.Where(action => action.StartsWith("answer:", StringComparison.Ordinal)),
            Is.Empty,
            "answered the old offer after a newer ring replaced it");
    }

    [Test]
    public async Task Accepting_with_no_offer_throws_without_failing_or_hanging_up_the_call()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));

        Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Incoming));
            Assert.That(signaling.Actions.Where(action => action.StartsWith("hangup:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(engine.HangupCount, Is.Zero);
        });
    }

    [Test]
    public async Task A_cancelled_accept_leaves_the_incoming_call_ready_to_be_accepted_again()
    {
        const string current = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: current));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: current));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => coordinator.AcceptAsync(cancelled.Token));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Incoming));
            Assert.That(session.Sdp, Is.EqualTo("offer-sdp"));
            Assert.That(engine.AcceptedOffers, Is.Empty);
        });

        await coordinator.AcceptAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
            Assert.That(signaling.CallIds, Is.EqualTo(new[] { current }));
        });
    }

    [Test]
    public async Task A_ring_that_arrives_after_the_click_does_not_end_the_accepted_call()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        const string second = "22222222-2222-2222-2222-222222222222";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        bool armed = false;
        bool injected = false;
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport,
            action =>
            {
                action();

                // Stand in for a newer ring being handled right after accept took the call and
                // before it got the operations lock.
                if (armed && !injected)
                {
                    injected = true;
                    transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: second));
                }
            });
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer", callId: first));
        armed = true;

        Assert.DoesNotThrowAsync(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "first-offer" }));
            Assert.That(signaling.CallIds, Is.EqualTo(new[] { first }));
            Assert.That(signaling.Actions.Where(action => action.StartsWith("hangup:", StringComparison.Ordinal)), Is.Empty);
        });
    }

    [Test]
    public async Task An_accept_queued_behind_a_hangup_does_not_accept_the_dead_call()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        bool armed = false;
        bool injected = false;
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport,
            action =>
            {
                action();

                // Stand in for the caller hanging up right after accept took the call and
                // before it got the operations lock.
                if (armed && !injected)
                {
                    injected = true;
                    transport.Raise(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: first));
                }
            });
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer", callId: first));
        armed = true;

        Assert.CatchAsync<InvalidOperationException>(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.Empty, "accepted the offer of a call that had already ended");
            Assert.That(signaling.Actions.Where(action => action.StartsWith("answer:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(signaling.Actions.Where(action => action.StartsWith("hangup:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
        });
    }

    [Test]
    public async Task A_second_accept_click_while_accepting_does_nothing()
    {
        const string first = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(blockAcceptOffer: true);
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: first));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp", callId: first));
        Task accepting = coordinator.AcceptAsync();
        await engine.AcceptOfferStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.DoesNotThrowAsync(() => coordinator.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            engine.ReleaseAcceptOffer.TrySetResult();
        }

        await accepting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
    }

    [Test]
    public async Task A_new_coordinator_does_not_follow_a_replayed_ring_of_an_ended_call()
    {
        const string ended = "11111111-1111-1111-1111-111111111111";
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.ring", conversationId, callId: ended));
        adapter.ApplyCallSignal(new CallSignalEventArgs("call.hangup", conversationId, reason: "peer_disconnected", callId: ended));
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.ring", conversationId, callId: ended));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "replayed-offer", callId: ended));
        await coordinator.HangupAsync();

        Assert.That(signaling.CallIds, Is.EqualTo(new string?[] { null }), "the coordinator followed the replayed call id");
    }

    [Test]
    public async Task A_late_ice_failure_of_an_id_less_call_does_not_end_the_next_call()
    {
        await AssertOlderCandidateFailureLeavesTheNewCallAlive(new RecordingCallSignalingClient(
            blockIce: true, failIce: true));
    }

    [Test]
    public async Task A_late_rtc_failure_of_an_older_call_does_not_end_the_next_call()
    {
        var engine = new RecordingRtcPeerEngine();
        await AssertLateFailureLeavesTheNextCallAlive(
            engine,
            (_, _) => engine.RaiseState(RtcPeerState.Failed));
    }

    [Test]
    public async Task A_late_signal_failure_of_an_older_call_does_not_end_the_next_call()
    {
        await AssertLateFailureLeavesTheNextCallAlive(
            new RecordingRtcPeerEngine(failApplyAnswer: true),
            (transport, conversationId) => transport.Raise(
                new CallSignalEventArgs("call.answer", conversationId, sdp: "answer-for-the-first-call")));
    }

    private static async Task AssertLateFailureLeavesTheNextCallAlive(
        RecordingRtcPeerEngine engine,
        Action<RecordingCallTransport, string> raiseLateFailure)
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockHangup: true);
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        var states = new List<CallSessionState>();
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CallSessionPresentation.State))
                states.Add(session.State);
        };
        await coordinator.StartAsync();
        Task hangingUp = coordinator.HangupAsync();
        await signaling.HangupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task starting = coordinator.StartAsync();
        raiseLateFailure(transport, conversationId);

        signaling.ReleaseHangup.TrySetResult();
        await hangingUp.WaitAsync(TimeSpan.FromSeconds(2));
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        // Bounded wait for the queued failure: a wrong implementation ends the next call within
        // milliseconds, a correct one never changes the state, so this waits the full second.
        SpinWait.SpinUntil(() => states.Contains(CallSessionState.Failed), TimeSpan.FromSeconds(1));
        await coordinator.DisposeAsync();

        Assert.That(states, Does.Not.Contain(CallSessionState.Failed), "a late failure of the first call ended the next call");
    }

    [Test]
    public async Task An_id_less_offer_during_accept_does_not_restart_the_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        bool armed = false;
        bool injected = false;
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport,
            action =>
            {
                action();

                // Stand in for another id-less offer being handled right after accept took the call
                // and before it got the operations lock.
                if (armed && !injected)
                {
                    injected = true;
                    transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "second-offer"));
                }
            });
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer"));
        armed = true;

        Assert.DoesNotThrowAsync(() => coordinator.AcceptAsync());

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "first-offer" }));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Connecting));
        });
    }

    [Test]
    public async Task An_id_less_offer_while_connected_does_not_restart_the_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        transport.Raise(new CallSignalEventArgs("call.ring", conversationId));
        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "first-offer"));
        await coordinator.AcceptAsync();
        engine.RaiseState(RtcPeerState.Connected);

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "second-offer"));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Connected));
            Assert.That(session.Sdp, Is.EqualTo("first-offer"));
        });
    }

    [Test]
    public async Task Call_coordinator_releases_media_when_hangup_signaling_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(failHangup: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        Assert.ThrowsAsync<HttpRequestException>(() => coordinator.HangupAsync());

        Assert.Multiple(() =>
        {
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
        });
    }

    [Test]
    public async Task Call_coordinator_ends_and_releases_media_when_offer_signaling_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(failOffer: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        Assert.ThrowsAsync<HttpRequestException>(() => coordinator.StartAsync());

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(engine.StartCount, Is.EqualTo(1));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(session.State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(session.Reason, Is.EqualTo("Call setup failed"));
        });
    }

    [Test]
    public void Outgoing_call_clears_the_previous_terminal_reason()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        session.End("Call setup failed");

        state.BeginOutgoingCall(conversationId);

        Assert.Multiple(() =>
        {
            Assert.That(session.State.ToString(), Is.EqualTo("Starting"));
            Assert.That(session.Reason, Is.Null);
        });
    }

    [Test]
    public async Task Call_coordinator_hangup_is_idempotent_for_one_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        await coordinator.HangupAsync();
        await coordinator.HangupAsync();

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions.Count(action => action == "hangup:local hangup"), Is.EqualTo(1));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Call_coordinator_accepts_incoming_offer_and_sends_answer()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);

        transport.Raise(new CallSignalEventArgs("call.offer", conversationId, sdp: "offer-sdp"));
        await coordinator.AcceptAsync();

        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
            Assert.That(signaling.Actions, Is.EqualTo(new[] { "answer:answer-sdp" }));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Connecting));
        });
    }

    [TestCase("call.ring", null)]
    [TestCase("call.offer", "echoed-own-offer")]
    public async Task Outgoing_call_ignores_echoed_incoming_signals(string eventType, string? sdp)
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state,
            conversationId,
            new RecordingCallSignalingClient(),
            new RecordingRtcPeerEngine(),
            transport);
        await coordinator.StartAsync();
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        var changedProperties = new List<string?>();
        session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        transport.Raise(new CallSignalEventArgs(eventType, conversationId, sdp: sdp));

        Assert.Multiple(() =>
        {
            Assert.That(session.State, Is.EqualTo(CallSessionState.Offering));
            Assert.That(session.Sdp, Is.Null);
            Assert.That(changedProperties, Is.Empty);
        });
    }

    [Test]
    public async Task Call_coordinator_fails_and_releases_media_when_answer_signaling_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(failAnswer: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        state.ApplyCallSignal("call.offer", conversationId, "offer-sdp", null, null);

        Assert.ThrowsAsync<HttpRequestException>(() => coordinator.AcceptAsync());

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(engine.AcceptedOffers, Is.EqualTo(new[] { "offer-sdp" }));
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(session.State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(session.Reason, Is.EqualTo("Call acceptance failed"));
        });
    }

    [Test]
    public async Task Call_coordinator_enters_connecting_while_accepting_a_remote_offer()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(blockAcceptOffer: true);
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        state.ApplyCallSignal("call.offer", conversationId, "offer-sdp", null, null);

        Task accepting = coordinator.AcceptAsync();
        await engine.AcceptOfferStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        string observedState = state.GetOrCreateCallSession(conversationId).State.ToString();
        engine.ReleaseAcceptOffer.TrySetResult();
        await accepting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(observedState, Is.EqualTo("Connecting"));
    }

    [Test]
    public async Task Concurrent_call_coordinator_disposals_join_actual_media_cleanup()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(blockDispose: true);
        var transport = new RecordingCallTransport();
        var coordinator = new CallCoordinator(state, conversationId, signaling, engine, transport);

        Task first = coordinator.DisposeAsync().AsTask();
        await engine.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = coordinator.DisposeAsync().AsTask();

        try
        {
            Assert.That(second.IsCompleted, Is.False);
        }
        finally
        {
            engine.ReleaseDispose.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(engine.DisposeCount, Is.EqualTo(1));
            Assert.That(signaling.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Call_coordinator_waits_for_an_inflight_ice_send_before_disposing_signaling()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockIce: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        var coordinator = new CallCoordinator(state, conversationId, signaling, engine, transport);

        engine.RaiseIce(new RtcIceCandidate("candidate-inflight"));
        await signaling.IceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = coordinator.DisposeAsync().AsTask();

        try
        {
            Assert.That(disposal.IsCompleted, Is.False);
        }
        finally
        {
            signaling.ReleaseIce.TrySetResult();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(signaling.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Coordinator_disposal_releases_media_before_inflight_ice_signaling_completes()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockIce: true);
        var engine = new RecordingRtcPeerEngine();
        var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, new RecordingCallTransport());
        await coordinator.StartAsync();
        engine.RaiseIce(new RtcIceCandidate("candidate-inflight-cleanup"));
        await signaling.IceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposal = coordinator.DisposeAsync().AsTask();
        bool mediaDisposalStarted;
        try
        {
            await engine.DisposeStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
            mediaDisposalStarted = true;
        }
        catch (TimeoutException)
        {
            mediaDisposalStarted = false;
        }
        finally
        {
            signaling.ReleaseIce.TrySetResult();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(mediaDisposalStarted, Is.True);
    }

    [Test]
    public async Task Call_coordinator_ends_the_call_when_ice_signaling_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(failIce: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        engine.RaiseIce(new RtcIceCandidate("candidate-failure"));
        await signaling.IceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bool cleanupCompleted;
        try
        {
            await engine.HangupStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
            cleanupCompleted = true;
        }
        catch (TimeoutException)
        {
            cleanupCompleted = false;
        }

        Assert.Multiple(() =>
        {
            Assert.That(cleanupCompleted, Is.True);
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(state.GetOrCreateCallSession(conversationId).Reason, Is.EqualTo("ICE signaling failed"));
        });
    }

    [Test]
    public async Task Call_coordinator_marks_the_session_failed_when_the_peer_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        engine.RaiseState(RtcPeerState.Failed);

        await signaling.HangupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions, Does.Contain("hangup:RTC peer failed"));
            Assert.That(session.State.ToString(), Is.EqualTo("Failed"));
            Assert.That(session.Reason, Is.EqualTo("RTC peer failed"));
        });
    }

    [TestCase(CallSessionState.Ended)]
    [TestCase(CallSessionState.Failed)]
    public async Task Terminal_call_state_ignores_a_delayed_connected_callback(CallSessionState terminalState)
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var engine = new RecordingRtcPeerEngine();
        await using var coordinator = new CallCoordinator(
            state,
            conversationId,
            new RecordingCallSignalingClient(),
            engine,
            new RecordingCallTransport());
        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        if (terminalState == CallSessionState.Failed)
            session.Fail("terminal failure");
        else
            session.SetLocalState(CallSessionState.Ended);

        engine.RaiseState(RtcPeerState.Connected);

        Assert.That(session.State, Is.EqualTo(terminalState));
    }

    [Test]
    public async Task User_hangup_notifies_remote_when_local_media_hangup_throws()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(failHangup: true);
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, new RecordingCallTransport());
        await coordinator.StartAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HangupAsync());

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions, Does.Contain("hangup:local hangup"));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
        });
    }

    [Test]
    public async Task Peer_failure_still_marks_the_session_failed_when_media_hangup_throws()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var engine = new RecordingRtcPeerEngine(failHangup: true);
        await using var coordinator = new CallCoordinator(
            state,
            conversationId,
            new RecordingCallSignalingClient(),
            engine,
            new RecordingCallTransport());
        await coordinator.StartAsync();

        engine.RaiseState(RtcPeerState.Failed);

        CallSessionPresentation session = state.GetOrCreateCallSession(conversationId);
        Assert.Multiple(() =>
        {
            Assert.That(engine.HangupCount, Is.EqualTo(1));
            Assert.That(session.State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(session.Reason, Is.EqualTo("RTC peer failed"));
        });
    }

    [Test]
    public async Task Call_coordinator_signals_hangup_when_disposed_during_an_active_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        await coordinator.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(signaling.Actions, Does.Contain("hangup:coordinator disposed"));
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
        });
    }

    [Test]
    public async Task Peer_failure_releases_media_before_remote_hangup_signaling_completes()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockHangup: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        engine.RaiseState(RtcPeerState.Failed);
        await signaling.HangupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(engine.HangupStarted.Task.IsCompleted, Is.True);
                Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Failed));
                Assert.That(state.GetOrCreateCallSession(conversationId).Reason, Is.EqualTo("RTC peer failed"));
            });
        }
        finally
        {
            signaling.ReleaseHangup.TrySetResult();
        }
    }

    [Test]
    public async Task User_hangup_releases_media_before_remote_hangup_signaling_completes()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockHangup: true);
        var engine = new RecordingRtcPeerEngine();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, new RecordingCallTransport());
        await coordinator.StartAsync();

        Task hangingUp = coordinator.HangupAsync();
        await signaling.HangupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(engine.HangupStarted.Task.IsCompleted, Is.True);
                Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Ended));
                Assert.That(hangingUp.IsCompleted, Is.False);
            });
        }
        finally
        {
            signaling.ReleaseHangup.TrySetResult();
        }

        await hangingUp.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Coordinator_disposal_releases_media_before_remote_hangup_signaling_completes()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient(blockHangup: true);
        var engine = new RecordingRtcPeerEngine();
        var transport = new RecordingCallTransport();
        var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        Task disposal = coordinator.DisposeAsync().AsTask();
        await signaling.HangupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.That(engine.DisposeStarted.Task.IsCompleted, Is.True);
        }
        finally
        {
            signaling.ReleaseHangup.TrySetResult();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Incoming_answer_failure_is_observed_and_ends_the_call()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(failApplyAnswer: true);
        var transport = new RecordingCallTransport();
        await using var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, transport);
        await coordinator.StartAsync();

        transport.Raise(new CallSignalEventArgs("call.answer", conversationId, sdp: "bad-answer"));
        await engine.ApplyAnswerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bool cleanupCompleted;
        try
        {
            await engine.HangupStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
            cleanupCompleted = true;
        }
        catch (TimeoutException)
        {
            cleanupCompleted = false;
        }

        Assert.Multiple(() =>
        {
            Assert.That(cleanupCompleted, Is.True);
            Assert.That(state.GetOrCreateCallSession(conversationId).State, Is.EqualTo(CallSessionState.Failed));
            Assert.That(state.GetOrCreateCallSession(conversationId).Reason, Is.EqualTo("Call signal handling failed"));
        });
    }

    [Test]
    public async Task Signaling_is_disposed_once_when_peer_disposal_fails()
    {
        PresentationState state = DemoData.Create();
        string conversationId = state.Conversations[0].Id.ToString();
        var signaling = new RecordingCallSignalingClient();
        var engine = new RecordingRtcPeerEngine(failDispose: true);
        var coordinator = new CallCoordinator(
            state, conversationId, signaling, engine, new RecordingCallTransport());

        Assert.That(
            async () => await coordinator.DisposeAsync(),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(
            async () => await coordinator.DisposeAsync(),
            Throws.TypeOf<InvalidOperationException>());

        Assert.Multiple(() =>
        {
            Assert.That(engine.DisposeCount, Is.EqualTo(1));
            Assert.That(signaling.DisposeCount, Is.EqualTo(1));
        });
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode) { RequestMessage = request };
            if (responseBody is not null)
                response.Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    private sealed class StreamingHandler(HttpStatusCode statusCode, System.IO.Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StreamContent(body)
            });
    }

    // A reply body with no known length that counts how many bytes the client reads.
    private sealed class CountingStream(byte[] content) : System.IO.Stream
    {
        private int _position;

        public int BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingHttpClient : HttpClient
    {
        public RecordingHttpClient(HttpMessageHandler handler) : base(handler)
        {
        }

        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingRtcAudioEndpoint : IRtcAudioEndpoint
    {
        private readonly bool _blockStop;

        public RecordingRtcAudioEndpoint(bool blockStop = false) => _blockStop = blockStop;

        public IReadOnlyList<AudioFormat> SourceFormats { get; } =
            new[] { new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU) };

        public event Action<uint, byte[]>? EncodedSample;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int SetFormatCount { get; private set; }
        public int MuteCount { get; private set; }
        public int UnmuteCount { get; private set; }
        public TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetFormat(AudioFormat format) => SetFormatCount++;

        public void Receive(RtcAudioPacket packet)
        {
        }

        public Task StartAsync()
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            StopCount++;
            StopStarted.TrySetResult();
            if (_blockStop)
                await ReleaseStop.Task;
        }

        public void Mute() => MuteCount++;

        public void Unmute() => UnmuteCount++;

        public void RaiseEncodedSample(uint duration, byte[] sample) =>
            EncodedSample?.Invoke(duration, sample);
    }

    private sealed class RecordingCallSignalingClient : ICallSignalingClient
    {
        private readonly bool _blockIce;
        private readonly bool _blockHangup;
        private readonly bool _failHangup;
        private readonly bool _failIce;
        private readonly bool _failOffer;
        private readonly bool _failAnswer;
        private readonly string? _issuedCallId;
        private readonly bool _staleAnswer;
        private readonly bool _staleIce;

        public RecordingCallSignalingClient(
            bool blockIce = false,
            bool blockHangup = false,
            bool failHangup = false,
            bool failIce = false,
            bool failOffer = false,
            bool failAnswer = false,
            string? issuedCallId = null,
            bool staleAnswer = false,
            bool staleIce = false)
        {
            _issuedCallId = issuedCallId;
            _staleAnswer = staleAnswer;
            _staleIce = staleIce;
            _blockIce = blockIce;
            _blockHangup = blockHangup;
            _failHangup = failHangup;
            _failIce = failIce;
            _failOffer = failOffer;
            _failAnswer = failAnswer;
        }

        public List<string> Actions { get; } = [];
        public List<string?> CallIds { get; } = [];
        public int DisposeCount { get; private set; }
        public TaskCompletionSource IceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIce { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HangupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHangup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> RingAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            Actions.Add("ring");
            return Task.FromResult(_issuedCallId);
        }

        public Task OfferAsync(string conversationId, string offerSdp, CancellationToken cancellationToken = default, string? callId = null)
        {
            Actions.Add($"offer:{offerSdp}");
            CallIds.Add(callId);
            if (_failOffer)
                throw new HttpRequestException("offer signaling unavailable");
            return Task.CompletedTask;
        }

        public Task AnswerAsync(string conversationId, string answerSdp, CancellationToken cancellationToken = default, string? callId = null)
        {
            Actions.Add($"answer:{answerSdp}");
            CallIds.Add(callId);
            if (_failAnswer)
                throw new HttpRequestException("answer signaling unavailable");
            if (_staleAnswer)
                throw new CallStaleGenerationException();
            return Task.CompletedTask;
        }

        public async Task IceAsync(string conversationId, string candidate, CancellationToken cancellationToken = default, string? callId = null)
        {
            Actions.Add($"ice:{candidate}");
            CallIds.Add(callId);
            IceStarted.TrySetResult();
            if (_blockIce)
                await ReleaseIce.Task.WaitAsync(cancellationToken);
            if (_staleIce)
                throw new CallStaleGenerationException();
            if (_failIce)
                throw new HttpRequestException("ICE signaling unavailable");
        }

        public async Task HangupAsync(string conversationId, string? reason = null, CancellationToken cancellationToken = default, string? callId = null)
        {
            Actions.Add($"hangup:{reason}");
            CallIds.Add(callId);
            HangupStarted.TrySetResult();
            if (_failHangup)
                throw new HttpRequestException("signaling unavailable");
            if (_blockHangup)
                await ReleaseHangup.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRtcPeerEngine : IRtcPeerEngine
    {
        private readonly bool _blockDispose;
        private readonly bool _failDispose;
        private readonly bool _failApplyAnswer;
        private readonly bool _blockAcceptOffer;
        private readonly bool _failHangup;

        public RecordingRtcPeerEngine(
            bool blockDispose = false,
            bool failDispose = false,
            bool failApplyAnswer = false,
            bool blockAcceptOffer = false,
            bool failHangup = false)
        {
            _blockDispose = blockDispose;
            _failDispose = failDispose;
            _failApplyAnswer = failApplyAnswer;
            _blockAcceptOffer = blockAcceptOffer;
            _failHangup = failHangup;
        }

        public event EventHandler<RtcPeerState>? StateChanged;
        public event EventHandler<RtcIceCandidate>? IceCandidateReady;

        public RtcPeerState State { get; private set; } = RtcPeerState.New;
        public int StartCount { get; private set; }
        public int HangupCount { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcceptOfferStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcceptOffer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HangupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ApplyAnswerStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> AcceptedOffers { get; } = [];
        public List<string> AppliedAnswers { get; } = [];
        public List<RtcIceCandidate> AddedCandidates { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> StartCall(CancellationToken cancellationToken = default)
        {
            StartCount++;
            State = RtcPeerState.Connecting;
            StateChanged?.Invoke(this, State);
            return Task.FromResult("offer-sdp");
        }

        public async Task<string> AcceptOffer(string offerSdp, CancellationToken cancellationToken = default)
        {
            AcceptedOffers.Add(offerSdp);
            AcceptOfferStarted.TrySetResult();
            if (_blockAcceptOffer)
                await ReleaseAcceptOffer.Task.WaitAsync(cancellationToken);
            return "answer-sdp";
        }

        public Task ApplyAnswer(string answerSdp, CancellationToken cancellationToken = default)
        {
            AppliedAnswers.Add(answerSdp);
            ApplyAnswerStarted.TrySetResult();
            if (_failApplyAnswer)
                throw new InvalidOperationException("remote answer rejected");
            return Task.CompletedTask;
        }

        public void AddIceCandidate(RtcIceCandidate candidate) => AddedCandidates.Add(candidate);

        public void Mute()
        {
        }

        public void Unmute()
        {
        }

        public Task Hangup(string reason = "local hangup")
        {
            HangupCount++;
            HangupStarted.TrySetResult();
            if (_failHangup)
                throw new InvalidOperationException("media hangup failed");
            State = RtcPeerState.Closed;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void RaiseIce(RtcIceCandidate candidate) => IceCandidateReady?.Invoke(this, candidate);

        public void RaiseState(RtcPeerState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (_failDispose)
                throw new InvalidOperationException("peer disposal failed");
            if (_blockDispose)
                await ReleaseDispose.Task;
        }
    }

    private sealed class RecordingCallTransport : IChatTransport
    {
#pragma warning disable CS0067
        public event EventHandler<MessageCreatedEventArgs>? MessageCreated;
        public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;
#pragma warning restore CS0067
        public event EventHandler<CallSignalEventArgs>? CallSignalReceived;

        public void Raise(CallSignalEventArgs signal) => CallSignalReceived?.Invoke(this, signal);

        public Task ConnectAsync(Uri server, string token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendAsync(string conversationId, string body, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetTypingAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
