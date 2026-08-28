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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
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

        public RecordingCallSignalingClient(
            bool blockIce = false,
            bool blockHangup = false,
            bool failHangup = false,
            bool failIce = false,
            bool failOffer = false,
            bool failAnswer = false)
        {
            _blockIce = blockIce;
            _blockHangup = blockHangup;
            _failHangup = failHangup;
            _failIce = failIce;
            _failOffer = failOffer;
            _failAnswer = failAnswer;
        }

        public List<string> Actions { get; } = [];
        public int DisposeCount { get; private set; }
        public TaskCompletionSource IceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIce { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HangupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHangup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RingAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            Actions.Add("ring");
            return Task.CompletedTask;
        }

        public Task OfferAsync(string conversationId, string offerSdp, CancellationToken cancellationToken = default)
        {
            Actions.Add($"offer:{offerSdp}");
            if (_failOffer)
                throw new HttpRequestException("offer signaling unavailable");
            return Task.CompletedTask;
        }

        public Task AnswerAsync(string conversationId, string answerSdp, CancellationToken cancellationToken = default)
        {
            Actions.Add($"answer:{answerSdp}");
            if (_failAnswer)
                throw new HttpRequestException("answer signaling unavailable");
            return Task.CompletedTask;
        }

        public async Task IceAsync(string conversationId, string candidate, CancellationToken cancellationToken = default)
        {
            Actions.Add($"ice:{candidate}");
            IceStarted.TrySetResult();
            if (_failIce)
                throw new HttpRequestException("ICE signaling unavailable");
            if (_blockIce)
                await ReleaseIce.Task.WaitAsync(cancellationToken);
        }

        public async Task HangupAsync(string conversationId, string? reason = null, CancellationToken cancellationToken = default)
        {
            Actions.Add($"hangup:{reason}");
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
